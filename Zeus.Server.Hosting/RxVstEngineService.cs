// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Zeus.Plugins.Contracts.Audio;
using Zeus.Plugins.Host;
using Zeus.Plugins.Host.Audio;

namespace Zeus.Server;

/// <summary>
/// Dedicated out-of-process VST engine for receive-side Audio Suite inserts.
/// TX VST mode owns a separate engine instance; this service loads only active
/// <c>rx.post-demod</c> VSTs so RX denoisers can run without sharing TX state.
/// </summary>
public sealed class RxVstEngineService : IHostedService, IAsyncDisposable
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(15);

    private readonly PluginManager _manager;
    private readonly RxChainOrderService _chainOrder;
    private readonly VstEngineController _engine;
    private readonly ILogger<RxVstEngineService> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _editorLock = new();
    private readonly bool _ownsEngine;
    private readonly Action<string> _stderrHandler;
    private readonly Action<string> _faultHandler;

    private Dictionary<string, int> _idToEngineSlot = new(StringComparer.Ordinal);
    private Dictionary<string, string> _fileToId = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _uidToId = new(StringComparer.Ordinal);
    private readonly HashSet<string> _openEditors = new(StringComparer.Ordinal);
    private Dictionary<string, string> _pluginStates = new(StringComparer.Ordinal);
    private int _activeVstCount;
    // One-shot "next sync must be a full load_chain" flag, guarded by _editorLock.
    // Set when plugin states are replaced wholesale (RX profile apply) so the new
    // voicing reaches the engine even when the chain order is unchanged.
    private bool _forceFullReload;

    public RxVstEngineService(
        PluginManager manager,
        RxChainOrderService chainOrder,
        ILogger<RxVstEngineService> log)
        : this(
            manager,
            chainOrder,
            new VstEngineController(maxFrames: 4096, rate: 48000, channels: 1),
            log,
            ownsEngine: true)
    {
    }

    internal RxVstEngineService(
        PluginManager manager,
        RxChainOrderService chainOrder,
        VstEngineController engine,
        ILogger<RxVstEngineService> log,
        bool ownsEngine = false)
    {
        _manager = manager;
        _chainOrder = chainOrder;
        _engine = engine;
        _log = log;
        _ownsEngine = ownsEngine;
        _stderrHandler = line => _log.LogDebug("rx-vst-engine: {Line}", line);
        _faultHandler = reason => _log.LogWarning("RX VST engine fault: {Reason}", reason);
        _engine.StdErr += _stderrHandler;
        _engine.EngineEvent += OnEngineEvent;
        // Self-heal: replay the RX chain when the supervisor relaunches a crashed
        // or hung engine, so recovered RX inserts come back loaded, not empty.
        _engine.Reconnected += OnEngineReconnected;
        _engine.Faulted += _faultHandler;
    }

    private void OnEngineReconnected()
    {
        _log.LogInformation("RX VST engine reconnected (restart #{Count}); replaying chain.", _engine.RestartCount);
        // The relaunched engine comes back EMPTY while our slot map is still stale-
        // populated from before the crash. Force a full load_chain so the incremental
        // diff can't see "no change" and leave the recovered engine with no plugins.
        lock (_editorLock) _forceFullReload = true;
        _ = SyncEngineAsync(_chainOrder.CurrentOrder, CancellationToken.None);
    }

    public bool EngineAvailable => VstEngineController.FindEngineExe() is not null;
    public bool EngineActive => _engine.IsActive && Volatile.Read(ref _activeVstCount) > 0;
    public int ActivePluginCount => Volatile.Read(ref _activeVstCount);
    public long DegradedBlocks => _engine.DegradedBlocks;

    public Task StartAsync(CancellationToken ct)
    {
        _chainOrder.OrderChanged += OnOrderChanged;
        _ = SyncEngineAsync(_chainOrder.CurrentOrder, ct);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _chainOrder.OrderChanged -= OnOrderChanged;
        _engine.Deactivate();
        return Task.CompletedTask;
    }

    private void OnOrderChanged(IReadOnlyList<string> activeOrder)
    {
        _ = SyncEngineAsync(activeOrder, CancellationToken.None);
    }

    /// <summary>
    /// Process through the RX engine when it is selected for active RX VSTs.
    /// Returns true when the RX engine route was selected, even if that block
    /// degraded to passthrough inside the robust VST bridge.
    /// </summary>
    public bool ProcessIfActive(
        ReadOnlySpan<float> input,
        Span<float> output,
        AudioBlockContext ctx)
    {
        if (!EngineActive) return false;
        _ = _engine.TryProcess(input, output, ctx);
        return true;
    }

    public bool HasEngineSlot(string pluginId)
    {
        lock (_editorLock) return _idToEngineSlot.ContainsKey(pluginId);
    }

    public EditorActionResult OpenEditor(string pluginId)
    {
        int slot;
        lock (_editorLock)
        {
            if (!_idToEngineSlot.TryGetValue(pluginId, out slot))
                return EditorActionResult.NotFound;
        }
        if (!_engine.IsActive) return EditorActionResult.NotLoaded;
        _engine.SendCommand(new { cmd = "open_editor", index = slot });
        lock (_editorLock) _openEditors.Add(pluginId);
        return EditorActionResult.Ok;
    }

    public EditorActionResult CloseEditor(string pluginId)
    {
        int slot;
        lock (_editorLock)
        {
            if (!_idToEngineSlot.TryGetValue(pluginId, out slot))
                return EditorActionResult.NotFound;
        }
        _engine.SendCommand(new { cmd = "close_editor", index = slot });
        lock (_editorLock) _openEditors.Remove(pluginId);
        return EditorActionResult.Ok;
    }

    public bool IsEditorOpen(string pluginId)
    {
        lock (_editorLock) return _openEditors.Contains(pluginId);
    }

    public async Task<IReadOnlyDictionary<string, string>> CaptureChainStatesAsync(
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        if (!_engine.IsActive) return new Dictionary<string, string>();

        var tcs = new TaskCompletionSource<IReadOnlyDictionary<string, string>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(JsonElement e)
        {
            try
            {
                if (!e.TryGetProperty("event", out var evt)
                    || evt.ValueKind != JsonValueKind.String
                    || evt.GetString() != "chain") return;
                if (!e.TryGetProperty("plugins", out var plugins)
                    || plugins.ValueKind != JsonValueKind.Array) return;

                Dictionary<string, string> fileToId;
                Dictionary<string, string> uidToId;
                lock (_editorLock)
                {
                    fileToId = _fileToId;
                    uidToId = _uidToId;
                }

                var states = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var plugin in plugins.EnumerateArray())
                {
                    var identifier = StrProp(plugin, "identifier");
                    string? zeusId = null;
                    if (identifier.Length > 0 && uidToId.TryGetValue(identifier, out var byUid))
                    {
                        zeusId = byUid;
                    }
                    else
                    {
                        var key = NormalizePath(StrProp(plugin, "file"));
                        if (key is not null && fileToId.TryGetValue(key, out var byFile))
                            zeusId = byFile;
                    }

                    var state = StrProp(plugin, "state");
                    if (zeusId is not null && state.Length > 0)
                        states[zeusId] = state;
                }
                tcs.TrySetResult(states);
            }
            catch
            {
                tcs.TrySetResult(new Dictionary<string, string>());
            }
        }

        _engine.EngineEvent += Handler;
        try
        {
            _engine.SendCommand(new { cmd = "get_chain" });
            return await tcs.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new Dictionary<string, string>();
        }
        finally
        {
            _engine.EngineEvent -= Handler;
        }
    }

    public void SetPluginStates(IReadOnlyDictionary<string, string> states)
    {
        lock (_editorLock)
        {
            _pluginStates = new Dictionary<string, string>(states, StringComparer.Ordinal);
            _forceFullReload = true;
        }
    }

    private async Task SyncEngineAsync(IReadOnlyList<string> activeOrder, CancellationToken ct)
    {
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var desired = BuildDesiredSlots(activeOrder);
            Volatile.Write(ref _activeVstCount, desired.Count);

            if (desired.Count == 0)
            {
                _engine.Deactivate();
                ClearEngineMaps();
                return;
            }

            bool wasActive = _engine.IsActive;
            var result = wasActive
                ? VstEngineStartResult.Started
                : await _engine.ActivateAsync(ReadyTimeout, ct).ConfigureAwait(false);

            if (result != VstEngineStartResult.Started)
            {
                _log.LogWarning(
                    "RX VST engine not active ({Result}); RX VST route falls back to passthrough/native chain.",
                    result);
                ClearEngineMaps();
                return;
            }

            bool forceFull;
            lock (_editorLock) forceFull = _forceFullReload;

            // Engine already running an edit (add / remove / reorder): diff against
            // its current slots and emit only the needed incremental commands so we
            // don't re-instantiate the whole RX rack and glitch live receive audio.
            if (wasActive && !forceFull)
            {
                List<string> current;
                lock (_editorLock)
                    current = _idToEngineSlot.OrderBy(kv => kv.Value).Select(kv => kv.Key).ToList();

                var cmds = VstChainDiff.Compute(current, desired);
                if (cmds is not null)
                {
                    if (cmds.Count == 0) return; // already in sync — nothing to push
                    RebuildEngineMaps(desired, full: false);
                    foreach (var cmd in cmds) _engine.SendCommand(cmd);
                    _log.LogInformation(
                        "RX VST engine: incremental chain update — {Ops} op(s), {Count} plugin(s).",
                        cmds.Count, desired.Count);
                    return;
                }
                // null → no usable current state; fall through to a full load.
            }

            lock (_editorLock) _forceFullReload = false;
            RebuildEngineMaps(desired, full: true);
            var slots = desired
                .Select(d => (object)new { file = d.File, identifier = d.Uid, state = d.State })
                .ToList();
            _engine.SendCommand(new { cmd = "load_chain", chain = slots });
            _log.LogInformation(
                "RX VST engine loaded {Count} receive VST3 plugin(s).",
                slots.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "RX VST engine sync threw; RX VST route falls back safely.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Build the desired RX engine chain: active <c>rx.*</c> VST3 inserts in chain
    /// order, each with its absolute path, descriptor uid, and last-known state.
    /// </summary>
    private List<VstChainDiff.DesiredSlot> BuildDesiredSlots(IReadOnlyList<string> activeOrder)
    {
        var active = new Dictionary<string, ActivatedPlugin>(StringComparer.Ordinal);
        foreach (var p in _manager.Active)
            active[p.Loaded.Manifest.Id] = p;

        Dictionary<string, string> savedStates;
        lock (_editorLock) savedStates = new(_pluginStates, StringComparer.Ordinal);

        var desired = new List<VstChainDiff.DesiredSlot>();
        foreach (var id in activeOrder)
        {
            if (!active.TryGetValue(id, out var plugin)) continue;
            var audio = plugin.Loaded.Manifest.Audio;
            if (audio is null
                || !audio.Slot.StartsWith("rx.", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(audio.Vst3Path))
            {
                continue;
            }

            var abs = Path.IsPathRooted(audio.Vst3Path)
                ? audio.Vst3Path
                : Path.Combine(plugin.Loaded.PluginDir, audio.Vst3Path);
            var uid = audio.Vst3Uid ?? string.Empty;
            var state = savedStates.GetValueOrDefault(id, string.Empty);
            desired.Add(new VstChainDiff.DesiredSlot(id, abs, uid, state));
        }
        return desired;
    }

    /// <summary>
    /// Rebuild the id→slot / file→id / uid→id maps from the desired chain's final
    /// shape. A full reload clears the optimistic open-editor set (the engine closes
    /// all editors on load_chain); an incremental push keeps the survivors' editors.
    /// </summary>
    private void RebuildEngineMaps(IReadOnlyList<VstChainDiff.DesiredSlot> desired, bool full)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        var fileToId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var uidToId = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < desired.Count; i++)
        {
            var d = desired[i];
            map[d.Id] = i;
            var key = NormalizePath(d.File);
            if (key is not null) fileToId[key] = d.Id;
            if (d.Uid.Length > 0) uidToId[d.Uid] = d.Id;
        }
        lock (_editorLock)
        {
            _idToEngineSlot = map;
            _fileToId = fileToId;
            _uidToId = uidToId;
            if (full) _openEditors.Clear();
            else _openEditors.IntersectWith(map.Keys);
        }
    }

    private void OnEngineEvent(JsonElement e)
    {
        try
        {
            if (!e.TryGetProperty("event", out var evt)
                || evt.ValueKind != JsonValueKind.String
                || evt.GetString() != "chain") return;
            if (!e.TryGetProperty("plugins", out var plugins)
                || plugins.ValueKind != JsonValueKind.Array) return;

            Dictionary<string, string> fileToId;
            Dictionary<string, string> uidToId;
            lock (_editorLock)
            {
                fileToId = _fileToId;
                uidToId = _uidToId;
            }

            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            var states = new Dictionary<string, string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var plugin in plugins.EnumerateArray())
            {
                var identifier = StrProp(plugin, "identifier");
                string? zeusId = null;
                if (identifier.Length > 0 && uidToId.TryGetValue(identifier, out var byUid))
                {
                    zeusId = byUid;
                }
                else
                {
                    var key = NormalizePath(StrProp(plugin, "file"));
                    if (key is not null && fileToId.TryGetValue(key, out var byFile))
                        zeusId = byFile;
                }

                if (zeusId is not null)
                {
                    map[zeusId] = index;
                    var state = StrProp(plugin, "state");
                    if (state.Length > 0) states[zeusId] = state;
                }
                index++;
            }

            lock (_editorLock)
            {
                _idToEngineSlot = map;
                foreach (var kv in states) _pluginStates[kv.Key] = kv.Value;
            }
        }
        catch
        {
            // Best effort; the provisional id->slot map remains usable.
        }
    }

    private void ClearEngineMaps()
    {
        lock (_editorLock)
        {
            _idToEngineSlot = new Dictionary<string, int>(StringComparer.Ordinal);
            _fileToId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _uidToId = new Dictionary<string, string>(StringComparer.Ordinal);
            _openEditors.Clear();
        }
    }

    private static string StrProp(JsonElement o, string key) =>
        o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    private static string? NormalizePath(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return null;
        try { return Path.GetFullPath(p).TrimEnd('\\', '/'); }
        catch { return p; }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _engine.StdErr -= _stderrHandler;
        _engine.EngineEvent -= OnEngineEvent;
        _engine.Reconnected -= OnEngineReconnected;
        _engine.Faulted -= _faultHandler;
        if (_ownsEngine)
            await _engine.DisposeAsync().ConfigureAwait(false);
    }
}
