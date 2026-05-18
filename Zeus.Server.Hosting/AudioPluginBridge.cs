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

public sealed class AudioPluginBridge : IHostedService, IAsyncDisposable
{
    private readonly PluginManager _manager;
    private readonly DspPipelineService _pipeline;
    private readonly IVstBridgeNative _vstBridge;
    private readonly Func<bool> _isMoxOn;
    private readonly Func<bool> _isMonitorOn;
    private readonly IAuditionAudioSink _audition;
    private readonly ILogger<AudioPluginBridge> _log;
    private readonly AudioChain _chain = new();
    private readonly Dictionary<string, int> _idToSlot = new();
    private readonly object _lock = new();
    // Pre-MOX preview gate — true ONLY when a) at least one IAudioPlugin
    // is attached AND b) the live DSP engine is WdspDspEngine (the
    // synthetic engine has no realtime TX path so a preview against it
    // would be meaningless). Volatile single-reader on the miniaudio
    // capture thread; written from the control-thread lifecycle paths.
    private volatile bool _previewEnabled;
    private bool _engineIsWdsp;

    public AudioPluginBridge(
        PluginManager manager,
        DspPipelineService pipeline,
        TxService tx,
        IAuditionAudioSink audition,
        ILogger<AudioPluginBridge> log)
        : this(manager, pipeline, new VstBridgeNative(),
               isMoxOn: () => tx.IsMoxOn,
               isMonitorOn: () => pipeline.CurrentEngine?.IsTxMonitorOn ?? false,
               audition: audition,
               log) { }

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
        ILogger<AudioPluginBridge> log)
    {
        _manager = manager;
        _pipeline = pipeline;
        _vstBridge = vstBridge;
        _isMoxOn = isMoxOn;
        _isMonitorOn = isMonitorOn;
        _audition = audition;
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
        bool engineIsWdsp = true)
    {
        _manager = null!;
        _pipeline = null!;
        _vstBridge = null!;
        _isMoxOn = isMoxOn;
        _isMonitorOn = isMonitorOn;
        _audition = audition ?? new NoOpAuditionAudioSink();
        _log = log;
        _engineIsWdsp = engineIsWdsp;
        _previewEnabled = previewEnabled;
    }

    /// <summary>Current chain (exposed for diagnostics / tests).</summary>
    internal AudioChain Chain => _chain;

    /// <summary>True if the pre-MOX preview tap is active (Wdsp engine + plugins attached).</summary>
    internal bool PreviewEnabled => _previewEnabled;

    public Task StartAsync(CancellationToken ct)
    {
        _manager.PluginActivated   += OnPluginActivated;
        _manager.PluginDeactivated += OnPluginDeactivated;
        _pipeline.EngineChanged    += OnEngineChanged;

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

        if (_pipeline.CurrentEngine is WdspDspEngine wdsp)
            wdsp.SetTxAudioPluginHandler(null);

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
        var ctx = new AudioBlockContext(sampleRate, channels, frames, sampleTime: 0, mox: true);
        _chain.Process(input, output, ctx);
    }

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
        Span<float> previewScratch = stackalloc float[mic.Length];
        var ctx = new AudioBlockContext(
            sampleRate: sampleRate,
            channels: 1,
            frames: mic.Length,
            sampleTime: 0,
            mox: false);
        _chain.Process(mic, previewOut, previewScratch, ctx);

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

        int slot;
        lock (_lock)
        {
            slot = FindFreeSlot();
            if (slot < 0)
            {
                _log.LogWarning(
                    "Audio chain full (8 slots); ignoring plugin {Id}",
                    p.Loaded.Manifest.Id);
                return;
            }
            _idToSlot[p.Loaded.Manifest.Id] = slot;
            _chain.SetSlot(slot, audioPlugin);
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
                _chain.ClearSlot(slot);
                _idToSlot.Remove(p.Loaded.Manifest.Id);
            }
            return;
        }

        _chain.MasterEnabled = true;
        RefreshPreviewEnabled();
        _log.LogInformation(
            "Audio plugin {Id} attached to slot {Slot}",
            p.Loaded.Manifest.Id, slot);
    }

    private void OnPluginDeactivated(ActivatedPlugin p)
    {
        IAudioPlugin? attached = null;
        int slot;
        lock (_lock)
        {
            if (!_idToSlot.TryGetValue(p.Loaded.Manifest.Id, out slot)) return;
            _idToSlot.Remove(p.Loaded.Manifest.Id);
            attached = _chain.GetSlot(slot);
            _chain.ClearSlot(slot);
            if (_idToSlot.Count == 0) _chain.MasterEnabled = false;
        }
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

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await _chain.DisposeAsync().ConfigureAwait(false);
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
