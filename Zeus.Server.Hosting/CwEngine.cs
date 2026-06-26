// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
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
    private readonly StreamingHub _hub;
    private readonly CwSettingsStore _settings;
    private readonly CwSidetoneSource? _sidetone;
    private readonly ILogger<CwEngine> _log;
    private readonly Channel<CwJob> _jobs = Channel.CreateUnbounded<CwJob>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    // .NET's default unbounded channel does NOT implement Reader.Count
    // (CanCount=false), so calling _jobs.Reader.Count throws
    // NotSupportedException. We need the depth for status reporting and
    // idle gating, so track it ourselves via Interlocked. Incremented at
    // the writer (SendAsync, Abort no-op), decremented after a successful
    // worker read.
    private int _pendingJobs;

    // Aborts the currently-playing job. Recreated on each new job so abort
    // doesn't poison the next send.
    private CancellationTokenSource? _currentAbort;
    // True only while a raw-key job (TCI keyer:1) is playing. Read under
    // _abortLock so RawKeyAsync(false) can decide whether the in-flight
    // cancel applies — keyer:0 must not truncate an unrelated text send.
    private bool _currentIsRawKey;
    private readonly object _abortLock = new();

    /// <summary>Raised on every state transition. Subscribers must not block;
    /// fired from the playback worker thread.</summary>
    public event Action<CwEngineStatus>? Status;

    public CwEngine(TxService tx, RadioService radio, TxIqRing ring, StreamingHub hub, CwSettingsStore settings, ILogger<CwEngine> log, CwSidetoneSource? sidetone = null)
    {
        _tx = tx;
        _radio = radio;
        _ring = ring;
        _hub = hub;
        _settings = settings;
        _sidetone = sidetone;
        _log = log;
        // Drop the current send if the operator overrides MOX from the UI
        // (or a trip happens). Owner==null on the falling edge means MOX
        // was just released; if it wasn't us doing the release, cancel.
        _tx.TxActiveChanged += OnTxActiveChanged;
        // Bridge the in-process Status event onto the wire so connected
        // clients (the React macro pad) see state edges without polling.
        Status += BroadcastStatus;
    }

    /// <summary>
    /// Enqueue <paramref name="text"/> for transmission at <paramref name="wpm"/>.
    /// When <paramref name="wpm"/> is null the engine reads the operator's
    /// persisted default from <see cref="CwSettingsStore"/>; if the store
    /// hasn't been initialised yet (test seam) it falls back to
    /// <see cref="WpmDefault"/>. Returns immediately; keying happens on
    /// the worker thread.
    /// </summary>
    public ValueTask SendAsync(string text, int? wpm, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(text);
        int requested = wpm ?? _settings?.Get().Wpm ?? WpmDefault;
        int effective = Math.Clamp(requested, WpmMin, WpmMax);
        Interlocked.Increment(ref _pendingJobs);
        return _jobs.Writer.WriteAsync(new CwJob(text, effective, RawKeyDown: false, DurationMs: null), ct);
    }

    /// <summary>
    /// Manual key-down / key-up entry point used by the TCI <c>keyer:rx,bool</c>
    /// command. Key-down enqueues a raw-key job that emits steady carrier
    /// (with raised-cosine attack/release) until <paramref name="durationMs"/>
    /// expires or a matching key-up arrives. Key-up cancels the in-flight
    /// raw-key job only — a parallel <see cref="SendAsync"/> in progress is
    /// preserved so a stray <c>keyer:0</c> from a contest logger doesn't
    /// truncate a macro mid-message.
    /// </summary>
    public ValueTask RawKeyAsync(bool keyDown, int? durationMs, CancellationToken ct)
    {
        if (keyDown)
        {
            Interlocked.Increment(ref _pendingJobs);
            return _jobs.Writer.WriteAsync(
                new CwJob(string.Empty, 0, RawKeyDown: true, DurationMs: durationMs),
                ct);
        }

        // Selective cancel — see _currentIsRawKey field doc.
        lock (_abortLock)
        {
            if (_currentIsRawKey && _currentAbort is { } cts)
            {
                try { cts.Cancel(); } catch (ObjectDisposedException) { /* race with worker */ }
            }
        }
        return ValueTask.CompletedTask;
    }

    private void BroadcastStatus(CwEngineStatus s)
    {
        try { _hub.Broadcast(CwEngineStatusFrame.FromStatus(s)); }
        catch (Exception ex) { _log.LogWarning(ex, "cw.status hub broadcast failed"); }
    }

    /// <summary>Hard cancel. Drains the queue and signals the in-flight
    /// playback to drop the rest of the symbols. MOX falls on the next
    /// playback tick.</summary>
    public void Abort(string reason = "operator abort")
    {
        // Drain queue first so cancelled jobs don't get picked up. Decrement
        // the depth counter for every job we discard so the in-flight Status
        // reports stay accurate.
        while (_jobs.Reader.TryRead(out _)) Interlocked.Decrement(ref _pendingJobs);
        CancellationTokenSource? cts;
        lock (_abortLock) cts = _currentAbort;
        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { /* race with worker disposal */ }
        _log.LogInformation("cw.abort reason={Reason}", reason);
    }

    /// <summary>Tests only: snapshot queue depth without taking work.</summary>
    internal int PendingJobCount => Volatile.Read(ref _pendingJobs);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            CwJob job;
            try { job = await _jobs.Reader.ReadAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            // Decrement BEFORE the job runs so QueueDepth reported in the
            // Sending status reflects "jobs still waiting after me", not
            // "jobs including me".
            int remaining = Interlocked.Decrement(ref _pendingJobs);

            var jobCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            lock (_abortLock)
            {
                _currentAbort = jobCts;
                _currentIsRawKey = job.RawKeyDown;
            }
            try
            {
                if (job.RawKeyDown)
                    await PlayRawKeyAsync(job, remaining, jobCts.Token).ConfigureAwait(false);
                else
                    await PlayJobAsync(job, remaining, jobCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Either operator abort or host shutdown. Either way, unkey
                // and re-emit Idle if MOX is still ours.
                Notify(new CwEngineStatus(
                    CwEngineState.Aborting, job.Text, job.Wpm,
                    Volatile.Read(ref _pendingJobs),
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
                    _currentIsRawKey = false;
                }
                jobCts.Dispose();
            }

            // Emit Idle once the queue actually drains. Mid-queue jobs roll
            // into the next iteration without a flicker.
            if (Volatile.Read(ref _pendingJobs) == 0)
                Notify(new CwEngineStatus(CwEngineState.Idle, string.Empty, 0, 0));
        }
    }

    private async Task PlayJobAsync(CwJob job, int queueDepth, CancellationToken ct)
    {
        // Before keying, force the hardware LO to the canonical CW offset
        // of the dial — eliminates CTUN drift so the carrier lands on the
        // operator's displayed VFO. No-op when CTUN wasn't in play; when it
        // was, the panadapter view recenters, which matches Thetis CW-TX
        // behaviour (the CTUN convenience is RX-only).
        bool loRealigned = _radio.AlignLoForCwTx();

        var snap = _radio.Snapshot();
        // Baseband offset = RadioLo − VFO. After AlignLoForCwTx this is
        // exactly ∓ CwPitchHz (negative for CWU, positive for CWL) and the
        // carrier lands at the dial. The formula is kept rather than
        // hard-coding ±pitch so a future caller that skips AlignLoForCwTx
        // (e.g. an operator who genuinely wants to TX off-centre under
        // CTUN) still gets correct baseband math.
        //
        // Sign note (HL2 IQ convention): the HL2 emits the RF carrier at
        // (LO − baseband_hz), not (LO + baseband_hz) — i.e. the "I − jQ"
        // complex-baseband convention. Verified on a live HL2 2026-05-24
        // (EA5IUE bench test).
        long txHz = RadioService.TxFrequencyHz(snap);
        int basebandHz = (int)(snap.RadioLoHz - txHz);

        if (!_tx.TrySetMox(true, MoxSource.Cwx, out var err))
        {
            _log.LogWarning("cw.mox.refused text={Text} reason={Err}", Truncate(job.Text), err);
            Notify(new CwEngineStatus(
                CwEngineState.Idle, job.Text, job.Wpm, queueDepth,
                Reason: err ?? "MOX refused"));
            return;
        }
        // Host CW now owns the air — disarm the P2 internal keyer so the
        // gateware doesn't self-key against this host-keyed send (#1032).
        _radio.SetHostCwKeying(true);
        Notify(new CwEngineStatus(CwEngineState.Sending, job.Text, job.Wpm, queueDepth));
        _log.LogInformation(
            "cw.send text={Text} wpm={Wpm} mode={Mode} txVfo={TxVfo} txHz={TxHz}Hz lo={Lo}Hz baseband={Bb}Hz loRealigned={LoRealigned}",
            Truncate(job.Text), job.Wpm, snap.Mode, snap.TxVfo, txHz, snap.RadioLoHz, basebandHz, loRealigned);

        // Phase accumulator runs continuously across symbols so the tone
        // stays coherent through inter-element gaps (no phase pop on
        // each dit-onset). Reset per-job.
        double phase = 0.0;
        double phaseStep = 2.0 * Math.PI * basebandHz / SampleRateHz;
        // Scratch buffer for chunked IQ writes. 2 floats per sample.
        var iq = new float[ChunkSamples * 2];

        try
        {
            foreach (var symbol in MorseEncoder.Encode(job.Text, job.Wpm))
            {
                ct.ThrowIfCancellationRequested();
                // Sidetone tracks the symbol's KeyDown — operator hears what's
                // going on the air, with the DSP-thread mixer doing its own
                // 5 ms raised-cosine envelope so the monitor edges don't
                // click even though we toggle this flag instantaneously.
                if (_sidetone is not null)
                {
                    if (symbol.KeyDown) _sidetone.Down();
                    else _sidetone.Up();
                }

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
        }
        finally
        {
            // End-of-message and cancellation share this path so a mid-message
            // abort still releases the sidetone — otherwise the mixer would
            // sit "keyed" until the next CW send arrived.
            _sidetone?.Up();
        }

        // Drop MOX after the final symbol. The ring still has up to ~10 ms
        // of envelope tail queued; wait that long so the radio actually
        // transmits the tail before MOX falls. (Shorter than the natural
        // ring depth — we deliberately keep the ring shallow above.)
        try { await Task.Delay(20, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        TryReleaseMox("done");
    }

    /// <summary>
    /// Steady-carrier playback for the TCI <c>keyer:1</c> path. Same envelope
    /// shaper as <see cref="PlayJobAsync"/> (raised-cosine attack, plateau,
    /// raised-cosine release on cancel or duration-end), but no Morse
    /// encoding — just one long key-down symbol that ends on
    /// <paramref name="ct"/> cancellation or after <c>job.DurationMs</c>
    /// elapses, whichever comes first.
    /// </summary>
    private async Task PlayRawKeyAsync(CwJob job, int queueDepth, CancellationToken ct)
    {
        // Same LO-align and baseband math as PlayJobAsync — keep them in
        // step so the carrier lands at the operator's dial regardless of
        // the keying source (text macro vs. raw key from logger).
        bool loRealigned = _radio.AlignLoForCwTx();
        var snap = _radio.Snapshot();
        long txHz = RadioService.TxFrequencyHz(snap);
        int basebandHz = (int)(snap.RadioLoHz - txHz);

        if (!_tx.TrySetMox(true, MoxSource.Cwx, out var err))
        {
            _log.LogWarning("cw.mox.refused keyer reason={Err}", err);
            Notify(new CwEngineStatus(
                CwEngineState.Idle, string.Empty, 0, queueDepth,
                Reason: err ?? "MOX refused"));
            return;
        }
        // Host CW (raw keyer/logger) owns the air — disarm the P2 internal
        // keyer for the duration so the gateware doesn't self-key too (#1032).
        _radio.SetHostCwKeying(true);
        Notify(new CwEngineStatus(CwEngineState.Sending, "<keyer>", 0, queueDepth));
        _log.LogInformation(
            "cw.keyer.down txVfo={TxVfo} txHz={TxHz}Hz baseband={Bb}Hz durationMs={Dur} loRealigned={LoR}",
            snap.TxVfo, txHz, basebandHz, job.DurationMs?.ToString() ?? "until-release", loRealigned);

        double phase = 0.0;
        double phaseStep = 2.0 * Math.PI * basebandHz / SampleRateHz;
        int rampSamples = RampMs * SampleRateHz / 1000;
        var iq = new float[ChunkSamples * 2];

        // No upper bound when the operator wants to hold the key indefinitely —
        // ct + keyer:0 are the release path. With a duration, we play exactly
        // that many samples then release with the same fade-out shape.
        int? totalSamples = job.DurationMs.HasValue
            ? (int)((long)job.DurationMs.Value * SampleRateHz / 1000)
            : null;

        int written = 0;
        bool releasedByCancel = false;
        // Sidetone follows the raw key for its whole held duration. Down here,
        // Up in the finally so a cancel / duration-expiry / error all release
        // the monitor tone; the DSP-thread mixer runs its own 5 ms fade so the
        // Up lands in lockstep with the carrier release tail below.
        _sidetone?.Down();
        try
        {
            while (!totalSamples.HasValue || written < totalSamples.Value)
            {
                if (ct.IsCancellationRequested) { releasedByCancel = true; break; }
                int n = totalSamples.HasValue
                    ? Math.Min(ChunkSamples, totalSamples.Value - written)
                    : ChunkSamples;
                for (int i = 0; i < n; i++)
                {
                    int pos = written + i;
                    // Attack region only — once past rampSamples we plateau at 1.0
                    // until release. The release tail is rendered after this loop
                    // exits so it always sees the steady-state amplitude.
                    double env = pos < rampSamples
                        ? 0.5 * (1.0 - Math.Cos(Math.PI * pos / rampSamples))
                        : 1.0;
                    iq[2 * i] = (float)(env * Math.Cos(phase));
                    iq[2 * i + 1] = (float)(env * Math.Sin(phase));
                    phase += phaseStep;
                    if (phase > 2.0 * Math.PI) phase -= 2.0 * Math.PI;
                }
                _ring.Write(new ReadOnlySpan<float>(iq, 0, 2 * n));
                written += n;
                int sleepMs = Math.Max(1, (n * 1000) / SampleRateHz - 1);
                try { await Task.Delay(sleepMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { releasedByCancel = true; break; }
            }
        }
        finally
        {
            _sidetone?.Up();
        }

        // Release tail — raised-cosine fall from 1.0 to 0. Use a non-cancellable
        // path so a cancellation that triggered the release doesn't itself
        // truncate the fade and put a click on the air.
        for (int chunkStart = 0; chunkStart < rampSamples; chunkStart += ChunkSamples)
        {
            int n = Math.Min(ChunkSamples, rampSamples - chunkStart);
            for (int i = 0; i < n; i++)
            {
                int into = chunkStart + i;
                double env = 0.5 * (1.0 - Math.Cos(Math.PI * (rampSamples - into) / rampSamples));
                iq[2 * i] = (float)(env * Math.Cos(phase));
                iq[2 * i + 1] = (float)(env * Math.Sin(phase));
                phase += phaseStep;
                if (phase > 2.0 * Math.PI) phase -= 2.0 * Math.PI;
            }
            _ring.Write(new ReadOnlySpan<float>(iq, 0, 2 * n));
            int sleepMs = Math.Max(1, (n * 1000) / SampleRateHz - 1);
            await Task.Delay(sleepMs).ConfigureAwait(false);
        }

        try { await Task.Delay(20, CancellationToken.None).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* shouldn't fire — token is None */ }
        TryReleaseMox(releasedByCancel ? "keyer.up" : "keyer.duration");
    }

    private void TryReleaseMox(string reason)
    {
        // Host CW is done keying — re-arm the P2 internal keyer (cleared
        // unconditionally, even if UI/trip took MOX from us, so a paddle works
        // again the moment this host send ends). No-op on P1. (#1032)
        _radio.SetHostCwKeying(false);
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

    /// <summary>Test seam: precompute the IQ stream a raw-key job
    /// (<c>keyer:0,true,durationMs</c>) would emit, including the attack
    /// and release ramps. Same envelope shape as <see cref="PlayRawKeyAsync"/>
    /// — the divergence is only in how the engine drives MOX and the
    /// ring, not in the carrier shape.</summary>
    internal static float[] RenderRawKeyForTest(int durationMs, int basebandHz)
    {
        int totalSamples = durationMs * SampleRateHz / 1000;
        int rampSamples = RampMs * SampleRateHz / 1000;
        // Plateau cannot be negative — if duration < 2× ramp the attack and
        // release overlap. Match PlayRawKeyAsync: attack fills the front,
        // release the tail, plateau (if any) in between.
        int plateauSamples = Math.Max(0, totalSamples - rampSamples);

        double phase = 0.0;
        double phaseStep = 2.0 * Math.PI * basebandHz / SampleRateHz;
        var buf = new float[2 * (totalSamples + rampSamples)];
        int idx = 0;

        for (int pos = 0; pos < plateauSamples; pos++)
        {
            double env = pos < rampSamples
                ? 0.5 * (1.0 - Math.Cos(Math.PI * pos / rampSamples))
                : 1.0;
            buf[idx++] = (float)(env * Math.Cos(phase));
            buf[idx++] = (float)(env * Math.Sin(phase));
            phase += phaseStep;
            if (phase > 2.0 * Math.PI) phase -= 2.0 * Math.PI;
        }
        for (int into = 0; into < rampSamples; into++)
        {
            double env = 0.5 * (1.0 - Math.Cos(Math.PI * (rampSamples - into) / rampSamples));
            buf[idx++] = (float)(env * Math.Cos(phase));
            buf[idx++] = (float)(env * Math.Sin(phase));
            phase += phaseStep;
            if (phase > 2.0 * Math.PI) phase -= 2.0 * Math.PI;
        }
        return buf;
    }

    /// <summary>Test seam: precompute the IQ stream for <paramref name="text"/>
    /// at <paramref name="wpm"/>. <paramref name="basebandHz"/> is the live
    /// engine's <c>(VfoHz − RadioLoHz)</c> — pass +600 for CWU without CTUN,
    /// -600 for CWL without CTUN, or any signed offset to exercise CTUN.</summary>
    internal static float[] RenderForTest(string text, int wpm, int basebandHz)
    {
        double phase = 0.0;
        double phaseStep = 2.0 * Math.PI * basebandHz / SampleRateHz;
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

    private readonly record struct CwJob(string Text, int Wpm, bool RawKeyDown, int? DurationMs);
}
