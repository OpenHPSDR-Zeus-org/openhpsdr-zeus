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
    }

    /// <summary>Current processing mode.</summary>
    public AudioProcessingMode Mode => _mode;

    /// <summary>True while the VST engine is live and routing TX audio.</summary>
    public bool EngineActive => _engine.IsActive;

    /// <summary>Resolve the engine exe without launching it; null = not installed.</summary>
    public static string? FindEngineExe() => VstEngineController.FindEngineExe();

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
    /// than just passing it through an empty host. Best-effort: a load failure is
    /// logged and the rest still load; the realtime path stays robust regardless.
    ///
    /// <para>v1 limitation: plugins load at their DEFAULT settings — per-knob
    /// state from the native chain is not transferred, and there is not yet a
    /// Zeus UI to open the external plugin's editor. Param/editor parity in VST
    /// mode is the next step (operator-facing → maintainer review).</para>
    /// </summary>
    private void LoadChainIntoEngine()
    {
        try
        {
            var byId = new Dictionary<string, ActivatedPlugin>(StringComparer.Ordinal);
            foreach (var p in _manager.Active) byId[p.Loaded.Manifest.Id] = p;

            int requested = 0;
            foreach (var id in _chainOrder.CurrentOrder) // ordered, parked excluded
            {
                if (!byId.TryGetValue(id, out var p)) continue;
                var vst3 = p.Loaded.Manifest.Audio?.Vst3Path;
                if (string.IsNullOrEmpty(vst3)) continue; // non-VST (native DSP) plugin

                var abs = Path.IsPathRooted(vst3)
                    ? vst3
                    : Path.Combine(p.Loaded.PluginDir, vst3);
                _engine.SendCommand(new { cmd = "add_plugin", file = abs, uid = "" });
                requested++;
            }
            _log.LogInformation(
                "VST mode: requested load of {Count} VST3 plugin(s) into the engine (default settings).",
                requested);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "VST mode chain load threw; engine runs with whatever loaded.");
        }
    }
}
