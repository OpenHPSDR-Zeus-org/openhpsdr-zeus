// SPDX-License-Identifier: GPL-2.0-or-later
//
// AudioPluginBridge — wires PluginManager's audio-bearing plugins into
// WdspDspEngine's realtime TX seam via Zeus.Plugins.Host.AudioChain.
//
// The chain itself (AudioChain) is realtime-safe and tested in
// isolation under Zeus.Plugins.Host.Tests. This file is the
// integration glue: it subscribes to PluginManager activation events,
// adopts each audio-bearing plugin into a free slot, and re-installs
// the WDSP delegate whenever DspPipelineService swaps engines.
//
// The whole bridge is a no-op when no plugins implement IAudioPlugin
// or declare audio.vst3Path in their manifest.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zeus.Dsp;
using Zeus.Dsp.Wdsp;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Audio;
using Zeus.Plugins.Contracts.Extensions;
using Zeus.Plugins.Host;
using Zeus.Plugins.Host.Audio;

namespace Zeus.Server;

/// <summary>Outcome of an Audio Suite VST editor open/close request.</summary>
public enum EditorActionResult
{
    /// <summary>The editor was opened/closed (or is the requested state).</summary>
    Ok,
    /// <summary>No plugin with that id is attached to the TX chain.</summary>
    NotFound,
    /// <summary>The plugin exists but is not a hosted VST (no native editor).</summary>
    NotAVst,
    /// <summary>The VST isn't natively loaded (native load gated off or load failed) — nothing to show.</summary>
    NotLoaded,
    /// <summary>The bridge reported a failure opening the editor (e.g. unsupported on this platform).</summary>
    Failed,
}

public sealed class AudioPluginBridge : IHostedService, IAsyncDisposable
{
    private const int TxAudioHostBlockSize = 1024;
    private const int RxAudioHostBlockSize = 2048;

    private readonly PluginManager _manager;
    private readonly DspPipelineService _pipeline;
    private readonly IVstBridgeNative _vstBridge;
    // Second native backend for macOS Audio Units (audio.format == "au").
    // Selected per-manifest in ResolveAudioPlugin; the VST3 backend
    // (_vstBridge) is untouched. Lazily created so a tests/Win/Linux process
    // that never loads an AU pays nothing (and on non-macOS the AU dylib is
    // simply absent → AuBridgeNative degrades to passthrough).
    private IVstBridgeNative? _auBridge;
    private readonly Func<bool> _isMoxOn;
    private readonly Func<bool> _isMonitorOn;
    private readonly Func<bool> _isTciTxAudioActive;
    private int _remoteBypassLogCount; // for one-time diagnostic log during verification/on-air testing
    private readonly ChainOrderService? _chainOrder;
    private readonly RxChainOrderService? _rxChainOrder;
    // Out-of-process VST engine route (opt-in "VST" processing mode). Null in
    // tests / when no controller is injected. When its IsActive gate is true the
    // realtime TX path routes through the external engine instead of _chain;
    // when false the engine is never touched and TX is byte-identical to native.
    private readonly VstEngineController? _vstEngine;
    private readonly ILogger<AudioPluginBridge> _log;
    private readonly AudioChain _chain = new();
    private readonly Dictionary<string, int> _idToSlot = new();
    private readonly Dictionary<string, IAudioPlugin> _idToPlugin = new();
    // TX plugins whose native resources have actually been loaded
    // (InitializeAudioAsync ran). A PARKED plugin is registered in
    // _idToPlugin but stays out of this set until the operator un-parks it
    // into the live chain — native instantiation is DEFERRED, exactly like
    // the receive path's _rxInitializedIds. This is what stops a scan from
    // launching every scanned plugin (the whole macOS AU registry, in the AU
    // scanner's case) the moment it registers them.
    private readonly HashSet<string> _txInitializedIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _txIdToSlotName = new();
    // RX audio insert chain — plugins whose manifest slot is rx.* (e.g.
    // rx.post-demod, a CW SCAF audio filter). Kept ENTIRELY separate from the
    // TX _chain: separate plugin instances, separate IIR state, and a separate
    // realtime runner (ProcessRxBlock, installed on DspPipelineService rather
    // than the WDSP TX seam). ChainOrderService governs only the TX chain; the
    // RX chain uses a separate RX order/membership service. The pipeline handler
    // is installed only while at least one RX plugin is active, so scanned RX
    // VSTs can sit parked without changing receive audio.
    private readonly AudioChain _rxChain = new();
    private readonly Dictionary<string, int> _rxIdToSlot = new();
    private readonly Dictionary<string, IAudioPlugin> _rxIdToPlugin = new();
    private readonly Dictionary<string, string> _rxIdToSlotName = new();
    private readonly HashSet<string> _rxInitializedIds = new(StringComparer.Ordinal);
    private readonly RxVstEngineService? _rxVstEngine;
    private readonly object _lock = new();
    private Action<IReadOnlyList<string>>? _orderChangedHandler;
    private Action<IReadOnlyList<string>>? _rxOrderChangedHandler;
    // Pre-MOX preview gate — true ONLY when a) at least one IAudioPlugin
    // is attached AND b) the live DSP engine is WdspDspEngine (the
    // synthetic engine has no realtime TX path so a preview against it
    // would be meaningless). Volatile single-reader on the miniaudio
    // capture thread; written from the control-thread lifecycle paths.
    private volatile bool _previewEnabled;
    private bool _engineIsWdsp;
    // Lock-free gate guarding the out-of-process engine's single SHM block. The
    // on-air TX thread (Process, MOX on) and the pre-MOX preview thread
    // (ProcessLivePreview, MOX off) are mutually exclusive in time except for the
    // microsecond window at a MOX edge; this CAS guarantees they never drive the
    // shared block concurrently there. 0 = free, 1 = in use.
    private int _engineGate;
    // Master IN/OUT chain meters for VST mode. The native chain populates its own
    // _chain.Meters during _chain.Process; when audio routes through the engine
    // instead, _chain.Process never runs, so we sample the block peaks here (same
    // instantaneous abs-peak ballistics as AudioChain.BlockPeak) to keep the
    // Audio Suite's IN/OUT meters live. Volatile single-writer (realtime thread),
    // single-reader (the /chain/meters poll).
    private volatile float _engineInPeak;
    private volatile float _engineOutPeak;
    private volatile float _rxInPeak;
    private volatile float _rxOutPeak;

    // ── VST-engine TX-audio liveness sampler (diagnostics only) ──────────────
    // Off-realtime timer that watches for the one engine failure the seq-based
    // health model is blind to: the engine stays alive AND responsive
    // (outSeq==inSeq promptly, so DegradedBlocks never climbs and the process
    // never exits) yet returns dead samples (silence / NaN sanitized to silence)
    // after sustained TX. The supervisor can't see it, so the operator transmits
    // silence until a manual profile change forces a fresh load_chain. This timer
    // never touches the realtime path — it only reads volatile peak fields and the
    // controller's diagnostic counters off a threadpool thread. See zeus-umt6.
    private System.Threading.Timer? _livenessTimer;
    private long _lastEngineDegraded;
    private int _deadOutputStreak;
    private int _livenessHeartbeat;
    private const int LivenessIntervalMs = 2000;
    // Input clearly above the noise floor (~-40 dBFS) but output effectively
    // silent (~-80 dBFS) — the dead-output signature.
    private const float LivenessInPresentPeak = 0.01f;
    private const float LivenessOutSilentPeak = 1e-4f;
    // Require the signature to persist before warning, so a brief speech gap or a
    // MOX edge can't false-trip it (~6 s at the 2 s interval).
    private const int LivenessWarnAfterTicks = 3;
    // Re-log cadence (heartbeat when healthy, repeat-warn when wedged): ~10 s.
    private const int LivenessHeartbeatTicks = 5;

    // ── VST-engine TX auto-recovery escalation (Phase 2, zeus-umt6) ──────────
    // Once the dead-output signature is confirmed, self-heal instead of waiting for
    // a manual profile change: Tier-1 re-pushes the chain into the live engine
    // (re-instantiates plugins — the manual-profile-change fix); if it's STILL dead
    // a grace window later, Tier-2 recycles the engine process. At most one of each
    // per episode; both reset when output recovers (or TX/engine drops). Default on
    // — the operator chose auto-recovery; the off-switch is for tests/diagnostics.
    internal bool AutoRecoverTxDeadOutput { get; set; } = true;
    private bool _reloadAttempted;
    private bool _recycleAttempted;
    // Ticks AFTER the warn point to let a Tier-1 reload take hold before escalating
    // to a Tier-2 recycle (~6 s at the 2 s interval).
    private const int LivenessEscalateAfterTicks = 3;

    public AudioPluginBridge(
        PluginManager manager,
        DspPipelineService pipeline,
        TxService tx,
        IPreviewAudioSink preview,
        ChainOrderService chainOrder,
        RxChainOrderService rxChainOrder,
        ILogger<AudioPluginBridge> log,
        TxAudioIngest txAudioIngest,
        RxVstEngineService rxVstEngine,
        VstEngineController vstEngine)
        : this(manager, pipeline, new VstBridgeNative(),
               isMoxOn: () => tx.IsMoxOn,
               isMonitorOn: () => pipeline.CurrentEngine?.IsTxMonitorOn ?? false,
               preview: preview,
               chainOrder: chainOrder,
               rxChainOrder: rxChainOrder,
               log,
               isTciTxAudioActive: () => txAudioIngest.IsTciTxAudioActive,
               rxVstEngine: rxVstEngine,
               vstEngine: vstEngine) { }

    // Testable ctor — lets unit tests inject a fake IVstBridgeNative and
    // plain delegates for the MOX / monitor lookups so tests don't need
    // to stand up a full TxService + radio.
    internal AudioPluginBridge(
        PluginManager manager,
        DspPipelineService pipeline,
        IVstBridgeNative vstBridge,
        Func<bool> isMoxOn,
        Func<bool> isMonitorOn,
        IPreviewAudioSink preview,
        ChainOrderService? chainOrder,
        RxChainOrderService? rxChainOrder,
        ILogger<AudioPluginBridge> log,
        Func<bool>? isTciTxAudioActive = null,
        RxVstEngineService? rxVstEngine = null,
        VstEngineController? vstEngine = null,
        IVstBridgeNative? auBridge = null)
    {
        _manager = manager;
        _pipeline = pipeline;
        _vstBridge = vstBridge;
        _auBridge = auBridge;
        _isMoxOn = isMoxOn;
        _isMonitorOn = isMonitorOn;
        _isTciTxAudioActive = isTciTxAudioActive ?? (() => false);
        _chainOrder = chainOrder;
        _rxChainOrder = rxChainOrder;
        _rxVstEngine = rxVstEngine;
        _vstEngine = vstEngine;
        _log = log;
    }

    // Realtime-only ctor for unit tests that exercise ProcessLivePreview
    // / the chain directly without standing up PluginManager or
    // DspPipelineService. Lifecycle methods (StartAsync, OnPluginActivated,
    // AttachToEngine) MUST NOT be invoked on a bridge built this way —
    // they'd null-deref _manager / _pipeline. Tests populate the chain
    // via the Chain accessor and drive the preview gate via the
    // previewEnabled / engineIsWdsp parameters.
    internal AudioPluginBridge(
        Func<bool> isMoxOn,
        Func<bool> isMonitorOn,
        ILogger<AudioPluginBridge> log,
        IPreviewAudioSink? preview = null,
        bool previewEnabled = true,
        bool engineIsWdsp = true,
        Func<bool>? isTciTxAudioActive = null,
        RxVstEngineService? rxVstEngine = null,
        VstEngineController? vstEngine = null)
    {
        _manager = null!;
        _pipeline = null!;
        _vstBridge = null!;
        _isMoxOn = isMoxOn;
        _isMonitorOn = isMonitorOn;
        _isTciTxAudioActive = isTciTxAudioActive ?? (() => false);
        _chainOrder = null;
        _rxVstEngine = rxVstEngine;
        _vstEngine = vstEngine;
        _log = log;
        _engineIsWdsp = engineIsWdsp;
        _previewEnabled = previewEnabled;
    }

    /// <summary>Current chain (exposed for diagnostics / tests).</summary>
    internal AudioChain Chain => _chain;

    /// <summary>True if the pre-MOX preview tap is active (Wdsp engine + plugins attached).</summary>
    internal bool PreviewEnabled => _previewEnabled;

    /// <summary>
    /// Operator master-bypass write-through. Called by
    /// <c>AudioChainMasterBypassService</c> — on startup (apply persisted
    /// state) and on every operator toggle (apply new state). Single
    /// <c>volatile bool</c> write on the chain; no locks, no plugin
    /// re-init, no clicks/pops.
    /// </summary>
    public void SetMasterBypassed(bool bypassed)
    {
        _chain.MasterBypassed = bypassed;
    }

    /// <summary>Current master bypass state (mirrors <c>AudioChain.MasterBypassed</c>).</summary>
    public bool IsMasterBypassed => _chain.MasterBypassed;

    /// <summary>Operator master-bypass write-through for receive-side inserts.</summary>
    public void SetRxMasterBypassed(bool bypassed)
    {
        _rxChain.MasterBypassed = bypassed;
    }

    /// <summary>Current RX master bypass state.</summary>
    public bool IsRxMasterBypassed => _rxChain.MasterBypassed;

    /// <summary>
    /// Real-time diagnostics snapshot of the Audio Suite for the diagnostics v2
    /// surface: active route, out-of-process engine health, and per-chain
    /// (TX / RX) insert latency, per-block DSP load, and fidelity counters.
    /// Off the realtime path — reads lock-free chain telemetry and enumerates
    /// the plugin maps under the control-thread lock. Never call from audio.
    /// </summary>
    public AudioSuiteDiagnostics GetAudioSuiteDiagnostics()
    {
        const double sampleRate = 48000.0; // Audio Suite chains run at the host rate
        bool engineActive = _vstEngine is { IsActive: true };

        AudioChainDiagnostics BuildChain(AudioChain chain, int hostBlock)
        {
            // Enumerate the actual realtime slots (≤ MaxSlots) in chain order —
            // NOT the registered/scanned plugin set, which holds the whole
            // library. Latency is summed only over non-bypassed slots, matching
            // what Process actually runs.
            var plugins = new List<ChainPluginInfo>(AudioChain.MaxSlots);
            int vstCount = 0, sumLatency = 0;
            for (int slot = 0; slot < chain.SlotCount; slot++)
            {
                var plugin = chain.GetSlot(slot);
                if (plugin is null) continue;
                bool bypassed = chain.IsSlotBypassed(slot);
                int latency = 0;
                bool isVst = plugin is VstHostAudioPlugin;
                if (plugin is VstHostAudioPlugin vst)
                {
                    latency = vst.ReportedLatencySamples;
                    vstCount++;
                    if (!bypassed) sumLatency += latency;
                }
                plugins.Add(new ChainPluginInfo(
                    slot, plugin.DisplayName, isVst, bypassed, latency, latency / sampleRate * 1000.0));
            }

            var t = chain.Telemetry;
            var (inPk, outPk) = chain.Meters;
            double periodMicros = hostBlock / sampleRate * 1_000_000.0;
            return new AudioChainDiagnostics(
                ActivePlugins: plugins.Count,
                VstPlugins: vstCount,
                SumLatencySamples: sumLatency,
                SumLatencyMs: sumLatency / sampleRate * 1000.0,
                InPeakDbfs: ToDbfs(inPk),
                OutPeakDbfs: ToDbfs(outPk),
                LastBlockProcMicros: t.LastProcMicros,
                MaxBlockProcMicros: t.MaxProcMicros,
                BlockPeriodMicros: periodMicros,
                DspLoadPercent: periodMicros > 0 ? t.LastProcMicros / periodMicros * 100.0 : 0.0,
                PeakDspLoadPercent: periodMicros > 0 ? t.MaxProcMicros / periodMicros * 100.0 : 0.0,
                BlocksProcessed: t.BlocksProcessed,
                NonFiniteRepairs: t.NonFiniteRepairs,
                Plugins: plugins);
        }

        return new AudioSuiteDiagnostics(
            ProcessingMode: engineActive ? "vst-out-of-process" : "native-in-process",
            OutOfProcessEngineActive: engineActive,
            OutOfProcessDegradedBlocks: _vstEngine?.DegradedBlocks ?? 0,
            Tx: BuildChain(_chain, TxAudioHostBlockSize),
            Rx: BuildChain(_rxChain, RxAudioHostBlockSize));
    }

    private static double ToDbfs(float linearPeak) =>
        linearPeak <= 1e-6f ? -120.0 : 20.0 * Math.Log10(linearPeak);

    /// <summary>
    /// Chain-level signal meters (linear peak) for the Audio Suite IN /
    /// OUT bars: the level entering the TX insert chain and the level
    /// leaving it. Both read 0 until the chain processes audio — which
    /// only happens during MOX/TX or desktop-mode preview (mic
    /// preview). Surfaced via GET /api/audio-suite/chain/meters.
    /// </summary>
    public (float In, float Out) ChainMeters =>
        _vstEngine is { IsActive: true } ? (_engineInPeak, _engineOutPeak) : _chain.Meters;

    /// <summary>
    /// Chain-level signal meters for the receive-side Audio Suite insert lane.
    /// The RX path can combine out-of-process VST slots and native rx.* slots,
    /// so these capture the whole lane: audio entering <c>rx.post-demod</c>
    /// and audio leaving it.
    /// </summary>
    public (float In, float Out) RxChainMeters => (_rxInPeak, _rxOutPeak);

    /// <summary>
    /// True if the TX insert plugin chain (Audio Suite) is currently being
    /// bypassed because the active TX audio source is remote (e.g. TCI client).
    /// This is independent of the operator's master bypass toggle.
    /// Intended for diagnostics and potential future UI ("plugins bypassed for remote TX").
    /// </summary>
    public bool IsBypassedForRemoteTxSource => _isTciTxAudioActive();

    // -- VST editor (plug-in GUI) ---------------------------------------
    //
    // Route an "open / close the native editor" request from the REST
    // endpoint to the right hosted VST. Only TX-chain plugins (the
    // _idToPlugin map) can host an editor; the editor itself is a native
    // OS window owned by the bridge dylib (Windows-only at present).

    /// <summary>
    /// Open the native editor GUI for the hosted VST with the given
    /// plugin id. Returns a result describing the outcome so the endpoint
    /// can map it to an HTTP status.
    /// </summary>
    public EditorActionResult OpenEditor(string pluginId)
    {
        var vst = ResolveVst(pluginId, out var result);
        if (vst is null) return result;
        if (!vst.IsNativelyLoaded) return EditorActionResult.NotLoaded;
        return vst.OpenEditor() ? EditorActionResult.Ok : EditorActionResult.Failed;
    }

    /// <summary>Close the native editor for the hosted VST with the given id.</summary>
    public EditorActionResult CloseEditor(string pluginId)
    {
        var vst = ResolveVst(pluginId, out var result);
        if (vst is null) return result;
        vst.CloseEditor();
        return EditorActionResult.Ok;
    }

    /// <summary>True if the hosted VST's native editor window is currently open.</summary>
    public bool IsEditorOpen(string pluginId)
    {
        var vst = ResolveVst(pluginId, out _);
        return vst?.IsEditorOpen ?? false;
    }

    private VstHostAudioPlugin? ResolveVst(string pluginId, out EditorActionResult result)
    {
        IAudioPlugin? plugin;
        lock (_lock)
        {
            if (!_idToPlugin.TryGetValue(pluginId, out plugin))
                _rxIdToPlugin.TryGetValue(pluginId, out plugin);
        }
        if (plugin is null) { result = EditorActionResult.NotFound; return null; }
        if (plugin is not VstHostAudioPlugin vst) { result = EditorActionResult.NotAVst; return null; }
        result = EditorActionResult.Ok;
        return vst;
    }

    /// <summary>
    /// True when the in-process bridge actually hosts a <em>natively-loaded</em>
    /// plugin (TX or RX) for this id — i.e. there is a real plugin instance
    /// behind the handle whose editor the bridge can open in-process. This
    /// gates strictly on <see cref="VstHostAudioPlugin.IsNativelyLoaded"/>, not
    /// bare map membership: engine-routed VSTs (the Windows out-of-process VST
    /// path, and the opt-in-gated TX slot) live in the bridge maps but were
    /// never natively loaded, so they return false here and the editor guard /
    /// engine routing stays exactly as before for them. Used by the editor
    /// endpoints to skip the "install the VST engine" guard for AU/VST3 plugins
    /// the bridge genuinely hosts in-process (the only host on macOS).
    /// </summary>
    public bool HostsPlugin(string pluginId) =>
        ResolveVst(pluginId, out _) is { IsNativelyLoaded: true };

    public Task StartAsync(CancellationToken ct)
    {
        _manager.PluginActivated   += OnPluginActivated;
        _manager.PluginDeactivated += OnPluginDeactivated;
        _pipeline.EngineChanged    += OnEngineChanged;

        if (_chainOrder is not null)
        {
            _orderChangedHandler = ApplyChainOrder;
            _chainOrder.OrderChanged += _orderChangedHandler;
        }
        if (_rxChainOrder is not null)
        {
            _rxOrderChangedHandler = ApplyRxChainOrder;
            _rxChainOrder.OrderChanged += _rxOrderChangedHandler;
        }

        // Adopt any plugins already active (PluginManager might have
        // finished startup before us depending on hosted-service ordering).
        foreach (var p in _manager.Active) OnPluginActivated(p);

        // Install the handler on whatever engine is currently live.
        if (_pipeline.CurrentEngine is { } engine) AttachToEngine(engine);

        // Arm the VST-engine TX-audio liveness sampler (diagnostics only). Harmless
        // when VST mode is never selected — the tick short-circuits unless the
        // out-of-process engine is active and MOX is on.
        if (_vstEngine is not null)
        {
            _lastEngineDegraded = _vstEngine.DegradedBlocks;
            _livenessTimer = new System.Threading.Timer(
                _ => LivenessTick(), null, LivenessIntervalMs, LivenessIntervalMs);
        }

        _log.LogInformation("AudioPluginBridge online.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _livenessTimer?.Dispose();
        _livenessTimer = null;

        _manager.PluginActivated   -= OnPluginActivated;
        _manager.PluginDeactivated -= OnPluginDeactivated;
        _pipeline.EngineChanged    -= OnEngineChanged;

        if (_chainOrder is not null && _orderChangedHandler is not null)
        {
            _chainOrder.OrderChanged -= _orderChangedHandler;
            _orderChangedHandler = null;
        }
        if (_rxChainOrder is not null && _rxOrderChangedHandler is not null)
        {
            _rxChainOrder.OrderChanged -= _rxOrderChangedHandler;
            _rxOrderChangedHandler = null;
        }

        if (_pipeline.CurrentEngine is WdspDspEngine wdsp)
            wdsp.SetTxAudioPluginHandler(null);

        // Tear down the RX insert seam regardless of engine type — it lives on
        // DspPipelineService, not the engine.
        _pipeline.SetRxAudioPluginHandler(null);

        // Disable the preview gate before host disposal so any in-flight
        // NativeMicCapture callback that arrives between StopAsync and
        // NativeMicCapture's own StopAsync becomes a no-op.
        _previewEnabled = false;
        _engineIsWdsp = false;

        return Task.CompletedTask;
    }

    private void OnEngineChanged(IDspEngine engine) => AttachToEngine(engine);

    private void AttachToEngine(IDspEngine engine)
    {
        // The realtime seam is WdspDspEngine-only; SyntheticDspEngine has
        // no TX block to intercept. Skip the install and let the chain
        // sit idle until the next engine swap.
        if (engine is not WdspDspEngine wdsp)
        {
            _log.LogDebug("Engine {Type} not WdspDspEngine; audio plugin bridge idle", engine.GetType().Name);
            _engineIsWdsp = false;
            RefreshPreviewEnabled();
            return;
        }
        wdsp.SetTxAudioPluginHandler(Process);
        _engineIsWdsp = true;
        RefreshPreviewEnabled();
        _log.LogInformation("Audio plugin handler installed on WdspDspEngine.");
    }

    /// <summary>WDSP TX-path entry point — never allocates, never logs.</summary>
    private void Process(
        ReadOnlySpan<float> input,
        Span<float> output,
        int frames,
        int channels,
        int sampleRate)
    {
        int sampleCount = frames * channels;

        // VST processing mode (opt-in): when the operator has selected the
        // out-of-process VST engine AND it's live, route the TX block through it
        // instead of the native chain. Mutually exclusive with the native path
        // below. Master bypass and the remote-TCI bypass still force clean
        // passthrough — "disengage the Audio Suite" means clean audio in either
        // mode. The engine tap is itself robust (bounded-wait, passthrough on
        // engine-down / timeout), so a wedged plugin never stalls the radio.
        // When the gate is false this whole block is one volatile read and the
        // native path below runs byte-identically.
        if (_vstEngine is { IsActive: true })
        {
            if (_chain.MasterBypassed || _isTciTxAudioActive())
            {
                CopySanitizedForWdsp(input, output, sampleCount);
                return;
            }
            var vstCtx = new AudioBlockContext(sampleRate, channels, frames, sampleTime: 0, mox: true);
            // Gated so the on-air block never collides with a pre-MOX preview
            // block on the engine's single SHM slot at a MOX edge.
            TryProcessThroughEngine(input, output, vstCtx);
            return;
        }

        // Remote TCI (or future remote) TX audio source: bypass the entire
        // operator Audio Suite insert chain. The remote client has already
        // processed (or deliberately left clean) the audio it wants on the air.
        // Local mic and WAV playback continue to use the chain.
        if (_chain.MasterBypassed || _isTciTxAudioActive())
        {
            if (!_chain.MasterBypassed && _isTciTxAudioActive() && Interlocked.Increment(ref _remoteBypassLogCount) == 1)
            {
                _log.LogInformation("TX audio plugin chain bypassed for remote TCI source (IsBypassedForRemoteTxSource=true). Local mic path unaffected.");
            }
            CopySanitizedForWdsp(input, output, sampleCount);
            return;
        }

        var ctx = new AudioBlockContext(sampleRate, channels, frames, sampleTime: 0, mox: true);
        _chain.Process(input, output, ctx);
        DspPipelineService.SanitizeAudioBuffer(output[..sampleCount]);
    }

    private static void CopySanitizedForWdsp(ReadOnlySpan<float> input, Span<float> output, int sampleCount)
    {
        for (int i = 0; i < sampleCount; i++)
        {
            output[i] = DspPipelineService.SanitizeAudioSample(input[i]);
        }
    }

    /// <summary>
    /// Route one block through the out-of-process VST engine when it is the
    /// active processing path. Returns <c>false</c> when the engine is not active
    /// (the caller should run the native chain). A lost gate race — the other
    /// realtime thread owns the engine's single SHM slot this instant — degrades
    /// to clean passthrough and still returns <c>true</c>: the engine route was
    /// chosen, the block simply isn't double-driven. Realtime-safe: one CAS, no
    /// allocation, no managed lock.
    /// </summary>
    private bool TryProcessThroughEngine(
        ReadOnlySpan<float> input,
        Span<float> output,
        AudioBlockContext ctx,
        bool recordPassthroughMeters = true)
    {
        var vst = _vstEngine;
        if (vst is not { IsActive: true }) return false;
        int sampleCount = ctx.Frames * ctx.Channels;
        if (Interlocked.CompareExchange(ref _engineGate, 1, 0) != 0)
        {
            input.CopyTo(output);            // other thread owns the engine this instant
            DspPipelineService.SanitizeAudioBuffer(output[..sampleCount]);
            if (recordPassthroughMeters)
                RecordEngineMeters(input, output[..sampleCount]);
            return true;
        }
        bool processed;
        try { processed = vst.TryProcess(input, output, ctx); }
        finally { Volatile.Write(ref _engineGate, 0); }
        // Sample master IN/OUT peaks so the Audio Suite meters stay live in VST
        // mode (the native chain's own metering path didn't run). Tap the OUT
        // peak from the RAW engine output BEFORE the ±1 safety clamp below, so
        // the meter shows real overshoot (a hot plugin railing past full scale)
        // instead of pinning at exactly 0 dBFS once the chain clips. This
        // mirrors the native AudioChain, which also meters pre-clamp; BlockPeak
        // skips non-finite samples, so reading un-sanitized output is safe.
        if (processed || recordPassthroughMeters)
            RecordEngineMeters(input, output[..sampleCount]);
        DspPipelineService.SanitizeAudioBuffer(output[..sampleCount]);
        return true;
    }

    private void RecordEngineMeters(ReadOnlySpan<float> input, ReadOnlySpan<float> output)
    {
        _engineInPeak = BlockPeak(input);
        _engineOutPeak = BlockPeak(output);
    }

    /// <summary>
    /// Off-realtime diagnostic tick (threadpool, ~2 s). Watches the VST-engine TX
    /// path for the "alive but output dead" wedge that the supervisor's seq/exit
    /// health model can't detect, and leaves a trail in the log. NEVER touches the
    /// realtime path — reads only the volatile meter fields and the controller's
    /// diagnostic counters. Behaviour-neutral: logs only. See zeus-umt6.
    /// </summary>
    private void LivenessTick()
    {
        try
        {
            var vst = _vstEngine;
            // Only meaningful while the out-of-process engine is the live TX route
            // AND we're actually transmitting (the engine meters are only fed during
            // MOX / monitor). Otherwise reset state and rebase the degraded counter.
            if (vst is not { IsActive: true } || !_isMoxOn())
            {
                _deadOutputStreak = 0;
                _livenessHeartbeat = 0;
                _reloadAttempted = false;
                _recycleAttempted = false;
                _lastEngineDegraded = vst?.DegradedBlocks ?? _lastEngineDegraded;
                return;
            }

            float inPeak = _engineInPeak;
            float outPeak = _engineOutPeak;
            long degraded = vst.DegradedBlocks;
            long degradedDelta = degraded - _lastEngineDegraded;
            _lastEngineDegraded = degraded;
            uint engineState = vst.EngineState;
            uint engineFlags = vst.EngineFlags;

            // The wedge signature: clear input energy, effectively-silent output,
            // and NOT a transport degrade (degradedDelta==0 means the engine IS
            // answering — so this is bad audio, not a hang the watchdog would catch).
            bool deadOutput = inPeak >= LivenessInPresentPeak
                              && outPeak <= LivenessOutSilentPeak
                              && degradedDelta == 0;

            if (deadOutput)
            {
                _deadOutputStreak++;

                // Tier-1: at the warn point, re-push the chain into the LIVE engine
                // (re-instantiate plugins) — the same fix a manual profile change
                // performs. One attempt per episode.
                if (_deadOutputStreak == LivenessWarnAfterTicks)
                {
                    _log.LogWarning(
                        "VST engine TX output is DEAD while input is present for ~{Sec}s " +
                        "(in={In:F4} out={Out:F4} degradedΔ={Delta} engineState={State} flags=0x{Flags:X2}). " +
                        "Engine is responsive (no degraded blocks) but returning silence — the " +
                        "post-long-TX wedge (zeus-umt6).",
                        _deadOutputStreak * (LivenessIntervalMs / 1000),
                        inPeak, outPeak, degradedDelta, engineState, engineFlags);

                    if (AutoRecoverTxDeadOutput && !_reloadAttempted)
                    {
                        _reloadAttempted = true;
                        _log.LogWarning(
                            "VST TX auto-recovery (Tier 1): reloading the engine chain in place (zeus-umt6).");
                        vst.RequestChainReload(
                            "TX output dead while input present; reloading chain in place (zeus-umt6).");
                    }
                    return;
                }

                // Tier-2: still dead a grace window after the reload — recycle the
                // engine process. One attempt per episode; the relaunch path's
                // crash-loop cap prevents thrashing on a genuinely broken plugin.
                if (_deadOutputStreak == LivenessWarnAfterTicks + LivenessEscalateAfterTicks
                    && AutoRecoverTxDeadOutput && _reloadAttempted && !_recycleAttempted)
                {
                    _recycleAttempted = true;
                    _log.LogWarning(
                        "VST TX auto-recovery (Tier 2): chain reload did not clear the dead output after " +
                        "~{Sec}s; recycling the engine process (zeus-umt6).",
                        _deadOutputStreak * (LivenessIntervalMs / 1000));
                    vst.ForceRecycle("TX output still dead after chain reload; recycling engine (zeus-umt6).");
                    return;
                }

                // Beyond escalation: periodic repeat-warn while still wedged (covers
                // the case where auto-recovery is off or a broken plugin can't heal).
                if (_deadOutputStreak > LivenessWarnAfterTicks
                    && (_deadOutputStreak - LivenessWarnAfterTicks) % LivenessHeartbeatTicks == 0)
                {
                    _log.LogWarning(
                        "VST engine TX output still dead (~{Sec}s): in={In:F4} out={Out:F4} " +
                        "degradedΔ={Delta} engineState={State} flags=0x{Flags:X2}.",
                        _deadOutputStreak * (LivenessIntervalMs / 1000),
                        inPeak, outPeak, degradedDelta, engineState, engineFlags);
                }
                return;
            }

            if (_deadOutputStreak >= LivenessWarnAfterTicks)
                _log.LogInformation(
                    "VST engine TX output recovered (in={In:F4} out={Out:F4}).", inPeak, outPeak);
            _deadOutputStreak = 0;
            _reloadAttempted = false;
            _recycleAttempted = false;

            // Periodic healthy heartbeat so the log captures the baseline IN/OUT
            // levels leading up to any future wedge.
            if (++_livenessHeartbeat >= LivenessHeartbeatTicks)
            {
                _livenessHeartbeat = 0;
                _log.LogInformation(
                    "VST TX liveness: in={In:F4} out={Out:F4} degradedΔ={Delta} " +
                    "engineState={State} flags=0x{Flags:X2}.",
                    inPeak, outPeak, degradedDelta, engineState, engineFlags);
            }
        }
        catch { /* diagnostics must never throw on the timer thread */ }
    }

    /// <summary>Instantaneous block abs-peak — mirrors AudioChain.BlockPeak so the
    /// engine-path IN/OUT meters read identically to the native chain's.</summary>
    private static float BlockPeak(ReadOnlySpan<float> block)
    {
        float peak = 0f;
        for (int i = 0; i < block.Length; i++)
        {
            float a = block[i];
            if (!float.IsFinite(a)) continue;
            if (a < 0f) a = -a;
            if (a > peak) peak = a;
        }
        return peak;
    }

    /// <summary>
    /// Test-only hook to drive the on-air TX audio plugin handler path
    /// (the one invoked via the TxAudioBlockHandler from WdspDspEngine.ProcessTxBlock
    /// during MOX). Respects both master bypass and the remote TCI source bypass.
    /// Never allocate, never for production code.
    /// </summary>
    internal void ProcessTxForTest(ReadOnlySpan<float> input, Span<float> output, int frames, int channels = 1, int sampleRate = 48000)
        => Process(input, output, frames, channels, sampleRate);

    /// <summary>
    /// Live mic preview entry point. Called from <c>NativeMicCapture</c>
    /// once per accumulated mic block, regardless of MOX, so the
    /// per-plugin IN / OUT / GR meters animate from live mic input
    /// even when nothing is being transmitted (matching the desktop
    /// main-GUI mic meter's pre-MOX behaviour).
    ///
    /// <para>Short-circuits when the preview gate is off (no Wdsp engine
    /// or no plugins attached), when MOX is on (the WDSP TX path is the
    /// canonical chain runner during MOX), or when the engine's TX
    /// monitor is on (the TX path runs the chain via ProcessTxBlock
    /// for the preview feed). The two paths are mutually exclusive in
    /// time except for the microsecond-scale overlap window at a MOX
    /// edge; the caller-supplied scratch span on
    /// <see cref="AudioChain.Process(ReadOnlySpan{float}, Span{float},
    /// Span{float}, AudioBlockContext)"/> ensures the two paths never
    /// collide on the chain's <c>_scratch</c> buffer in that window.</para>
    ///
    /// <para>Output samples are discarded — audible Audio Suite preview is
    /// handled by the WDSP TX-monitor path so it includes the full TXA chain
    /// (leveler, compressor, CFC, ALC, bandpass/CFIR), not just the plugin
    /// insert output. This preview path only updates each plugin's last-input
    /// / last-output / last-GR meter fields, which the REST <c>/meters</c>
    /// polling surfaces to the React panels.</para>
    ///
    /// <para>Realtime contract: never allocates on the heap, never
    /// throws (the caller wraps in try/catch as defence in depth),
    /// never logs.</para>
    /// </summary>
    internal void ProcessLivePreview(ReadOnlySpan<float> mic, int sampleRate)
    {
        if (!_previewEnabled) return;
        // Master bypass — skip the entire preview pipeline (stackalloc +
        // chain dispatch) when the operator has disengaged the suite.
        // The chain itself would also short-circuit to a copy, but we
        // save the stackallocs and the virtual call by bailing here.
        // Per-plugin meters freeze on their last-active values, which
        // matches operator intuition ("the engine isn't running").
        if (_chain.MasterBypassed) return;
        if (_isMoxOn()) return;
        if (_isMonitorOn()) return;

        // Stack-allocated buffers — at the desktop mic block size (960
        // samples / 20 ms) this is 2 * 960 * 4 = 7.5 KiB on the stack,
        // well under the realtime stackalloc budget. The output is discarded;
        // the side-effect on each plugin's last-meter fields is what drives
        // the IN / OUT / GR animation in the panels.
        Span<float> previewOut = stackalloc float[mic.Length];
        var ctx = new AudioBlockContext(
            sampleRate: sampleRate,
            channels: 1,
            frames: mic.Length,
            sampleTime: 0,
            mox: false);

        // VST mode: route the live mic block through the out-of-process engine so
        // the operator can meter the VST chain OFF-AIR, exactly as the native
        // chain animates its meters here on-bench. Audible preview is owned by
        // TX Monitor, not this preview output. When the engine isn't the active
        // route, fall through to the native in-process chain unchanged.
        if (!TryProcessThroughEngine(
                mic,
                previewOut,
                ctx,
                recordPassthroughMeters: false))
        {
            Span<float> previewScratch = stackalloc float[mic.Length];
            _chain.Process(mic, previewOut, previewScratch, ctx);
        }
        DspPipelineService.SanitizeAudioBuffer(previewOut);

        // Do not publish previewOut to any local sink. Audio Suite preview is
        // the TX Monitor path, which runs this same mic block through
        // ProcessTxBlock and demodulates the post-output IQ for a full-chain
        // comparison with on-air TX audio.
    }

    /// <summary>
    /// Recompute the <c>_previewEnabled</c> gate from the current engine
    /// + plugin-count state. Called on every plugin attach / detach and
    /// on every engine swap. One-shot info log on edges so an operator
    /// can grep for "preview" to confirm the live-preview tap is wired.
    /// </summary>
    private void RefreshPreviewEnabled()
    {
        bool shouldEnable;
        lock (_lock)
        {
            shouldEnable = _engineIsWdsp && _idToSlot.Count > 0;
        }
        if (shouldEnable == _previewEnabled) return;
        _previewEnabled = shouldEnable;
        _log.LogInformation(
            "Audio plugin live-preview tap {State}",
            shouldEnable ? "enabled" : "disabled");
    }

    // -- Plugin lifecycle ------------------------------------------------

    private void OnPluginActivated(ActivatedPlugin p)
    {
        var audioPlugin = ResolveAudioPlugin(p);
        if (audioPlugin is null) return;

        // Route rx.* slots into the separate RX insert chain. These never join
        // the TX chain or ChainOrderService.
        var manifestSlot = p.Loaded.Manifest.Audio?.Slot;
        if (manifestSlot is not null && manifestSlot.StartsWith("rx.", StringComparison.Ordinal))
        {
            AttachRxPlugin(p, audioPlugin, manifestSlot);
            return;
        }

        int slot = -1;
        bool parked = false;
        lock (_lock)
        {
            if (_chainOrder is not null)
            {
                // ChainOrderService-driven slot assignment: pull the
                // operator's canonical order, find the new plugin's
                // place in it (appending to the end if first install),
                // and re-slot the entire chain so the new plugin lands
                // in the right position relative to the existing ones.
                _idToPlugin[p.Loaded.Manifest.Id] = audioPlugin;
                var attachedIds = _idToPlugin.Keys.ToList();
                _chainOrder.OnPluginAttached(p.Loaded.Manifest.Id, attachedIds);
                ReapplySlotsUnderLock();
                if (!_idToSlot.TryGetValue(p.Loaded.Manifest.Id, out slot))
                {
                    // Not in the runtime slot table after re-slotting. The
                    // legitimate reason is that the plugin is PARKED — the
                    // operator excluded it from the live chain. A parked
                    // plugin stays attached (kept in _idToPlugin and
                    // initialized below, so its VST loads and its editor
                    // can open) but out of the DSP graph; un-parking
                    // re-slots it instantly. Only a NON-parked miss is a
                    // real inconsistency worth evicting + logging — parking
                    // must not lose the plugin instance (regression: it
                    // used to, so parked VSTs vanished until a restart).
                    parked = _chainOrder.IsParked(p.Loaded.Manifest.Id);
                    if (!parked)
                    {
                        _log.LogError(
                            "Audio plugin {Id} not slotted after ReapplySlotsUnderLock; chain may be inconsistent",
                            p.Loaded.Manifest.Id);
                        _idToPlugin.Remove(p.Loaded.Manifest.Id);
                        return;
                    }
                    slot = -1; // parked — initialized but not in the chain
                }
            }
            else
            {
                // Legacy / test path — no ChainOrderService injected; use
                // the historical first-free-slot behaviour.
                slot = FindFreeSlot();
                if (slot < 0)
                {
                    _log.LogWarning(
                        "Audio chain full (8 slots); ignoring plugin {Id}",
                        p.Loaded.Manifest.Id);
                    return;
                }
                _idToSlot[p.Loaded.Manifest.Id] = slot;
                _idToPlugin[p.Loaded.Manifest.Id] = audioPlugin;
                _chain.SetSlot(slot, audioPlugin);
            }
        }

        // Realtime-safe native init off-thread before the chain dispatches —
        // but ONLY for a plugin that is actually in the live chain (slot >= 0).
        // A PARKED plugin (slot < 0) is registered and left UNINITIALIZED so a
        // scan never instantiates / launches it; its native resources load
        // lazily the moment the operator un-parks it (ApplyChainOrder →
        // EnsureTxPluginInitialized), mirroring the receive path's
        // EnsureRxPluginInitialized deferral. The chain itself doesn't call
        // Initialize; we do it here so a plugin can allocate / open resources
        // before its first Process() call.
        var slotName = p.Loaded.Manifest.Audio?.Slot ?? "tx.post-leveler";
        lock (_lock) _txIdToSlotName[p.Loaded.Manifest.Id] = slotName;

        if (slot >= 0 &&
            !EnsureTxPluginInitialized(p.Loaded.Manifest.Id, audioPlugin, slotName))
        {
            lock (_lock)
            {
                _chain.ClearSlot(slot);
                _idToSlot.Remove(p.Loaded.Manifest.Id);
                _idToPlugin.Remove(p.Loaded.Manifest.Id);
                _txIdToSlotName.Remove(p.Loaded.Manifest.Id);
            }
            _chainOrder?.OnPluginDetached(p.Loaded.Manifest.Id);
            return;
        }

        // Note: master bypass is owned by AudioChainMasterBypassService now;
        // the bridge no longer auto-toggles _chain.MasterBypassed on attach.
        // An attach into an operator-bypassed chain leaves the chain inert
        // (correct behaviour — operator's choice stays sticky).
        RefreshPreviewEnabled();
        if (parked)
            _log.LogInformation(
                "Audio plugin {Id} attached (parked — registered, native load deferred until added to the chain)",
                p.Loaded.Manifest.Id);
        else
            _log.LogInformation(
                "Audio plugin {Id} attached to slot {Slot}",
                p.Loaded.Manifest.Id, slot);
    }

    private void OnPluginDeactivated(ActivatedPlugin p)
    {
        // RX insert chain detach takes priority — an rx.* plugin never lives in
        // the TX _idToSlot map.
        if (DetachRxPlugin(p)) return;

        var id = p.Loaded.Manifest.Id;
        IAudioPlugin? attached;
        bool wasInitialized;
        lock (_lock)
        {
            // A parked plugin lives in _idToPlugin but not _idToSlot — handle
            // both so deactivating a never-un-parked (and therefore
            // never-initialized) scanned plugin cleans up fully instead of
            // leaking its registration.
            if (!_idToPlugin.Remove(id, out attached)) return;
            if (_idToSlot.Remove(id, out var slot))
                _chain.ClearSlot(slot);
            _txIdToSlotName.Remove(id);
            wasInitialized = _txInitializedIds.Remove(id);
            // Re-slot remaining plugins so the chain compacts when
            // ChainOrderService is active — gaps from a removed plugin
            // close instead of leaving a hole in the middle of the
            // chain.
            if (_chainOrder is not null) ReapplySlotsUnderLock();
            // Note: master bypass is owned by AudioChainMasterBypassService;
            // bridge no longer flips _chain.MasterBypassed on last detach.
            // The chain Process loop already no-ops cleanly on an empty
            // slot table (all slots null → all iterations skipped).
        }
        _chainOrder?.OnPluginDetached(id);
        RefreshPreviewEnabled();

        if (attached is null) return;
        // Only tear down native resources we actually brought up. A parked
        // plugin was never initialized, so there is nothing to shut down.
        if (wasInitialized)
        {
            try
            {
                attached.ShutdownAudioAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Audio plugin {Id} ShutdownAudioAsync threw", id);
            }
        }
        if (attached is IAsyncDisposable ad)
        {
            try { ad.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch { /* best effort */ }
        }
    }

    // -- RX insert chain (rx.* slots) -----------------------------------

    /// <summary>
    /// Adopt an rx.* audio plugin into the dedicated <see cref="_rxChain"/>.
    /// Installs the pipeline RX handler on the first attach so the RX audio
    /// path stays untouched (null handler) until an RX plugin is present.
    /// </summary>
    private void AttachRxPlugin(ActivatedPlugin p, IAudioPlugin audioPlugin, string slotName)
    {
        var id = p.Loaded.Manifest.Id;
        lock (_lock)
        {
            _rxIdToPlugin[id] = audioPlugin;
            _rxIdToSlotName[id] = slotName;
        }

        if (_rxChainOrder is not null)
        {
            _rxChainOrder.OnPluginAttached(id);
        }
        else
        {
            if (!EnsureRxPluginInitialized(id, audioPlugin, slotName))
            {
                lock (_lock)
                {
                    _rxIdToPlugin.Remove(id);
                    _rxIdToSlotName.Remove(id);
                }
                return;
            }

            bool anyActive;
            lock (_lock)
            {
                if (ShouldRouteRxPluginThroughEngine(audioPlugin))
                {
                    anyActive = true;
                }
                else
                {
                    var slot = FindFreeRxSlot();
                    if (slot < 0)
                    {
                        _log.LogWarning(
                            "RX audio chain full ({Max} slots); ignoring plugin {Id}",
                            _rxChain.SlotCount, id);
                        _rxIdToPlugin.Remove(id);
                        return;
                    }
                    _rxIdToSlot[id] = slot;
                    _rxChain.SetSlot(slot, audioPlugin);
                    anyActive = _rxIdToSlot.Count > 0;
                }
            }
            if (anyActive) _pipeline.SetRxAudioPluginHandler(ProcessRxBlock);
        }

        _log.LogInformation(
            "RX audio plugin {Id} attached to RX route (slot={SlotName}, active={Active})",
            id, slotName, IsRxPluginActive(id));
    }

    /// <summary>
    /// Detach an rx.* plugin if present. Returns true when the plugin was an RX
    /// plugin (handled here); false when it was not, so the caller falls
    /// through to the TX detach path. Uninstalls the pipeline RX handler on the
    /// last detach.
    /// </summary>
    private bool DetachRxPlugin(ActivatedPlugin p)
    {
        IAudioPlugin? attached;
        bool anyActive;
        bool wasInitialized;
        var id = p.Loaded.Manifest.Id;
        lock (_lock)
        {
            if (!_rxIdToPlugin.Remove(id, out attached)) return false;
            _rxIdToSlotName.Remove(id);
            wasInitialized = _rxInitializedIds.Remove(id);
            if (_rxIdToSlot.Remove(id, out var slot))
                _rxChain.ClearSlot(slot);
            anyActive = _rxIdToSlot.Count > 0
                || _rxIdToPlugin.Values.Any(ShouldRouteRxPluginThroughEngine);
        }

        if (_rxChainOrder is not null)
            _rxChainOrder.OnPluginDetached(id);
        else if (!anyActive)
            _pipeline.SetRxAudioPluginHandler(null);

        if (attached is not null)
        {
            if (wasInitialized)
            {
                try { attached.ShutdownAudioAsync(CancellationToken.None).GetAwaiter().GetResult(); }
                catch (Exception ex)
                {
                    _log.LogWarning(ex,
                        "RX audio plugin {Id} ShutdownAudioAsync threw", id);
                }
            }
            if (attached is IAsyncDisposable ad)
            {
                try { ad.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                catch { /* best effort */ }
            }
        }
        _log.LogInformation("RX audio plugin {Id} detached", id);
        return true;
    }

    private void ApplyRxChainOrder(IReadOnlyList<string> activeOrder)
    {
        foreach (var id in activeOrder)
        {
            IAudioPlugin? plugin;
            string? slotName;
            lock (_lock)
            {
                _rxIdToPlugin.TryGetValue(id, out plugin);
                _rxIdToSlotName.TryGetValue(id, out slotName);
            }
            if (plugin is null || string.IsNullOrWhiteSpace(slotName)) continue;
            EnsureRxPluginInitialized(id, plugin, slotName);
        }

        bool anyActive;
        bool engineRouteActive;
        lock (_lock)
        {
            anyActive = ReapplyRxSlotsUnderLock(activeOrder, out engineRouteActive);
        }
        _pipeline.SetRxAudioPluginHandler(anyActive || engineRouteActive ? ProcessRxBlock : null);
        _log.LogInformation("RX audio plugin chain re-slotted ({Count} active)", activeOrder.Count);
    }

    private bool ReapplyRxSlotsUnderLock(IReadOnlyList<string> activeOrder, out bool engineRouteActive)
    {
        engineRouteActive = false;
        for (int i = 0; i < _rxChain.SlotCount; i++)
            _rxChain.ClearSlot(i);
        _rxIdToSlot.Clear();

        var slot = 0;
        foreach (var id in activeOrder)
        {
            if (!_rxIdToPlugin.TryGetValue(id, out var plugin)) continue;
            if (!_rxInitializedIds.Contains(id)) continue;
            if (ShouldRouteRxPluginThroughEngine(plugin))
            {
                engineRouteActive = true;
                continue;
            }
            if (slot >= _rxChain.SlotCount)
            {
                _log.LogWarning(
                    "RX audio chain full ({Max} slots); plugin {Id} is active but not slotted",
                    _rxChain.SlotCount, id);
                continue;
            }
            _rxIdToSlot[id] = slot;
            _rxChain.SetSlot(slot, plugin);
            slot++;
        }
        return slot > 0;
    }

    private bool ShouldRouteRxPluginThroughEngine(IAudioPlugin plugin) =>
        plugin is VstHostAudioPlugin && _rxVstEngine is { EngineAvailable: true };

    private bool EnsureRxPluginInitialized(string id, IAudioPlugin audioPlugin, string slotName)
    {
        lock (_lock)
        {
            if (_rxInitializedIds.Contains(id)) return true;
        }

        if (audioPlugin is VstHostAudioPlugin && _rxVstEngine is { EngineAvailable: true })
        {
            lock (_lock)
            {
                _rxInitializedIds.Add(id);
            }
            _log.LogInformation(
                "RX VST plugin {Id} reserved for the out-of-process RX VST engine; in-process VST bridge not loaded",
                id);
            return true;
        }

        try
        {
            audioPlugin.InitializeAudioAsync(
                    new AudioHost(slotName, blockSize: RxAudioHostBlockSize),
                    CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "RX audio plugin {Id} InitializeAudioAsync threw; leaving it inactive",
                id);
            return false;
        }

        lock (_lock)
        {
            _rxInitializedIds.Add(id);
        }
        return true;
    }

    private int FindFreeRxSlot()
    {
        for (int i = 0; i < _rxChain.SlotCount; i++)
            if (_rxChain.GetSlot(i) is null) return i;
        return -1;
    }

    private bool IsRxPluginActive(string pluginId)
    {
        lock (_lock) return _rxIdToSlot.ContainsKey(pluginId);
    }

    /// <summary>
    /// RX-path entry point installed on <c>DspPipelineService</c>. Runs the RX
    /// chain in place over the demodulated band audio block. Never allocates,
    /// never logs. The chain seeds its output from input (a self-copy no-op
    /// when the two spans alias) then ping-pongs output ⇄ its internal scratch,
    /// never re-reading input — so passing one span as both input and output is
    /// safe for true in-place processing.
    /// </summary>
    private void ProcessRxBlock(Span<float> audio, int frames, int sampleRate)
    {
        var block = audio[..frames];
        var inPeak = BlockPeak(block);
        if (_rxChain.MasterBypassed)
        {
            DspPipelineService.SanitizeAudioBuffer(block);
            RecordRxChainMeters(inPeak, block);
            return;
        }
        var ctx = new AudioBlockContext(sampleRate, channels: 1, frames: frames, sampleTime: 0, mox: false);
        _rxVstEngine?.ProcessIfActive(block, block, ctx);
        _rxChain.Process(block, block, ctx);
        DspPipelineService.SanitizeAudioBuffer(block);
        RecordRxChainMeters(inPeak, block);
    }

    internal void ProcessRxForTest(Span<float> audio, int frames, int sampleRate)
        => ProcessRxBlock(audio, frames, sampleRate);

    private void RecordRxChainMeters(float inputPeak, ReadOnlySpan<float> output)
    {
        _rxInPeak = inputPeak;
        _rxOutPeak = BlockPeak(output);
    }

    /// <summary>Returns the plugin's IAudioPlugin, or synthesises a
    /// <see cref="VstHostAudioPlugin"/> if the manifest declares audio.vst3Path,
    /// or null if the plugin contributes no audio.</summary>
    private IAudioPlugin? ResolveAudioPlugin(ActivatedPlugin p)
    {
        if (p.Loaded.Plugin is IAudioPlugin direct) return direct;

        var audio = p.Loaded.Manifest.Audio;
        if (audio is null) return null;

        // Format selects the native backend. "au" → macOS Audio Unit bridge
        // keyed by auComponentId; anything else (default "vst3") → the VST3
        // bridge keyed by vst3Path. The VST3 path is unchanged; AU is purely
        // additive (3-way dispatch).
        bool isAu = string.Equals(audio.Format, "au", StringComparison.OrdinalIgnoreCase);
        bool hasIdentity = isAu
            ? audio.AuComponentId is { Length: > 0 }
            : audio.Vst3Path is { Length: > 0 };
        if (!hasIdentity) return null;

        var bridge = isAu ? (_auBridge ??= new AuBridgeNative()) : _vstBridge;
        return new VstHostAudioPlugin(
            bridge: bridge,
            manifestAudio: audio,
            pluginRootPath: p.Loaded.PluginDir,
            displayName: p.Loaded.Manifest.Name,
            log: _log);
    }

    private int FindFreeSlot()
    {
        for (int i = 0; i < _chain.SlotCount; i++)
            if (_chain.GetSlot(i) is null) return i;
        return -1;
    }

    /// <summary>
    /// Event handler for <see cref="ChainOrderService.OrderChanged"/>.
    /// Re-slots the chain so the runtime sequence matches the
    /// canonical order the operator chose via the Audio Suite tile
    /// strip. Fired off the chain order's _sync lock; we take
    /// _lock here for the slot mutation.
    /// </summary>
    private void ApplyChainOrder(IReadOnlyList<string> _)
    {
        // We don't use the supplied order argument directly because
        // ReapplySlotsUnderLock reads CurrentOrder fresh from the
        // service inside the lock — same semantics, but it picks up
        // the latest snapshot in the rare case the order changes again
        // between the OrderChanged fire and our lock acquisition.
        //
        // Native load is DEFERRED until a plugin is un-parked into the live
        // chain, so this OrderChanged fire — the operator adding a plugin to
        // the chain — is the first point a newly-active plugin must load.
        // Initialize the now-active set BEFORE slotting it (outside _lock;
        // native load can block), mirroring ApplyRxChainOrder.
        EnsureActiveTxPluginsInitialized();
        lock (_lock) ReapplySlotsUnderLock();
        _log.LogInformation("Audio plugin chain re-slotted to operator order");
    }

    /// <summary>
    /// Recycle already-active TX plugins so they re-read their
    /// <see cref="Zeus.Plugins.Contracts.IPluginSettings"/> values after a TX
    /// audio profile restore. Profile apply writes plugin settings directly to
    /// LiteDB; an initialized plugin would otherwise keep its prior in-memory
    /// defaults until the next Zeus restart.
    /// </summary>
    public void ReloadActiveTxPlugins(IReadOnlyCollection<string> pluginIds)
    {
        if (pluginIds.Count == 0) return;

        var requested = new HashSet<string>(pluginIds, StringComparer.Ordinal);
        var reload = new List<(string Id, IAudioPlugin Plugin, string SlotName)>();

        lock (_lock)
        {
            foreach (var id in requested)
            {
                if (!_idToPlugin.TryGetValue(id, out var plugin)) continue;
                if (!_idToSlot.TryGetValue(id, out var slot)) continue;
                if (!_txInitializedIds.Remove(id)) continue;

                var slotName = _txIdToSlotName.TryGetValue(id, out var s)
                    ? s
                    : "tx.post-leveler";
                reload.Add((id, plugin, slotName));
                _chain.ClearSlot(slot);
            }
        }

        if (reload.Count == 0) return;

        foreach (var item in reload)
        {
            try
            {
                item.Plugin.ShutdownAudioAsync(CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Audio plugin {Id} ShutdownAudioAsync threw during TX profile reload",
                    item.Id);
            }
        }

        var failed = new List<string>();
        foreach (var item in reload)
        {
            if (!EnsureTxPluginInitialized(item.Id, item.Plugin, item.SlotName))
                failed.Add(item.Id);
        }

        if (failed.Count > 0)
        {
            lock (_lock)
            {
                foreach (var id in failed)
                {
                    if (_idToSlot.Remove(id, out var slot))
                        _chain.ClearSlot(slot);
                    _idToPlugin.Remove(id);
                    _txIdToSlotName.Remove(id);
                    _txInitializedIds.Remove(id);
                }
                if (_chainOrder is not null) ReapplySlotsUnderLock();
            }

            foreach (var id in failed)
                _chainOrder?.OnPluginDetached(id);
        }
        else
        {
            lock (_lock)
            {
                if (_chainOrder is not null) ReapplySlotsUnderLock();
            }
        }

        RefreshPreviewEnabled();
        _log.LogInformation(
            "Reloaded {Count} active TX audio plugin(s) after profile restore",
            reload.Count - failed.Count);
    }

    /// <summary>
    /// Lazily load the native resources of any TX plugin that is currently
    /// ACTIVE (un-parked) in the operator's order but not yet initialized.
    /// Native instantiation is deferred until a plugin enters the live chain
    /// (parity with the receive path's <see cref="EnsureRxPluginInitialized"/>),
    /// so this runs on un-park / reorder. Outside <c>_lock</c> —
    /// <c>InitializeAudioAsync</c> can block on native load.
    /// </summary>
    private void EnsureActiveTxPluginsInitialized()
    {
        if (_chainOrder is null) return;
        foreach (var id in _chainOrder.CurrentOrder)
        {
            IAudioPlugin? plugin;
            string slotName;
            lock (_lock)
            {
                if (!_idToPlugin.TryGetValue(id, out plugin)) continue;
                slotName = _txIdToSlotName.TryGetValue(id, out var s) ? s : "tx.post-leveler";
            }
            EnsureTxPluginInitialized(id, plugin, slotName);
        }
    }

    /// <summary>
    /// Initialize one TX plugin's native resources exactly once. Returns true
    /// if the plugin is initialized (now or already); false if
    /// <c>InitializeAudioAsync</c> threw — the caller leaves it out of the live
    /// chain (it passes audio through until a later successful load). Mirrors
    /// <see cref="EnsureRxPluginInitialized"/>.
    /// </summary>
    private bool EnsureTxPluginInitialized(string id, IAudioPlugin audioPlugin, string slotName)
    {
        lock (_lock)
        {
            if (_txInitializedIds.Contains(id)) return true;
        }

        try
        {
            audioPlugin.InitializeAudioAsync(
                    new AudioHost(slotName, blockSize: TxAudioHostBlockSize),
                    CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Audio plugin {Id} InitializeAudioAsync threw; leaving it inactive", id);
            return false;
        }

        lock (_lock)
        {
            _txInitializedIds.Add(id);
        }
        return true;
    }

    /// <summary>
    /// Clears every chain slot and re-populates them so plugins in
    /// <see cref="_idToPlugin"/> land at indices matching their
    /// position in <see cref="ChainOrderService.CurrentOrder"/>,
    /// skipping IDs that aren't currently attached. CALLER must
    /// hold <see cref="_lock"/>.
    ///
    /// <para>Plugins not present in the canonical order (e.g. a
    /// third-party plugin that landed before ChainOrderService had
    /// a chance to append it) get appended to the end in
    /// deterministic order (by ID).</para>
    /// </summary>
    private void ReapplySlotsUnderLock()
    {
        if (_chainOrder is null) return;
        var canonical = _chainOrder.CurrentOrder;
        var canonicalSet = new HashSet<string>(canonical, StringComparer.Ordinal);

        // Clear current slot assignments. AudioChain.SetSlot replaces
        // the slot's plugin reference and clears bypass; ClearSlot
        // nulls it and clears bypass. Use ClearSlot for the wipe so
        // bypass state doesn't leak across reorders.
        for (int i = 0; i < _chain.SlotCount; i++) _chain.ClearSlot(i);
        _idToSlot.Clear();

        // Re-slot in canonical order, plus any orphans appended.
        int slotIndex = 0;
        for (int i = 0; i < canonical.Count && slotIndex < _chain.SlotCount; i++)
        {
            var id = canonical[i];
            if (!_idToPlugin.TryGetValue(id, out var plugin)) continue;
            _chain.SetSlot(slotIndex, plugin);
            _idToSlot[id] = slotIndex;
            slotIndex++;
        }
        // Append orphans (attached but not in canonical order) at the
        // end, sorted for determinism. PARKED plugins are deliberately
        // absent from CurrentOrder (and thus canonicalSet); they must
        // NOT be re-slotted here, or parking wouldn't actually pull
        // them out of the live processing chain.
        foreach (var kvp in _idToPlugin
                     .Where(k => !canonicalSet.Contains(k.Key)
                                 && !(_chainOrder?.IsParked(k.Key) ?? false))
                     .OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (slotIndex >= _chain.SlotCount)
            {
                _log.LogWarning(
                    "Audio chain full at re-slot; dropping {Id}", kvp.Key);
                continue;
            }
            _chain.SetSlot(slotIndex, kvp.Value);
            _idToSlot[kvp.Key] = slotIndex;
            slotIndex++;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await _chain.DisposeAsync().ConfigureAwait(false);
        await _rxChain.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class AudioHost : IAudioHost
    {
        public AudioHost(string slotName, int blockSize)
        {
            Slot = slotName;
            CurrentBlockSize = blockSize;
        }

        public int CurrentSampleRate => 48000;
        public int CurrentChannels => 1;
        public int CurrentBlockSize { get; }
        public string Slot { get; }
    }
}
