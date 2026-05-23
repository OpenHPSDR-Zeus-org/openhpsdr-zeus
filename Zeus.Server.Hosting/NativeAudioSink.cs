// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Brian Keating (EI6LF) and contributors.
//
// Desktop-mode RX audio sink: drains demodulated audio (mono float32 48 kHz,
// produced by DspPipelineService) directly into the OS default playback
// device via miniaudio. The WebSocket fan-out is skipped entirely in this
// mode — the SPA's audio-decoder is opted out by Phase 2c.
//
// RxAfGainDb is already applied upstream in WDSP via SetRXAPanelGain1 before
// the AudioFrame is produced, so this sink does NOT add any software gain
// stage. The operator's volume slider drives the level for free.

using System.Threading;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// <see cref="IRxAudioSink"/> implementation that pushes RX audio straight
/// to the OS default playback device via miniaudio. Used in desktop mode
/// (<see cref="ZeusHostMode.Desktop"/>) in place of
/// <see cref="WebSocketAudioSink"/>.
///
/// <para>Data flow:</para>
/// <list type="number">
/// <item>DSP tick thread calls <see cref="Publish"/> with a mono float32
/// 48 kHz <see cref="AudioFrame"/> (~1024 samples / frame, ~46 Hz).</item>
/// <item><see cref="Publish"/> copies the samples into an in-process SPSC
/// float ring. The frame's underlying buffer is owned by the DSP service
/// and may be reused by the next tick, so we must NOT keep a reference.</item>
/// <item>The miniaudio playback thread calls <see cref="OnPlaybackData"/>
/// asking for N frames of stereo float32 (at whatever rate the device
/// negotiated). The callback drains the ring, duplicates each mono sample
/// to L=R, and writes silence + bumps an underrun counter when the ring is
/// empty.</item>
/// </list>
///
/// <para>Sample rate: miniaudio negotiates the device rate at open time and
/// the device runs at whatever rate the OS gave us; if that's not 48 kHz,
/// miniaudio's internal resampler (configured to linear in the native shim)
/// converts on our behalf when we ask for the device rate via
/// <c>preferSampleRate=48000</c>. We declare we want 48 kHz, miniaudio
/// honours it where possible and resamples behind the scenes otherwise.
/// Either way the playback callback receives buffers at the device's actual
/// rate — and that's exactly what we fill.</para>
///
/// <para>Channels: miniaudio negotiates the device channel count; we ask
/// for stereo (2) but accept whatever it gives. The callback handles any
/// channel count by either passing mono straight through, duplicating
/// L=R for stereo, or replicating across all channels for surround setups.</para>
///
/// <para>Underrun policy: when the ring is empty (DSP thread hasn't produced
/// audio yet — initial connect, between bursts, or backlog spike) we write
/// silence and increment <c>_underrunSamples</c>. A 5-second timer logs the
/// count + resets it; persistent underruns mean either ring sizing is too
/// small or the DSP thread is starved.</para>
/// </summary>
internal sealed class NativeAudioSink : IRxAudioSink, IAuditionAudioSink, IHostedService, IDisposable
{
    private const int FrameRateHz = 48_000;

    // ~1 s @ 48 kHz mono = 49152 samples ≈ 192 KB. Power of two for the
    // bitmask wrap. Even with the playback thread running ~10 ms periods,
    // anything past ~50 ms is just bounded slack to absorb DSP-tick
    // jitter.
    private const int RingCapacity = 65_536;

    // ~250 ms @ 48 kHz mono = 12000 samples ≈ 48 KB. Power of two for the
    // mask wrap. The audition path is sourced from mic capture (one block
    // every 20 ms — 960 samples) so the buffer never needs to hold more
    // than a few hundred ms of slack. A small ring is preferred: on a MOX
    // rising edge, AudioPluginBridge stops pushing into this ring and the
    // tail drains in <300 ms so the operator doesn't keep hearing the
    // pre-MOX tail of their own voice after keying.
    private const int AuditionRingCapacity = 16_384;

    private readonly ILogger<NativeAudioSink> _log;
    // Service-provider-based lookup for TxService, used to subscribe to
    // TxActiveChanged in StartAsync. NativeAudioSink can NOT take TxService
    // as a constructor dep directly: DspPipelineService depends on
    // IRxAudioSink (us), TxService depends on DspPipelineService, so a
    // direct ctor-time dependency creates a DI cycle. Resolving TxService
    // lazily inside StartAsync breaks the cycle — by the time the hosted-
    // service start phase fires, all singletons in the cycle exist.
    private readonly IServiceProvider? _services;
    private readonly FloatSpscRing _ring = new(RingCapacity);
    private readonly FloatSpscRing _auditionRing = new(AuditionRingCapacity);

    // Resolved on Start, kept so Stop can detach the handler cleanly.
    // Null when no TxService was available (legacy test ctor or
    // pre-construction failure).
    private TxService? _tx;
    private Action<bool>? _txActiveHandler;

    private MiniAudioOutput? _output;
    private bool _disposed;

    // Mute flag for the Photino-side Mute/Unmute button. Read on the DSP
    // tick thread inside Publish, written from the REST request thread —
    // volatile is the right tool. On mute we drain the ring so unmute
    // doesn't replay ~1 s of stale audio; the miniaudio device stays
    // open either way so there's no pop and no fight with the OS mixer.
    private volatile bool _muted;

    // Audition enable flag — set via REST /api/audio-suite/audition by
    // the operator toggling the Audio Suite window's Audition button.
    // Read on the miniaudio capture worker thread inside PublishAudition
    // and on the playback worker thread inside OnPlaybackData. Volatile
    // is sufficient: a stale read across a toggle just means one extra
    // (or one missing) audition block, inaudible.
    private volatile bool _auditionEnabled;

    public bool IsMuted => _muted;
    public bool IsEnabled => _auditionEnabled;

    public void SetMuted(bool muted)
    {
        _muted = muted;
        if (muted) _ring.Clear();
    }

    public void SetEnabled(bool enabled)
    {
        _auditionEnabled = enabled;
        // Drain the audition ring on disable so re-enabling doesn't
        // replay the tail of the prior audition session.
        if (!enabled) _auditionRing.Clear();
        _log.LogInformation("audio.native.rx audition {State}", enabled ? "enabled" : "disabled");
    }

    public void PublishAudition(ReadOnlySpan<float> monoSamples, int sampleRate)
    {
        // No-op when audition is off — keeps the realtime path on the
        // mic capture thread cheap (one volatile read + return) when the
        // operator hasn't engaged the feature.
        if (!_auditionEnabled) return;
        if (sampleRate != FrameRateHz) return;   // defence in depth — mic is always 48 kHz
        if (_muted) return;                       // RX mute also silences audition

        _auditionRing.Write(monoSamples);
        // Audition overruns are not interesting enough to track — the
        // mic-capture cadence (960 samples / 20 ms) means the worst case
        // is ~250 ms of stale audition dropped if the playback thread
        // stalls, which is inaudible.
    }

    // Diagnostics — accessed from the audio worker thread; volatile / interlocked
    // suffices since they're write-only there and read-only on the timer thread.
    private long _underrunSamples;
    private long _overrunSamples;
    private long _totalSamplesIn;
    private long _totalSamplesOut;
    private DateTime _lastLogUtc = DateTime.UtcNow;

    public NativeAudioSink(ILogger<NativeAudioSink> log, IServiceProvider? services = null)
    {
        _log = log;
        _services = services;
    }

    /// <summary>
    /// Hosted-service hook: open the miniaudio playback device. Failures are
    /// logged at warning level and the sink degrades to a no-op (every frame
    /// is dropped silently) so the rest of the host still comes up — the
    /// operator gets logs but not a crash if their audio subsystem is
    /// uncooperative.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_output != null) return Task.CompletedTask;
        try
        {
            _output = new MiniAudioOutput(
                onFrames: OnPlaybackData,
                onNotify: OnDeviceNotification,
                preferSampleRate: FrameRateHz,
                preferChannels: 2,
                periodFrames: 480,   // ≈10 ms @ 48 kHz
                periods: 2);
            _output.Start();
            _log.LogInformation(
                "audio.native.rx open rate={Rate}Hz channels={Channels} version={Version}",
                _output.SampleRate, _output.Channels, MiniAudioInterop.Version());
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "audio.native.rx open failed; RX audio output disabled");
            _output?.Dispose();
            _output = null;
        }

        // Subscribe to TX-active edges so we can drain the ring on TX-on.
        // The radio sample clock and the WASAPI playback clock drift relative
        // to each other (the radio runs slightly faster than the soundcard on
        // most Windows systems), so the ring slowly accumulates a backlog
        // over a multi-minute session. Without this hook the operator hears
        // up to ~1.3 sec of stale RX audio after pressing MOX or TUNE before
        // the buffer drains to silence. macOS / Linux see this too in
        // principle but their audio backends drift much less and the ring
        // stays at near-zero steady-state depth, so the clear is a no-op
        // there. See issue #403 for the original symptom report and the
        // diagnostic write-up.
        _tx = _services?.GetService(typeof(TxService)) as TxService;
        if (_tx is not null)
        {
            _txActiveHandler = OnTxActiveChanged;
            _tx.TxActiveChanged += _txActiveHandler;
        }
        return Task.CompletedTask;
    }

    /// <summary>Hosted-service hook: stop the playback device. Idempotent.</summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_tx is not null && _txActiveHandler is not null)
        {
            _tx.TxActiveChanged -= _txActiveHandler;
            _txActiveHandler = null;
        }
        try { _output?.Stop(); }
        catch (Exception ex) { _log.LogWarning(ex, "audio.native.rx stop threw"); }
        return Task.CompletedTask;
    }

    /// <summary>
    /// TxService.TxActiveChanged subscriber. On the rising edge (TX
    /// engaging via MOX, TUN, or TwoTone) drains the RX audio ring so
    /// the operator hears instant silence rather than the accumulated
    /// pre-TX backlog. The audition ring is left alone — audition is
    /// gated separately and pre-MOX preview is meaningful right up to
    /// the MOX edge; the existing audition gates handle the rest.
    /// On falling edge (TX releasing → back to RX) this is a no-op
    /// because the ring is already empty after the drain; new RX
    /// samples land in an empty ring and reach the speaker promptly.
    /// </summary>
    internal void OnTxActiveChanged(bool txActive)
    {
        if (!txActive) return;
        _ring.Clear();
    }

    // Test surface — lets unit tests assert the ring's drain behaviour
    // without reaching into private state via reflection. Read on any
    // thread; matches FloatSpscRing.Count's relaxed-reader contract
    // (best-effort snapshot, may be off by one in a race window).
    internal int CurrentRingDepth => _ring.Count;

    public void Publish(in AudioFrame frame)
    {
        // Muted at the door: don't enqueue and let the ring drain to silence
        // on the playback callback's underrun path. Cheaper than gating in
        // the audio worker thread and avoids any sample-rate / channel-count
        // negotiation with the producer.
        if (_muted) return;

        // The DSP tick produces mono float32 @ 48 kHz. We assert the format
        // softly: anything else is logged and dropped rather than corrupting
        // the ring. (The format is set in DspPipelineService.AudioOutputRateHz
        // and the AudioFrame ctor; this is defence in depth.)
        if (frame.Channels != 1 || frame.SampleRateHz != FrameRateHz)
        {
            // Don't spam — these fire at frame rate. Log first occurrence
            // only by ANDing against a never-set flag once dropped.
            return;
        }

        var src = frame.Samples.Span;
        int written = _ring.Write(src);
        if (written < src.Length)
        {
            Interlocked.Add(ref _overrunSamples, src.Length - written);
        }
        Interlocked.Add(ref _totalSamplesIn, src.Length);

        MaybeLog();
    }

    private void OnPlaybackData(Span<float> output, uint frameCount, uint channels)
    {
        // miniaudio's buffer is interleaved float32 sized frameCount * channels.
        // We hold mono samples in the ring; expand to N channels by replication.
        int totalFrames = (int)frameCount;
        int channelsI = (int)channels;

        // Read up to `totalFrames` mono samples from the ring into a small
        // scratch buffer (stack-allocated when small enough). The miniaudio
        // worker thread is the only consumer; stack alloc is safe.
        Span<float> mono = totalFrames <= 4096
            ? stackalloc float[totalFrames]
            : new float[totalFrames];

        int read = _ring.Read(mono);
        if (read < totalFrames)
        {
            // Underrun — zero the remainder.
            mono[read..].Clear();
            Interlocked.Add(ref _underrunSamples, totalFrames - read);
        }

        // Audition mixing: when the operator has audition turned on,
        // sum the audio plugin chain's output into the mono buffer
        // BEFORE channel expansion. We use a second stack-alloc'd
        // scratch span so we don't touch the audition ring on the
        // common audition-off path. Audition underrun is benign — the
        // operator just hears silence for that gap, which is what they
        // expect when the mic isn't producing.
        if (_auditionEnabled)
        {
            Span<float> aud = totalFrames <= 4096
                ? stackalloc float[totalFrames]
                : new float[totalFrames];
            int audRead = _auditionRing.Read(aud);
            // Sum the audition slice into mono. If the audition ring
            // underran we still sum the bytes we got and leave the
            // rest of mono unchanged (RX continues underneath).
            for (int i = 0; i < audRead; i++) mono[i] += aud[i];
        }

        // Expand mono → output channels. channels==1 is the trivial path.
        if (channelsI == 1)
        {
            mono.CopyTo(output);
        }
        else
        {
            // Interleaved write: out[i*ch + c] = mono[i] for c in 0..ch.
            int outIdx = 0;
            for (int i = 0; i < totalFrames; i++)
            {
                float s = mono[i];
                for (int c = 0; c < channelsI; c++)
                {
                    output[outIdx++] = s;
                }
            }
        }

        Interlocked.Add(ref _totalSamplesOut, totalFrames);
    }

    private void OnDeviceNotification(int kind)
    {
        // 1=started, 2=stopped, 3=rerouted, 4=interruption_began,
        // 5=interruption_ended, 6=unlocked.
        // miniaudio reroutes automatically on default-device change (headphone
        // hotplug, BT switch) so no re-init is required — the SAMPLE RATE and
        // channel count may shift, but the data callback keeps firing.
        string label = kind switch
        {
            1 => "started",
            2 => "stopped",
            3 => "rerouted (default device changed)",
            4 => "interruption_began",
            5 => "interruption_ended",
            6 => "unlocked",
            _ => $"kind={kind}",
        };
        _log.LogInformation("audio.native.rx event {Event}", label);
    }

    private void MaybeLog()
    {
        // Cheap throttle — only the producer thread reads/writes _lastLogUtc.
        var now = DateTime.UtcNow;
        if (now - _lastLogUtc < TimeSpan.FromSeconds(5)) return;
        _lastLogUtc = now;

        long inS = Interlocked.Read(ref _totalSamplesIn);
        long outS = Interlocked.Read(ref _totalSamplesOut);
        long under = Interlocked.Exchange(ref _underrunSamples, 0);
        long over = Interlocked.Exchange(ref _overrunSamples, 0);
        // Only log when there's something to flag — otherwise stay quiet
        // on the happy path so dev logs don't fill up.
        if (under == 0 && over == 0) return;
        _log.LogInformation(
            "audio.native.rx stats in={InS} out={OutS} underrun={Under} overrun={Over}",
            inS, outS, under, over);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _output?.Dispose(); }
        catch (Exception ex) { _log.LogWarning(ex, "audio.native.rx dispose threw"); }
        _output = null;
    }
}
