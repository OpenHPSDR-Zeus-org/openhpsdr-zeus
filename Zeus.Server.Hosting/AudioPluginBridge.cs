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
    private readonly PluginManager _manager;
    private readonly DspPipelineService _pipeline;
    private readonly IVstBridgeNative _vstBridge;
    private readonly Func<bool> _isMoxOn;
    private readonly Func<bool> _isMonitorOn;
    private readonly Func<bool> _isTciTxAudioActive;
    private readonly IAuditionAudioSink _audition;
    private int _remoteBypassLogCount; // for one-time diagnostic log during verification/on-air testing
    private readonly ChainOrderService? _chainOrder;
    // Out-of-process VST engine route (opt-in "VST" processing mode). Null in
    // tests / when no controller is injected. When its IsActive gate is true the
    // realtime TX path routes through the external engine instead of _chain;
    // when false the engine is never touched and TX is byte-identical to native.
    private readonly VstEngineController? _vstEngine;
    private readonly ILogger<AudioPluginBridge> _log;
    private readonly AudioChain _chain = new();
    private readonly Dictionary<string, int> _idToSlot = new();
    private readonly Dictionary<string, IAudioPlugin> _idToPlugin = new();
    // RX audio insert chain — plugins whose manifest slot is rx.* (e.g.
    // rx.post-demod, a CW SCAF audio filter). Kept ENTIRELY separate from the
    // TX _chain: separate plugin instances, separate IIR state, and a separate
    // realtime runner (ProcessRxBlock, installed on DspPipelineService rather
    // than the WDSP TX seam). ChainOrderService governs only the TX chain; the
    // RX chain uses simple first-free-slot ordering. The pipeline handler is
    // installed only while at least one RX plugin is attached, so the RX audio
    // path stays bit-identical to before this seam when none is loaded.
    private readonly AudioChain _rxChain = new();
    private readonly Dictionary<string, int> _rxIdToSlot = new();
    private readonly object _lock = new();
    private Action<IReadOnlyList<string>>? _orderChangedHandler;
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

    public AudioPluginBridge(
        PluginManager manager,
        DspPipelineService pipeline,
        TxService tx,
        IAuditionAudioSink audition,
        ChainOrderService chainOrder,
        ILogger<AudioPluginBridge> log,
        TxAudioIngest txAudioIngest,
        VstEngineController vstEngine)
        : this(manager, pipeline, new VstBridgeNative(),
               isMoxOn: () => tx.IsMoxOn,
               isMonitorOn: () => pipeline.CurrentEngine?.IsTxMonitorOn ?? false,
               audition: audition,
               chainOrder: chainOrder,
               log,
               isTciTxAudioActive: () => txAudioIngest.IsTciTxAudioActive,
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
        IAuditionAudioSink audition,
        ChainOrderService? chainOrder,
        ILogger<AudioPluginBridge> log,
        Func<bool>? isTciTxAudioActive = null,
        VstEngineController? vstEngine = null)
    {
        _manager = manager;
        _pipeline = pipeline;
        _vstBridge = vstBridge;
        _isMoxOn = isMoxOn;
        _isMonitorOn = isMonitorOn;
        _isTciTxAudioActive = isTciTxAudioActive ?? (() => false);
        _audition = audition;
        _chainOrder = chainOrder;
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
        IAuditionAudioSink? audition = null,
        bool previewEnabled = true,
        bool engineIsWdsp = true,
        Func<bool>? isTciTxAudioActive = null)
    {
        _manager = null!;
        _pipeline = null!;
        _vstBridge = null!;
        _isMoxOn = isMoxOn;
        _isMonitorOn = isMonitorOn;
        _isTciTxAudioActive = isTciTxAudioActive ?? (() => false);
        _audition = audition ?? new NoOpAuditionAudioSink();
        _chainOrder = null;
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

    /// <summary>
    /// Chain-level signal meters (linear peak) for the Audio Suite IN /
    /// OUT bars: the level entering the TX insert chain and the level
    /// leaving it. Both read 0 until the chain processes audio — which
    /// only happens during MOX/TX or desktop-mode audition (mic
    /// preview). Surfaced via GET /api/audio-suite/chain/meters.
    /// </summary>
    public (float In, float Out) ChainMeters =>
        _vstEngine is { IsActive: true } ? (_engineInPeak, _engineOutPeak) : _chain.Meters;

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
        lock (_lock) _idToPlugin.TryGetValue(pluginId, out plugin);
        if (plugin is null) { result = EditorActionResult.NotFound; return null; }
        if (plugin is not VstHostAudioPlugin vst) { result = EditorActionResult.NotAVst; return null; }
        result = EditorActionResult.Ok;
        return vst;
    }

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

        // Adopt any plugins already active (PluginManager might have
        // finished startup before us depending on hosted-service ordering).
        foreach (var p in _manager.Active) OnPluginActivated(p);

        // Install the handler on whatever engine is currently live.
        if (_pipeline.CurrentEngine is { } engine) AttachToEngine(engine);

        _log.LogInformation("AudioPluginBridge online.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _manager.PluginActivated   -= OnPluginActivated;
        _manager.PluginDeactivated -= OnPluginDeactivated;
        _pipeline.EngineChanged    -= OnEngineChanged;

        if (_chainOrder is not null && _orderChangedHandler is not null)
        {
            _chainOrder.OrderChanged -= _orderChangedHandler;
            _orderChangedHandler = null;
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
                input.CopyTo(output);
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
            input.CopyTo(output);
            return;
        }

        int sampleCount = frames * channels;
        var ctx = new AudioBlockContext(sampleRate, channels, frames, sampleTime: 0, mox: true);
        _chain.Process(input, output, ctx);
        DspPipelineService.SanitizeAudioBuffer(output[..sampleCount]);
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
    private bool TryProcessThroughEngine(ReadOnlySpan<float> input, Span<float> output, AudioBlockContext ctx)
    {
        var vst = _vstEngine;
        if (vst is not { IsActive: true }) return false;
        int sampleCount = ctx.Frames * ctx.Channels;
        if (Interlocked.CompareExchange(ref _engineGate, 1, 0) != 0)
        {
            input.CopyTo(output);            // other thread owns the engine this instant
            DspPipelineService.SanitizeAudioBuffer(output[..sampleCount]);
            _engineInPeak = BlockPeak(input);
            _engineOutPeak = BlockPeak(output[..sampleCount]);
            return true;
        }
        try { vst.Process(input, output, ctx); }
        finally { Volatile.Write(ref _engineGate, 0); }
        DspPipelineService.SanitizeAudioBuffer(output[..sampleCount]);
        // Sample master IN/OUT peaks so the Audio Suite meters stay live in VST
        // mode (the native chain's own metering path didn't run).
        _engineInPeak = BlockPeak(input);
        _engineOutPeak = BlockPeak(output[..sampleCount]);
        return true;
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
    /// for the audition feed). The two paths are mutually exclusive in
    /// time except for the microsecond-scale overlap window at a MOX
    /// edge; the caller-supplied scratch span on
    /// <see cref="AudioChain.Process(ReadOnlySpan{float}, Span{float},
    /// Span{float}, AudioBlockContext)"/> ensures the two paths never
    /// collide on the chain's <c>_scratch</c> buffer in that window.</para>
    ///
    /// <para>Output samples are discarded — the only side effect is
    /// updating each plugin's last-input / last-output / last-GR
    /// meter fields, which the REST <c>/meters</c> polling surfaces
    /// to the React panels.</para>
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
        // well under the realtime stackalloc budget. The output is
        // either pushed to the audition sink (when the operator has
        // engaged the Audio Suite "Audition" toggle) or discarded; in
        // both cases the side-effect on each plugin's last-meter fields
        // is what drives the IN / OUT / GR animation in the panels.
        Span<float> previewOut = stackalloc float[mic.Length];
        var ctx = new AudioBlockContext(
            sampleRate: sampleRate,
            channels: 1,
            frames: mic.Length,
            sampleTime: 0,
            mox: false);

        // VST mode: route the live mic block through the out-of-process engine so
        // the operator can monitor and meter the VST chain OFF-AIR, exactly as the
        // native chain animates its meters here on-bench. The engine processes the
        // block, so the VST's own GUI meters move and the audition feed below plays
        // the VST's output (not the native chain's). When the engine isn't the
        // active route, fall through to the native in-process chain unchanged.
        if (!TryProcessThroughEngine(mic, previewOut, ctx))
        {
            Span<float> previewScratch = stackalloc float[mic.Length];
            _chain.Process(mic, previewOut, previewScratch, ctx);
        }
        DspPipelineService.SanitizeAudioBuffer(previewOut);

        // Audition path: when the operator has audition turned on, push
        // the chain's output into the audition sink so they hear what
        // the plugins are doing to their voice through the same
        // headphones/speakers that play RX audio. The sink no-ops
        // internally when audition is disabled, but the IsEnabled
        // short-circuit here keeps the hot-path "audition off" case
        // free of even a virtual call.
        if (_audition.IsEnabled)
        {
            _audition.PublishAudition(previewOut, sampleRate);
        }
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

        // Realtime-safe init off-thread before the chain dispatches. The
        // chain itself doesn't call Initialize; we do it here so plugins
        // get a chance to allocate / open resources before their first
        // Process() call.
        try
        {
            audioPlugin.InitializeAudioAsync(
                new AudioHost(slotName: p.Loaded.Manifest.Audio?.Slot ?? "tx.post-leveler"),
                CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Audio plugin {Id} InitializeAudioAsync threw; clearing slot {Slot}",
                p.Loaded.Manifest.Id, slot);
            lock (_lock)
            {
                if (slot >= 0) _chain.ClearSlot(slot);
                _idToSlot.Remove(p.Loaded.Manifest.Id);
                _idToPlugin.Remove(p.Loaded.Manifest.Id);
            }
            return;
        }

        // Note: master bypass is owned by AudioChainMasterBypassService now;
        // the bridge no longer auto-toggles _chain.MasterBypassed on attach.
        // An attach into an operator-bypassed chain leaves the chain inert
        // (correct behaviour — operator's choice stays sticky).
        RefreshPreviewEnabled();
        if (parked)
            _log.LogInformation(
                "Audio plugin {Id} attached (parked — initialized, not in the live chain)",
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

        IAudioPlugin? attached = null;
        int slot;
        lock (_lock)
        {
            if (!_idToSlot.TryGetValue(p.Loaded.Manifest.Id, out slot)) return;
            _idToSlot.Remove(p.Loaded.Manifest.Id);
            _idToPlugin.Remove(p.Loaded.Manifest.Id);
            attached = _chain.GetSlot(slot);
            _chain.ClearSlot(slot);
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
        _chainOrder?.OnPluginDetached(p.Loaded.Manifest.Id);
        RefreshPreviewEnabled();

        if (attached is null) return;
        try
        {
            attached.ShutdownAudioAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Audio plugin {Id} ShutdownAudioAsync threw", p.Loaded.Manifest.Id);
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
        int slot;
        bool firstRx;
        lock (_lock)
        {
            slot = FindFreeRxSlot();
            if (slot < 0)
            {
                _log.LogWarning(
                    "RX audio chain full ({Max} slots); ignoring plugin {Id}",
                    _rxChain.SlotCount, p.Loaded.Manifest.Id);
                return;
            }
            firstRx = _rxIdToSlot.Count == 0;
            _rxIdToSlot[p.Loaded.Manifest.Id] = slot;
            _rxChain.SetSlot(slot, audioPlugin);
        }

        try
        {
            audioPlugin.InitializeAudioAsync(new AudioHost(slotName), CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "RX audio plugin {Id} InitializeAudioAsync threw; clearing rx slot {Slot}",
                p.Loaded.Manifest.Id, slot);
            lock (_lock)
            {
                _rxChain.ClearSlot(slot);
                _rxIdToSlot.Remove(p.Loaded.Manifest.Id);
            }
            return;
        }

        // Install the realtime runner on the pipeline only once an RX plugin is
        // actually present. _pipeline is null only on the realtime-only test
        // ctor, whose lifecycle methods are never invoked.
        if (firstRx) _pipeline.SetRxAudioPluginHandler(ProcessRxBlock);

        _log.LogInformation(
            "RX audio plugin {Id} attached to rx slot {Slot} (slot={SlotName})",
            p.Loaded.Manifest.Id, slot, slotName);
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
        bool lastRx;
        lock (_lock)
        {
            if (!_rxIdToSlot.TryGetValue(p.Loaded.Manifest.Id, out var slot)) return false;
            _rxIdToSlot.Remove(p.Loaded.Manifest.Id);
            attached = _rxChain.GetSlot(slot);
            _rxChain.ClearSlot(slot);
            lastRx = _rxIdToSlot.Count == 0;
        }

        if (lastRx) _pipeline.SetRxAudioPluginHandler(null);

        if (attached is not null)
        {
            try { attached.ShutdownAudioAsync(CancellationToken.None).GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "RX audio plugin {Id} ShutdownAudioAsync threw", p.Loaded.Manifest.Id);
            }
            if (attached is IAsyncDisposable ad)
            {
                try { ad.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                catch { /* best effort */ }
            }
        }
        _log.LogInformation("RX audio plugin {Id} detached", p.Loaded.Manifest.Id);
        return true;
    }

    private int FindFreeRxSlot()
    {
        for (int i = 0; i < _rxChain.SlotCount; i++)
            if (_rxChain.GetSlot(i) is null) return i;
        return -1;
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
        var ctx = new AudioBlockContext(sampleRate, channels: 1, frames: frames, sampleTime: 0, mox: false);
        _rxChain.Process(audio, audio, ctx);
    }

    /// <summary>Returns the plugin's IAudioPlugin, or synthesises a
    /// <see cref="VstHostAudioPlugin"/> if the manifest declares audio.vst3Path,
    /// or null if the plugin contributes no audio.</summary>
    private IAudioPlugin? ResolveAudioPlugin(ActivatedPlugin p)
    {
        if (p.Loaded.Plugin is IAudioPlugin direct) return direct;

        var audio = p.Loaded.Manifest.Audio;
        if (audio is { Vst3Path: { Length: > 0 } })
        {
            return new VstHostAudioPlugin(
                bridge: _vstBridge,
                manifestAudio: audio,
                pluginRootPath: p.Loaded.PluginDir,
                displayName: p.Loaded.Manifest.Name,
                log: _log);
        }
        return null;
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
        lock (_lock) ReapplySlotsUnderLock();
        _log.LogInformation("Audio plugin chain re-slotted to operator order");
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
        public AudioHost(string slotName) { Slot = slotName; }
        public int CurrentSampleRate => 48000;
        public int CurrentChannels => 1;
        public int CurrentBlockSize => 256;
        public string Slot { get; }
    }
}
