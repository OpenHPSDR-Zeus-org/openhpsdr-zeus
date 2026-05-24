// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// Host-side CW keyer. WDSP's CWX module is not exported by the libwdsp
// binaries we ship today (verified 2026-05-24: `nm -D libwdsp.so | grep -i
// CWX` is empty on linux-x64 and osx-arm64), so we generate the IQ here
// instead and push it straight into the protocol-1 TX ring. The shape we
// produce — a single tone at the CW pitch with raised-cosine rise/fall —
// is what piHPSDR's `transmitter_send_cw` ends up emitting on the wire
// anyway, so this is a wire-compatible substitute, not a fallback.
//
// PR 1 of zeus-4np epic: validates the end-to-end keying path (encoder →
// engine → TxIqRing → EP2 → RF). UI, macros, TCI keyer, and sidetone
// monitor land in subsequent PRs.

using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zeus.Contracts;
using Zeus.Protocol1;

namespace Zeus.Server;

/// <summary>
/// Queues text-to-CW jobs and drives them out as host-generated IQ. One
/// instance per process — registered as a singleton hosted service. The
/// engine claims MOX as <see cref="MoxSource.Cwx"/> for the life of each
/// message and releases it on completion; <see cref="AbortAsync"/> is the
/// hard cut for operator override, <see cref="StopAsync(CancellationToken)"/>
/// is the graceful shutdown path used by host shutdown.
///
/// Audio shape: a 600 Hz tone (sign-flipped for CWL) at 48 kHz, envelope
/// shaped with a 5 ms raised-cosine on each edge to keep the on-air signal
/// inside the CW passband. Amplitude is full-scale (operator drives PA
/// power via the normal drive % slider, not here).
/// </summary>
public sealed class CwEngine : BackgroundService
{
    // TX-side ring sample rate. Both P1 and P2 backends pull from TxIqRing
    // at 48 kHz; see Zeus.Protocol1.TxIqRing class doc.
    public const int SampleRateHz = 48_000;
    // Raised-cosine ramp on each key edge. 5 ms ≈ ±100 Hz of skirt energy at
    // the CW pitch — well under the WDSP RX CW bandpass (250 Hz wide). Below
    // 2 ms produces audible clicks on the air; above 10 ms starts to round
    // off short dits at 30+ WPM.
    private const int RampMs = 5;
    // Speed bounds. 5 WPM = 240 ms dit (very slow CW); 50 WPM = 24 ms dit
    // (faster than most hand keys can copy). Clamping at ingest keeps the
    // ramp/dit math sane and prevents divide-by-tiny artefacts.
    private const int WpmMin = 5;
    private const int WpmMax = 50;
    private const int WpmDefault = 20;

    // Chunk size used by the playback loop. 480 samples = 10 ms at 48 kHz —
    // small enough that the ring stays well under its 340 ms drop-oldest
    // threshold, large enough that the loop only wakes 100 times per second
    // of TX (negligible CPU). Stay coherent with the ring's `Write(span)`
    // path which is per-block.
    private const int ChunkSamples = 480;

    private readonly TxService _tx;
    private readonly RadioService _radio;
    private readonly TxIqRing _ring;
    private readonly ILogger<CwEngine> _log;
    private readonly Channel<CwJob> _jobs = Channel.CreateUnbounded<CwJob>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    // Aborts the currently-playing job. Recreated on each new job so abort
    // doesn't poison the next send.
    private CancellationTokenSource? _currentAbort;
    private readonly object _abortLock = new();

    /// <summary>Raised on every state transition. Subscribers must not block;
    /// fired from the playback worker thread.</summary>
    public event Action<CwEngineStatus>? Status;

    public CwEngine(TxService tx, RadioService radio, TxIqRing ring, ILogger<CwEngine> log)
    {
        _tx = tx;
        _radio = radio;
        _ring = ring;
        _log = log;
        // Drop the current send if the operator overrides MOX from the UI
        // (or a trip happens). Owner==null on the falling edge means MOX
        // was just released; if it wasn't us doing the release, cancel.
        _tx.TxActiveChanged += OnTxActiveChanged;
    }

    /// <summary>
    /// Enqueue <paramref name="text"/> for transmission at <paramref name="wpm"/>
    /// (null = engine default, currently 20). Returns immediately; actual
    /// keying happens on the worker thread.
    /// </summary>
    public ValueTask SendAsync(string text, int? wpm, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(text);
        int effective = Math.Clamp(wpm ?? WpmDefault, WpmMin, WpmMax);
        return _jobs.Writer.WriteAsync(new CwJob(text, effective), ct);
    }

    /// <summary>Hard cancel. Drains the queue and signals the in-flight
    /// playback to drop the rest of the symbols. MOX falls on the next
    /// playback tick.</summary>
    public void Abort(string reason = "operator abort")
    {
        // Drain queue first so cancelled jobs don't get picked up.
        while (_jobs.Reader.TryRead(out _)) { /* discard */ }
        CancellationTokenSource? cts;
        lock (_abortLock) cts = _currentAbort;
        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { /* race with worker disposal */ }
        _log.LogInformation("cw.abort reason={Reason}", reason);
    }

    /// <summary>Tests only: snapshot queue depth without taking work.</summary>
    internal int PendingJobCount => _jobs.Reader.Count;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            CwJob job;
            try { job = await _jobs.Reader.ReadAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            var jobCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            lock (_abortLock) _currentAbort = jobCts;
            try
            {
                await PlayJobAsync(job, jobCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Either operator abort or host shutdown. Either way, unkey
                // and re-emit Idle if MOX is still ours.
                Notify(new CwEngineStatus(
                    CwEngineState.Aborting, job.Text, job.Wpm, _jobs.Reader.Count,
                    Reason: "aborted"));
                TryReleaseMox("aborted");
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "cw.play failed text={Text} wpm={Wpm}", Truncate(job.Text), job.Wpm);
                TryReleaseMox("error");
            }
            finally
            {
                lock (_abortLock)
                {
                    if (ReferenceEquals(_currentAbort, jobCts)) _currentAbort = null;
                }
                jobCts.Dispose();
            }

            // Emit Idle once the queue actually drains. Mid-queue jobs roll
            // into the next iteration without a flicker.
            if (_jobs.Reader.Count == 0)
                Notify(new CwEngineStatus(CwEngineState.Idle, string.Empty, 0, 0));
        }
    }

    private async Task PlayJobAsync(CwJob job, CancellationToken ct)
    {
        var snap = _radio.Snapshot();
        int pitchHz = PitchForMode(snap.Mode);

        if (!_tx.TrySetMox(true, MoxSource.Cwx, out var err))
        {
            _log.LogWarning("cw.mox.refused text={Text} reason={Err}", Truncate(job.Text), err);
            Notify(new CwEngineStatus(
                CwEngineState.Idle, job.Text, job.Wpm, _jobs.Reader.Count,
                Reason: err ?? "MOX refused"));
            return;
        }
        Notify(new CwEngineStatus(CwEngineState.Sending, job.Text, job.Wpm, _jobs.Reader.Count));
        _log.LogInformation("cw.send text={Text} wpm={Wpm} pitch={Pitch}Hz",
            Truncate(job.Text), job.Wpm, pitchHz);

        // Phase accumulator runs continuously across symbols so the tone
        // stays coherent through inter-element gaps (no phase pop on
        // each dit-onset). Reset per-job.
        double phase = 0.0;
        double phaseStep = 2.0 * Math.PI * pitchHz / SampleRateHz;
        // Scratch buffer for chunked IQ writes. 2 floats per sample.
        var iq = new float[ChunkSamples * 2];

        foreach (var symbol in MorseEncoder.Encode(job.Text, job.Wpm))
        {
            ct.ThrowIfCancellationRequested();
            int totalSamples = (int)((long)symbol.DurationMs * SampleRateHz / 1000);
            int rampSamples = Math.Min(
                (int)((long)RampMs * SampleRateHz / 1000),
                totalSamples / 2);

            int written = 0;
            while (written < totalSamples)
            {
                ct.ThrowIfCancellationRequested();
                int n = Math.Min(ChunkSamples, totalSamples - written);
                for (int i = 0; i < n; i++)
                {
                    double env = symbol.KeyDown
                        ? RaisedCosineEnvelope(written + i, totalSamples, rampSamples)
                        : 0.0;
                    iq[2 * i] = (float)(env * Math.Cos(phase));
                    iq[2 * i + 1] = (float)(env * Math.Sin(phase));
                    phase += phaseStep;
                    // Keep phase bounded so cos/sin stays numerically clean
                    // over multi-minute transmissions.
                    if (phase > 2.0 * Math.PI) phase -= 2.0 * Math.PI;
                }
                _ring.Write(new ReadOnlySpan<float>(iq, 0, 2 * n));
                written += n;
                // Pace ourselves to the ring drain rate so the ring stays
                // well below its 16384-pair drop-oldest threshold. EP2 drains
                // ~42k pairs/s; we write at the same average rate, so a
                // (chunk/rate) sleep keeps the ring at ~one-chunk depth.
                int sleepMs = Math.Max(1, (n * 1000) / SampleRateHz - 1);
                try { await Task.Delay(sleepMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
            }
        }

        // Drop MOX after the final symbol. The ring still has up to ~10 ms
        // of envelope tail queued; wait that long so the radio actually
        // transmits the tail before MOX falls. (Shorter than the natural
        // ring depth — we deliberately keep the ring shallow above.)
        try { await Task.Delay(20, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        TryReleaseMox("done");
    }

    private void TryReleaseMox(string reason)
    {
        // Only release MOX if we still own it — UI or trip may have already
        // dropped it under us. Idempotent at the TxService layer either way.
        if (_tx.MoxOwner == MoxSource.Cwx)
        {
            _tx.TrySetMox(false, MoxSource.Cwx, out _);
            _log.LogInformation("cw.mox.released reason={Reason}", reason);
        }
    }

    private void OnTxActiveChanged(bool active)
    {
        if (active) return;
        // MOX just fell. If we have a job in flight and the falling edge
        // wasn't initiated by us (TryReleaseMox above), the operator hit
        // the UI override or a trip fired — cancel the playback so the
        // remaining symbols don't queue up against a closed transmitter.
        CancellationTokenSource? cts;
        lock (_abortLock) cts = _currentAbort;
        if (cts is null) return;
        // MoxOwner is null after the falling edge regardless of who caused
        // it, so we can't disambiguate "we released" from "UI overrode" by
        // owner alone. We don't need to: the worker's own release path
        // tolerates a redundant cancel (the linked-token CTS is one-shot
        // and the worker's loop tail finishes the next iteration silently).
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Test seam: precompute the IQ stream for <paramref name="text"/>
    /// at <paramref name="wpm"/> and <paramref name="mode"/>. Used to assert
    /// the wire shape without standing up a hub/ring/transport.</summary>
    internal static float[] RenderForTest(string text, int wpm, RxMode mode)
    {
        int pitchHz = PitchForMode(mode);
        double phase = 0.0;
        double phaseStep = 2.0 * Math.PI * pitchHz / SampleRateHz;
        var buf = new System.Collections.Generic.List<float>();
        foreach (var sym in MorseEncoder.Encode(text, wpm))
        {
            int total = (int)((long)sym.DurationMs * SampleRateHz / 1000);
            int ramp = Math.Min((int)((long)RampMs * SampleRateHz / 1000), total / 2);
            for (int i = 0; i < total; i++)
            {
                double env = sym.KeyDown ? RaisedCosineEnvelope(i, total, ramp) : 0.0;
                buf.Add((float)(env * Math.Cos(phase)));
                buf.Add((float)(env * Math.Sin(phase)));
                phase += phaseStep;
                if (phase > 2.0 * Math.PI) phase -= 2.0 * Math.PI;
            }
        }
        return buf.ToArray();
    }

    private void Notify(CwEngineStatus status)
    {
        try { Status?.Invoke(status); }
        catch (Exception ex) { _log.LogWarning(ex, "cw.status subscriber threw"); }
    }

    private static int PitchForMode(RxMode mode) => mode switch
    {
        // CWU: LO sits below the displayed VFO by CwPitchHz (see CwOffset),
        // so +pitch baseband puts the carrier ON the displayed VFO.
        RxMode.CWU => CwOffset.CwPitchHz,
        // CWL: mirror image — LO above VFO, baseband tone is -pitch.
        RxMode.CWL => -CwOffset.CwPitchHz,
        // Operator not in CW. We still key — they may want a carrier on
        // the displayed VFO for testing — but with no offset.
        _ => 0,
    };

    private static double RaisedCosineEnvelope(int sample, int total, int ramp)
    {
        // Plateau region.
        if (sample >= ramp && sample < total - ramp) return 1.0;
        // Rising edge: 0.5 (1 - cos(π·t/ramp)) — Tukey window's leading half.
        if (sample < ramp)
            return 0.5 * (1.0 - Math.Cos(Math.PI * sample / ramp));
        // Falling edge: same curve mirrored.
        int into = sample - (total - ramp);
        return 0.5 * (1.0 - Math.Cos(Math.PI * (ramp - into) / ramp));
    }

    private static string Truncate(string s) => s.Length <= 40 ? s : s[..40] + "…";

    private readonly record struct CwJob(string Text, int Wpm);
}
