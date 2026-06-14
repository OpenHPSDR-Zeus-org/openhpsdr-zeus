// SPDX-License-Identifier: GPL-2.0-or-later
using Microsoft.Extensions.Hosting;
using Zeus.Plugins.Host;
using Zeus.Plugins.Host.Audio;

namespace Zeus.Server;

/// <summary>
/// Owns the Audio Suite processing-mode selector: <see cref="AudioProcessingMode.Native"/>
/// (Brian's in-process plugin chain — the untouched default) versus
/// <see cref="AudioProcessingMode.Vst"/> (the out-of-process VST engine). The two
/// routes are mutually exclusive; <see cref="AudioPluginBridge"/> reads
/// <see cref="VstEngineController.IsActive"/> on the realtime TX thread to pick one.
///
/// <para><b>Default on first run:</b> <see cref="AudioProcessingMode.Native"/>. A
/// brand-new operator's TX path is byte-identical to a build with no VST mode at
/// all — the realtime tap never touches the engine until the operator explicitly
/// opts in.</para>
///
/// <para><b>Robust path:</b> selecting VST launches the external engine and arms
/// the bridge; if the engine isn't installed or never handshakes, the controller
/// stays inactive and TX audio falls through clean. The operator's persisted mode
/// choice is kept regardless — flipping the engine on later (install + retry)
/// honours it. Mode persists via <see cref="AudioProcessingModeStore"/>.</para>
///
/// <para><b>No wire-format change:</b> unlike master bypass, mode changes are NOT
/// broadcast over the hub (that would add a Zeus.Contracts frame — red-light).
/// Clients read the mode via <c>GET /api/audio-suite/processing-mode</c>.</para>
/// </summary>
public sealed class AudioProcessingModeService : IHostedService
{
    /// <summary>How long to wait for the engine's <c>ready</c> handshake on activation.</summary>
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(15);

    private readonly AudioProcessingModeStore _store;
    private readonly VstEngineController _engine;
    private readonly PluginManager _manager;
    private readonly ChainOrderService _chainOrder;
    private readonly ILogger<AudioProcessingModeService> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AudioProcessingMode _mode = AudioProcessingMode.Native;

    // Out-of-process editor routing (host-consolidation step 1/2). When the
    // engine is active, the Audio Suite editor endpoints route here instead of
    // the in-process zeus-vst-bridge, so the editor is hosted crash-isolated in
    // the engine process — the SAME instance that is processing audio. The map
    // is rebuilt every time the chain is pushed to the engine (load_chain order
    // == engine slot index). Open state is tracked optimistically on this side
    // (Zeus-driven open/close); the engine owns the actual windows.
    private readonly object _editorLock = new();
    private Dictionary<string, int> _idToEngineSlot = new(StringComparer.Ordinal);
    // Normalised VST3 file path -> Zeus plugin id, from the last chain push.
    // The engine compacts slots when a plugin fails to load, so the authoritative
    // id->slot map is rebuilt from each `chain` event by matching on file path
    // (load-order is only a provisional default until that event arrives).
    private Dictionary<string, string> _fileToId = new(StringComparer.OrdinalIgnoreCase);
    // Engine plugin uid -> Zeus plugin id. Preferred over _fileToId: a "shell"
    // file (e.g. Waves WaveShell) hosts many plugins sharing ONE file, so file
    // alone can't disambiguate which slot is which — the uid does.
    private Dictionary<string, string> _uidToId = new(StringComparer.Ordinal);
    private readonly HashSet<string> _openEditors = new(StringComparer.Ordinal);

    public AudioProcessingModeService(
        AudioProcessingModeStore store,
        VstEngineController engine,
        PluginManager manager,
        ChainOrderService chainOrder,
        ILogger<AudioProcessingModeService> log)
    {
        _store = store;
        _engine = engine;
        _manager = manager;
        _chainOrder = chainOrder;
        _log = log;
        _engine.StdErr += line => _log.LogDebug("vst-engine: {Line}", line);
        _engine.EngineEvent += OnEngineEvent;
        // Keep the live engine's loaded chain in sync with operator edits made
        // AFTER activation. LoadChainIntoEngine() runs once on activation; without
        // this subscription a VST added/removed/reordered later never reaches the
        // engine (its editor 404s with "No such plugin in the TX chain" and no
        // audio flows). OrderChanged fires only on deliberate chain edits
        // (add/remove/reorder/park) — NOT on the bulk OnPluginAttached storm from a
        // directory scan (those land parked), so a scan does not thrash the engine.
        _chainOrder.OrderChanged += OnChainOrderChanged;
    }

    /// <summary>
    /// Re-push the Audio Suite chain to the external engine when the operator
    /// edits it while the engine is live. No-op unless the engine is active
    /// (Native mode, or VST mode with no engine, leaves the realtime tap clean).
    /// </summary>
    private void OnChainOrderChanged(IReadOnlyList<string> _)
    {
        if (_engine.IsActive) LoadChainIntoEngine();
    }

    /// <summary>
    /// Engine control-plane events (control thread). We watch <c>chain</c> to keep
    /// the id-&gt;slot map aligned with the engine's ACTUAL slot indices — the
    /// engine compacts slots past any plugin that failed to load, so load-order
    /// position alone is unreliable. Matches each engine slot to a Zeus plugin id
    /// by <c>uid</c> first (the only way to disambiguate shell sub-plugins that
    /// share a file), falling back to <c>file</c> via <see cref="_fileToId"/>.
    /// </summary>
    private void OnEngineEvent(System.Text.Json.JsonElement e)
    {
        try
        {
            if (!e.TryGetProperty("event", out var evt)
                || evt.ValueKind != System.Text.Json.JsonValueKind.String) return;
            if (evt.GetString() != "chain") return;
            if (!e.TryGetProperty("plugins", out var plugins)
                || plugins.ValueKind != System.Text.Json.JsonValueKind.Array) return;

            Dictionary<string, string> fileToId;
            Dictionary<string, string> uidToId;
            lock (_editorLock) { fileToId = _fileToId; uidToId = _uidToId; }

            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            int i = 0;
            foreach (var pl in plugins.EnumerateArray())
            {
                // Prefer uid (unambiguous for shell sub-plugins); fall back to file.
                if (pl.TryGetProperty("uid", out var u)
                    && u.ValueKind == System.Text.Json.JsonValueKind.String
                    && uidToId.TryGetValue(u.GetString() ?? string.Empty, out var byUid))
                {
                    map[byUid] = i;
                }
                else if (pl.TryGetProperty("file", out var f)
                    && f.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var key = NormalizePath(f.GetString());
                    if (key is not null && fileToId.TryGetValue(key, out var byFile))
                        map[byFile] = i;
                }
                i++;
            }
            lock (_editorLock) _idToEngineSlot = map;
        }
        catch { /* event parsing is best-effort; stale map self-heals next event */ }
    }

    private static string? NormalizePath(string? p)
    {
        if (string.IsNullOrEmpty(p)) return null;
        try { return Path.GetFullPath(p).TrimEnd('\\', '/'); }
        catch { return p; }
    }

    /// <summary>Current processing mode.</summary>
    public AudioProcessingMode Mode => _mode;

    /// <summary>True while the VST engine is live and routing TX audio.</summary>
    public bool EngineActive => _engine.IsActive;

    /// <summary>Resolve the engine exe without launching it; null = not installed.</summary>
    public static string? FindEngineExe() => VstEngineController.FindEngineExe();

    /// <summary>
    /// Open the out-of-process engine's native editor window for the chain
    /// plugin with the given id. Only meaningful while <see cref="EngineActive"/>
    /// — the endpoint routes here in that case and to the in-process bridge
    /// otherwise. The window is owned by the engine process (crash-isolated).
    /// </summary>
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

    /// <summary>Close the engine's editor window for the given chain plugin id.</summary>
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

    /// <summary>
    /// Whether the engine editor for the given id is believed open. Tracked
    /// optimistically from Zeus-driven open/close; if the operator closes the
    /// native window directly, this can read stale-open until the next toggle.
    /// </summary>
    public bool IsEditorOpen(string pluginId)
    {
        lock (_editorLock) return _openEditors.Contains(pluginId);
    }

    public Task StartAsync(CancellationToken ct)
    {
        var persisted = _store.GetMode();
        _mode = persisted ?? AudioProcessingMode.Native;

        _log.LogInformation(
            "AudioProcessingModeService initialised; mode = {Mode}{Source}",
            _mode, persisted is null ? " (first run, default Native)" : " (persisted)");

        // If the operator left us in VST mode, bring the engine up in the
        // background — never block server startup on a 15 s engine handshake.
        if (_mode == AudioProcessingMode.Vst)
            _ = ActivateEngineAsync();

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        // Take the realtime tap offline before the host tears down further.
        _engine.Deactivate();
        await Task.CompletedTask;
    }

    /// <summary>
    /// Set the processing mode. Persists the choice, then activates or
    /// deactivates the VST engine to match. Idempotent — no work if unchanged.
    /// Returns the (possibly unchanged) current mode.
    /// </summary>
    public async Task<AudioProcessingMode> SetModeAsync(AudioProcessingMode mode, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_mode == mode && (mode == AudioProcessingMode.Native || _engine.IsActive))
                return _mode;

            // Persist BEFORE acting so a crash mid-switch leaves disk reflecting
            // the operator's intent on next boot.
            try { _store.SetMode(mode); }
            catch (Exception ex) { _log.LogWarning(ex, "AudioProcessingModeService persist threw"); }

            _mode = mode;

            if (mode == AudioProcessingMode.Vst)
                await ActivateEngineAsync(ct).ConfigureAwait(false);
            else
                _engine.Deactivate();

            _log.LogInformation("Audio suite processing mode set to {Mode}", mode);
            return _mode;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ActivateEngineAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _engine.ActivateAsync(ReadyTimeout, ct).ConfigureAwait(false);
            switch (result)
            {
                case VstEngineStartResult.Started:
                    _log.LogInformation("VST engine active ({Path})", _engine.ResolvedEnginePath);
                    LoadChainIntoEngine();
                    break;
                case VstEngineStartResult.EngineNotFound:
                    _log.LogWarning(
                        "VST mode selected but no VSTHost engine is installed — TX audio passes through clean. Install from https://github.com/KlayaR/VSTHost.");
                    break;
                case VstEngineStartResult.PlatformUnsupported:
                    _log.LogWarning("VST mode is Windows-only; TX audio passes through clean on this platform.");
                    break;
                default:
                    _log.LogWarning("VST engine did not come up ({Result}); TX audio passes through clean.", result);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "VST engine activation threw; TX audio passes through clean.");
        }
    }

    /// <summary>
    /// Mirror the operator's active Audio Suite VST3 plugins into the external
    /// engine, in chain order, so VST mode actually processes their audio rather
    /// than passing it through an empty host. Uses a single <c>load_chain</c> so
    /// the engine adds the plugins in <em>slot order</em> (each slot's index is
    /// its position in this list) — that determinism is what lets us map a
    /// plugin id to its engine slot for editor open/close. Best-effort: a load
    /// failure is logged engine-side and the realtime path stays robust.
    ///
    /// <para>v1 limitation: plugins load at their DEFAULT settings — per-knob
    /// state from the native chain is not transferred yet (the engine's
    /// <c>load_chain</c> accepts a per-slot <c>state</c>/<c>parameters</c> blob;
    /// wiring Zeus's saved state into it is the next step).</para>
    /// </summary>
    private void LoadChainIntoEngine()
    {
        try
        {
            var byId = new Dictionary<string, ActivatedPlugin>(StringComparer.Ordinal);
            foreach (var p in _manager.Active) byId[p.Loaded.Manifest.Id] = p;

            var slots = new List<object>();
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            var fileToId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var uidToId = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var id in _chainOrder.CurrentOrder) // ordered, parked excluded
            {
                if (!byId.TryGetValue(id, out var p)) continue;
                var vst3 = p.Loaded.Manifest.Audio?.Vst3Path;
                if (string.IsNullOrEmpty(vst3)) continue; // non-VST (native DSP) plugin

                var abs = Path.IsPathRooted(vst3)
                    ? vst3
                    : Path.Combine(p.Loaded.PluginDir, vst3);
                // uid selects ONE plugin from a shell file; "" => engine loads the
                // single/first plugin in the file (describeFile fallback).
                var uid = p.Loaded.Manifest.Audio?.Vst3Uid ?? string.Empty;
                map[id] = slots.Count; // provisional: engine slot == load_chain position
                var key = NormalizePath(abs);
                if (key is not null) fileToId[key] = id;
                if (uid.Length > 0) uidToId[uid] = id;
                slots.Add(new { file = abs, identifier = uid });
            }

            lock (_editorLock)
            {
                _idToEngineSlot = map;
                _fileToId = fileToId;
                _uidToId = uidToId;
                _openEditors.Clear(); // the engine closes all editors on load_chain
            }

            _engine.SendCommand(new { cmd = "load_chain", chain = slots });
            _log.LogInformation(
                "VST mode: loaded {Count} VST3 plugin(s) into the engine via load_chain.",
                slots.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "VST mode chain load threw; engine runs with whatever loaded.");
        }
    }
}
