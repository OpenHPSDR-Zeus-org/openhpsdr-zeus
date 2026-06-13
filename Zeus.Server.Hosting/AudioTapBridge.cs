// SPDX-License-Identifier: GPL-2.0-or-later
//
// AudioTapBridge — fans the host's audio streams out to read-only plugin taps:
//   • RX band audio       -> IRxAudioTapPlugin.OnRxAudio
//   • raw TX mic audio    -> ITxAudioTapPlugin.OnTxMicAudio
//   • processed TX (air)  -> ITxAudioTapPlugin.OnTxAirAudio
// Taps observe audio but never alter it and never occupy an insert-chain slot.
// The bridge adds NO code to the audio hot paths themselves — it subscribes to
// events those paths already raise (DspPipelineService.RxAudioAvailable /
// .TxMonitorAudioAvailable and TxAudioIngest.MicPcmTapped). Each fan-out reads
// a copy-on-write array with no lock and no allocation on the audio thread.

using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts.Audio;
using Zeus.Plugins.Contracts.Extensions;
using Zeus.Plugins.Host;

namespace Zeus.Server;

public sealed class AudioTapBridge : IHostedService
{
    private readonly PluginManager _manager;
    private readonly DspPipelineService _pipeline;
    private readonly TxAudioIngest _txIngest;
    private readonly ILogger<AudioTapBridge> _log;

    private readonly object _lock = new();
    private readonly Dictionary<string, IRxAudioTapPlugin> _rxById = new();
    private readonly Dictionary<string, ITxAudioTapPlugin> _txById = new();
    private volatile IRxAudioTapPlugin[] _rxTaps = Array.Empty<IRxAudioTapPlugin>();
    private volatile ITxAudioTapPlugin[] _txTaps = Array.Empty<ITxAudioTapPlugin>();

    public AudioTapBridge(
        PluginManager manager,
        DspPipelineService pipeline,
        TxAudioIngest txIngest,
        ILogger<AudioTapBridge> log)
    {
        _manager = manager;
        _pipeline = pipeline;
        _txIngest = txIngest;
        _log = log;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _manager.PluginActivated   += OnPluginActivated;
        _manager.PluginDeactivated += OnPluginDeactivated;
        _pipeline.RxAudioAvailable += OnRxAudio;
        _pipeline.TxMonitorAudioAvailable += OnTxAirAudio;
        _txIngest.MicPcmTapped += OnTxMicBytes;

        foreach (var p in _manager.Active) OnPluginActivated(p);
        _log.LogInformation("AudioTapBridge online.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _txIngest.MicPcmTapped -= OnTxMicBytes;
        _pipeline.TxMonitorAudioAvailable -= OnTxAirAudio;
        _pipeline.RxAudioAvailable -= OnRxAudio;
        _manager.PluginActivated   -= OnPluginActivated;
        _manager.PluginDeactivated -= OnPluginDeactivated;

        IRxAudioTapPlugin[] rx;
        lock (_lock)
        {
            rx = _rxTaps;
            _rxById.Clear();
            _txById.Clear();
            _rxTaps = Array.Empty<IRxAudioTapPlugin>();
            _txTaps = Array.Empty<ITxAudioTapPlugin>();
        }
        foreach (var t in rx) ShutdownRx(t);
        return Task.CompletedTask;
    }

    // ---- Audio-thread fan-out (no alloc, no lock, no throw escaping) -------

    private void OnRxAudio(int receiver, int sampleRate, ReadOnlyMemory<float> block)
    {
        var taps = _rxTaps;
        if (taps.Length == 0) return;
        var span = block.Span;
        var ctx = new AudioBlockContext(sampleRate, 1, span.Length, 0, mox: false, receiver: receiver);
        for (int i = 0; i < taps.Length; i++)
        {
            try { taps[i].OnRxAudio(span, ctx); } catch { /* a tap must never break audio */ }
        }
    }

    private void OnTxAirAudio(int receiver, int sampleRate, ReadOnlyMemory<float> block)
    {
        var taps = _txTaps;
        if (taps.Length == 0) return;
        var span = block.Span;
        var ctx = new AudioBlockContext(sampleRate, 1, span.Length, 0, mox: true);
        for (int i = 0; i < taps.Length; i++)
        {
            try { taps[i].OnTxAirAudio(span, ctx); } catch { /* ignore */ }
        }
    }

    private void OnTxMicBytes(ReadOnlyMemory<byte> f32lePayload)
    {
        var taps = _txTaps;
        if (taps.Length == 0) return;
        // f32le bytes reinterpreted as float — zero-copy on little-endian RIDs.
        var span = MemoryMarshal.Cast<byte, float>(f32lePayload.Span);
        var ctx = new AudioBlockContext(DspPipelineService.AudioOutputRateHz, 1, span.Length, 0, mox: true);
        for (int i = 0; i < taps.Length; i++)
        {
            try { taps[i].OnTxMicAudio(span, ctx); } catch { /* ignore */ }
        }
    }

    // ---- Plugin lifecycle --------------------------------------------------

    private void OnPluginActivated(ActivatedPlugin p)
    {
        var plugin = p.Loaded.Plugin;
        var id = p.Loaded.Manifest.Id;
        bool any = false;

        if (plugin is IRxAudioTapPlugin rx)
        {
            try
            {
                rx.InitializeTapAsync(new TapHost(), CancellationToken.None).GetAwaiter().GetResult();
                lock (_lock) { _rxById[id] = rx; _rxTaps = _rxById.Values.ToArray(); }
                any = true;
            }
            catch (Exception ex) { _log.LogError(ex, "RX tap {Id} InitializeTapAsync threw; not subscribing", id); }
        }

        if (plugin is ITxAudioTapPlugin tx)
        {
            lock (_lock) { _txById[id] = tx; _txTaps = _txById.Values.ToArray(); }
            any = true;
        }

        if (any) _log.LogInformation("Audio tap {Id} subscribed (rx={Rx} tx={Tx})", id, plugin is IRxAudioTapPlugin, plugin is ITxAudioTapPlugin);
    }

    private void OnPluginDeactivated(ActivatedPlugin p)
    {
        var id = p.Loaded.Manifest.Id;
        IRxAudioTapPlugin? rx = null;
        lock (_lock)
        {
            if (_rxById.Remove(id, out rx)) _rxTaps = _rxById.Values.ToArray();
            if (_txById.Remove(id, out _)) _txTaps = _txById.Values.ToArray();
        }
        if (rx is not null) ShutdownRx(rx);
    }

    private void ShutdownRx(IRxAudioTapPlugin tap)
    {
        try { tap.ShutdownTapAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            // Best-effort shutdown: never let a tap (or the logger itself, which
            // may already be torn down during host StopAsync) abort the stop
            // sequence for the other taps.
            try { _log.LogWarning(ex, "RX tap ShutdownTapAsync threw"); } catch { /* logger disposed */ }
        }
    }

    private sealed class TapHost : IAudioHost
    {
        public int CurrentSampleRate => DspPipelineService.AudioOutputRateHz;
        public int CurrentChannels => 1;
        public int CurrentBlockSize => 2048;
        public string Slot => "audio.tap";
    }
}
