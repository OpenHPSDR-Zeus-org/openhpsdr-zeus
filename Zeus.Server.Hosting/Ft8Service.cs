// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Ft8Service — the live RX pipeline for the built-in FT8/FT4 client. It taps the
// existing post-demod RX audio stream (DspPipelineService.RxAudioAvailable, the
// same seam AudioTapBridge uses — no hot-path DSP changes), decimates 48 kHz ->
// 12 kHz, buffers UTC-aligned slots, and decodes each completed slot on a
// background worker so the audio thread is never blocked by the ~hundreds-of-ms
// LDPC decode.
//
// Per-RX by construction: each enabled receiver gets its own resampler +
// accumulator + native decoder, so simultaneous multi-band decode is a matter
// of enabling more than one RX (gated today on the pipeline only tapping RX0).
//
// SELF-HEALING (regression #1080 / pop-out era): the RxAudioAvailable tap is a
// process-lifetime subscription that no reconfigure (SetVfo/SetMode/SetFilter/
// band-change) ever drops — but the single decode worker + native decoder behind
// it CAN wedge or die (a hung native decode, or the increased re-enable/
// reconfigure churn the pop-out introduced). When that happened decode silently
// stopped while IsEnabled stayed true and only a manual disable->enable revived
// it. A lightweight watchdog now does that revive automatically: if audio is
// still arriving but the worker has made no progress for ~2.5 slot periods, the
// session + worker are rebuilt in place. No operator action, no TX, no DSP
// changes — exactly what disable->enable did, on a timer.
//
// This is the RX half. TX (encode + scheduling + keying) and the SignalR/UI
// surface come later; for now decodes are logged and raised via DecodesReady.

using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Dsp.Ft8;

namespace Zeus.Server;

/// <summary>A decoded slot's worth of FT8/FT4 messages, with context.</summary>
public sealed record Ft8DecodeBatch(
    int Receiver,
    DateTime SlotStartUtc,
    Ft8Protocol Protocol,
    IReadOnlyList<Ft8DecodeResult> Decodes);

public sealed class Ft8Service : IHostedService, IDisposable
{
    private readonly DspPipelineService? _pipeline;
    private readonly ILogger<Ft8Service> _log;
    private readonly Func<IFt8Decoder> _decoderFactory;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<bool> _nativeAvailable;

    private readonly object _gate = new();
    private RxSession? _session;                 // single active session (RX0) for now
    private Channel<PendingSlot>? _decodeQueue;
    private Task? _decodeWorker;
    private CancellationTokenSource? _workerCts;
    private double _slotSeconds = 15.0;

    // Watchdog progress tracking (UTC ticks, 0 = none yet). The audio thread and
    // the decode worker write these; the watchdog timer reads them.
    private long _lastAudioTicks;
    private long _lastWorkerProgressTicks;
    private Timer? _watchdog;

    // Field-observability + leak-bounding for the watchdog. The watchdog RECOVERS
    // a stall; it does not yet PREVENT one (the originating mechanism — a hung
    // native decode vs a faulted worker — is still unproven on the bench). These
    // make every recovery visible in the log so the true cause can be captured on
    // the wire, and cap the damage if rebuilds stop holding.
    private int _rebuildCount;          // total watchdog rebuilds this session (diagnostic)
    private int _leakedWorkers;         // detached old workers that didn't exit in 2 s
    private readonly Queue<DateTime> _recentRebuilds = new(); // rebuild times in RebuildWindow
    private bool _degraded;             // gave up rebuilding to stop leaking; needs re-enable

    /// <summary>Audio is "actively arriving" if a block fed within this window.</summary>
    private static readonly TimeSpan AudioFreshWindow = TimeSpan.FromSeconds(5);
    /// <summary>How often the watchdog checks for a wedged worker.</summary>
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(5);
    /// <summary>Sliding window over which repeated rebuilds are counted.</summary>
    private static readonly TimeSpan RebuildWindow = TimeSpan.FromMinutes(10);
    /// <summary>
    /// Max rebuilds in <see cref="RebuildWindow"/> before the watchdog gives up.
    /// A truly-hung native decode leaks one worker thread + native context per
    /// rebuild (the old worker can't be joined), so we must NOT rebuild forever.
    /// </summary>
    private const int MaxRebuildsPerWindow = 5;

    /// <summary>How many decode passes to run (1 = NORMAL, &gt;1 = DEEP/MULTI).</summary>
    public int DecodePasses { get; set; } = 3;

    /// <summary>Raised on the worker thread when a slot has been decoded.</summary>
    public event Action<Ft8DecodeBatch>? DecodesReady;

    public Ft8Service(DspPipelineService pipeline, ILoggerFactory loggerFactory)
    {
        _pipeline = pipeline;
        _log = loggerFactory.CreateLogger<Ft8Service>();
        _decoderFactory = static () => new Ft8Decoder();
        _utcNow = static () => DateTime.UtcNow;
        _nativeAvailable = static () => Ft8Decoder.IsAvailable;
    }

    /// <summary>Test seam: inject a fake decoder + clock; no live audio tap.</summary>
    internal Ft8Service(
        Func<IFt8Decoder> decoderFactory,
        Func<DateTime> utcNow,
        ILogger<Ft8Service>? log = null)
    {
        _pipeline = null;
        _decoderFactory = decoderFactory;
        _utcNow = utcNow;
        _nativeAvailable = static () => true;
        _log = log ?? NullLogger<Ft8Service>.Instance;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_pipeline is not null) _pipeline.RxAudioAvailable += OnRxAudio;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_pipeline is not null) _pipeline.RxAudioAvailable -= OnRxAudio;
        Disable();
        return Task.CompletedTask;
    }

    /// <summary>True if the native decoder is present on this platform.</summary>
    public bool NativeAvailable => _nativeAvailable();

    /// <summary>True while a receiver is actively decoding.</summary>
    public bool IsEnabled { get { lock (_gate) return _session is not null; } }

    /// <summary>The receiver currently decoding, or -1 when disabled.</summary>
    public int ActiveReceiver { get { lock (_gate) return _session?.Receiver ?? -1; } }

    /// <summary>The protocol currently decoding (FT8 when disabled).</summary>
    public Ft8Protocol ActiveProtocol { get { lock (_gate) return _session?.Protocol ?? Ft8Protocol.Ft8; } }

    /// <summary>
    /// True if the watchdog rebuilt repeatedly without the stall clearing and has
    /// stopped rebuilding to avoid leaking worker threads / native contexts. Decode
    /// stays dead until the operator disables and re-enables. Surfaced so a
    /// persistent native fault is visible rather than silently leaking forever.
    /// </summary>
    public bool IsDegraded { get { lock (_gate) return _degraded; } }

    /// <summary>Total watchdog rebuilds this session (diagnostic / test).</summary>
    internal int RebuildCount => Volatile.Read(ref _rebuildCount);

    /// <summary>
    /// Enter FT8/FT4 decode on a receiver. Idempotent: re-enabling on the same
    /// receiver+protocol only updates the pass count (it does NOT tear down the
    /// in-flight session/decoder — that churn is what the pop-out era exposed).
    /// Returns false if the native decoder is unavailable.
    /// </summary>
    public bool Enable(int receiver = 0, Ft8Protocol protocol = Ft8Protocol.Ft8)
    {
        if (!_nativeAvailable())
        {
            _log.LogWarning("FT8 decode requested but zeus_ft8 native library is unavailable.");
            return false;
        }

        lock (_gate)
        {
            // Same receiver+protocol already running: a no-op session-wise. The
            // caller (setPasses / settings hydration) just wants the new pass
            // count, which OnRxAudio reads live for the next slot.
            if (_session is { } cur && cur.Receiver == receiver && cur.Protocol == protocol)
                return true;

            _session?.Dispose();
            _slotSeconds = protocol == Ft8Protocol.Ft4 ? 7.5 : 15.0;
            _session = new RxSession(receiver, protocol, _slotSeconds, _decoderFactory());

            // Explicit operator (re-)enable clears any degraded state and the
            // rebuild history — this IS the manual recovery the watchdog mimics.
            _degraded = false;
            _recentRebuilds.Clear();

            EnsureWorkerStarted();
            StartWatchdog();
        }
        _log.LogInformation("FT8 decode enabled on RX{Rx} ({Proto}).", receiver, protocol);
        return true;
    }

    /// <summary>Leave FT8/FT4 decode and tear down the worker.</summary>
    public void Disable()
    {
        RxSession? toDispose;
        Task? worker;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            toDispose = _session; _session = null;
            worker = _decodeWorker; _decodeWorker = null;
            cts = _workerCts; _workerCts = null;
            _decodeQueue?.Writer.TryComplete();
            _decodeQueue = null;
            _watchdog?.Dispose();
            _watchdog = null;
        }
        cts?.Cancel();
        try { worker?.Wait(TimeSpan.FromSeconds(2)); } catch { /* shutting down */ }
        toDispose?.Dispose();
        cts?.Dispose();
    }

    // --- worker / watchdog plumbing (callers hold _gate) ---

    private void EnsureWorkerStarted()
    {
        if (_decodeWorker is not null) return;
        _workerCts = new CancellationTokenSource();
        _decodeQueue = Channel.CreateBounded<PendingSlot>(
            new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.DropOldest });
        long now = _utcNow().Ticks;
        Interlocked.Exchange(ref _lastWorkerProgressTicks, now);
        Interlocked.Exchange(ref _lastAudioTicks, 0);
        var ct = _workerCts.Token;
        _decodeWorker = Task.Run(() => DecodeLoopAsync(ct));
    }

    private void StartWatchdog()
    {
        if (_pipeline is null || _watchdog is not null) return; // tests drive RunWatchdogOnce
        _watchdog = new Timer(_ => { try { RunWatchdogOnce(_utcNow()); } catch { } },
            null, WatchdogInterval, WatchdogInterval);
    }

    // Audio thread — must stay cheap: resample + append, hand decode to the worker.
    private void OnRxAudio(int receiver, int sampleRate, ReadOnlyMemory<float> block)
        => FeedAudio(receiver, sampleRate, block.Span);

    internal void FeedAudio(int receiver, int sampleRate, ReadOnlySpan<float> block)
    {
        RxSession? s;
        Channel<PendingSlot>? queue;
        int passes;
        lock (_gate) { s = _session; queue = _decodeQueue; passes = DecodePasses; }
        if (s is null || queue is null || receiver != s.Receiver) return;

        if (sampleRate != Ft8Resampler.InputRate)
        {
            // The decimator is fixed 48k->12k; an unexpected rate would alias.
            // Skip rather than feed the decoder wrong-rate audio.
            // NOTE: this is a watchdog blind spot — _lastAudioTicks is only
            // stamped post-gate, so if a reconfigure ever changed the RX audio
            // rate the watchdog would see "audio not fresh" and NOT rebuild. That
            // is intentional: rebuilding a session cannot heal a rate mismatch.
            // AudioOutputRateHz is fixed at 48 kHz today, so this is latent; if it
            // ever becomes configurable, surface the rate mismatch here instead.
            return;
        }

        float[] decimated = s.Resampler.Process(block);
        if (decimated.Length == 0) return;

        // Audio is reaching the decode pipeline — note it for the watchdog. The
        // watchdog only recovers session-internal stalls (worker wedged while
        // audio still flows); a true tap-loss (OnRxAudio stops firing) leaves
        // audio stale and is deliberately out of its scope.
        Interlocked.Exchange(ref _lastAudioTicks, _utcNow().Ticks);

        Ft8Slot? completed = s.Accumulator.Add(decimated, _utcNow());
        if (completed is { } slot)
            queue.Writer.TryWrite(new PendingSlot(s.Receiver, s.Protocol, s.Decoder, slot, passes));
    }

    private async Task DecodeLoopAsync(CancellationToken ct)
    {
        Channel<PendingSlot>? queue;
        lock (_gate) { queue = _decodeQueue; }
        if (queue is null) return;

        try
        {
            await foreach (var pending in queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                // Worker dequeued a slot — proof of life (entering the decode).
                Interlocked.Exchange(ref _lastWorkerProgressTicks, _utcNow().Ticks);
                try
                {
                    // Time the native decode. If it ever HANGS this line never
                    // returns (the watchdog then rebuilds); if it merely runs slow
                    // — the cross-platform risk on a Pi at 3 passes — the warning
                    // below makes "approaching the stall threshold" visible BEFORE
                    // the watchdog mistakes a slow decode for a hang.
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var decodes = pending.Decoder.Decode(pending.Slot.Samples, pending.Protocol, pending.Passes);
                    sw.Stop();
                    // Decode returned — refresh progress so a slow-but-completing
                    // worker is never mistaken for a wedged one on the next tick.
                    Interlocked.Exchange(ref _lastWorkerProgressTicks, _utcNow().Ticks);
                    if (sw.Elapsed.TotalSeconds > _slotSeconds * 2.0)
                        _log.LogWarning(
                            "FT8 RX{Rx} decode took {Ms} ms ({Passes} passes) — abnormally slow; " +
                            "watchdog stall threshold is {Stall:0}s. If this approaches the threshold the " +
                            "decode-pass count is too high for this platform.",
                            pending.Receiver, sw.ElapsedMilliseconds, pending.Passes, _slotSeconds * 2.5);

                    if (decodes.Count > 0)
                    {
                        foreach (var d in decodes)
                            _log.LogInformation(
                                "FT8 RX{Rx} {Time:HH:mm:ss} {Snr,3:+0;-0} dB {Dt,4:0.0} {Freq,4:0} Hz  {Msg}",
                                pending.Receiver, pending.Slot.SlotStartUtc, d.SnrDb, d.DtSec, d.FreqHz, d.Text);

                        DecodesReady?.Invoke(new Ft8DecodeBatch(
                            pending.Receiver, pending.Slot.SlotStartUtc, pending.Protocol, decodes));
                    }
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "FT8 slot decode failed (RX{Rx}).", pending.Receiver);
                }
            }
        }
        catch (OperationCanceledException) { /* normal shutdown / rebuild */ }
        catch (Exception ex) { _log.LogError(ex, "FT8 decode worker exited unexpectedly."); }
    }

    /// <summary>
    /// Watchdog tick: if audio is still arriving but the worker has made no
    /// progress for ~2.5 slot periods, the worker/decoder has wedged — rebuild
    /// the session in place (the automatic equivalent of disable-&gt;enable).
    /// Returns true if it rebuilt. Internal so tests can drive it deterministically.
    /// </summary>
    internal bool RunWatchdogOnce(DateTime now)
    {
        lock (_gate)
        {
            if (_session is null) return false; // disabled
            if (_degraded) return false;        // gave up — not leaking further

            long audio = Interlocked.Read(ref _lastAudioTicks);
            long prog = Interlocked.Read(ref _lastWorkerProgressTicks);

            // Nothing to recover if audio isn't actively arriving.
            if (audio == 0 || now - new DateTime(audio, DateTimeKind.Utc) > AudioFreshWindow)
                return false;

            var stall = TimeSpan.FromSeconds(_slotSeconds * 2.5);
            if (prog != 0 && now - new DateTime(prog, DateTimeKind.Utc) <= stall)
                return false;

            // Bound the leak. A genuinely-hung native decode cannot be joined, so
            // each rebuild abandons one worker thread + native context. Periodic
            // single stalls (the field cadence) prune out of the window and never
            // trip this; only a persistent hang rebuilds fast enough to hit the
            // cap, at which point we stop and surface a degraded state.
            while (_recentRebuilds.Count > 0 && now - _recentRebuilds.Peek() > RebuildWindow)
                _recentRebuilds.Dequeue();
            if (_recentRebuilds.Count >= MaxRebuildsPerWindow)
            {
                _degraded = true;
                _log.LogError(
                    "FT8 decode RX{Rx} stalled and rebuilds are not holding ({N} in {Win} min, {Leaked} worker(s) " +
                    "failed to exit) — giving up to avoid leaking threads/native contexts. Decode is DEGRADED until " +
                    "the operator disables and re-enables. Capture worker dequeue/decode timing to root-cause the hang.",
                    _session.Receiver, _recentRebuilds.Count, RebuildWindow.TotalMinutes, Volatile.Read(ref _leakedWorkers));
                return false;
            }

            _recentRebuilds.Enqueue(now);
            int n = Interlocked.Increment(ref _rebuildCount);
            _log.LogWarning(
                "FT8 decode worker stalled (audio flowing, no progress for >{Stall:0}s) — rebuilding RX{Rx} (rebuild #{N}).",
                stall.TotalSeconds, _session.Receiver, n);
            RebuildSessionLocked(now);
            return true;
        }
    }

    // Replace the wedged session + worker with a fresh one. Caller holds _gate.
    // The old worker may be stuck inside a hung native decode, so it is cancelled
    // and torn down on a detached task — never waited on inline.
    private void RebuildSessionLocked(DateTime now)
    {
        var s = _session!;
        RxSession? old = s;
        Task? oldWorker = _decodeWorker;
        CancellationTokenSource? oldCts = _workerCts;

        _session = new RxSession(s.Receiver, s.Protocol, _slotSeconds, _decoderFactory());
        _decodeWorker = null;
        _workerCts = null;
        _decodeQueue?.Writer.TryComplete();
        _decodeQueue = null;
        EnsureWorkerStarted();
        Interlocked.Exchange(ref _lastWorkerProgressTicks, now.Ticks);

        oldCts?.Cancel();
        int rx = s.Receiver;
        _ = Task.Run(() =>
        {
            bool exited = true;
            try { exited = oldWorker is null || oldWorker.Wait(TimeSpan.FromSeconds(2)); } catch { }
            if (!exited)
                _log.LogWarning(
                    "FT8 old decode worker (RX{Rx}) did not exit within 2s after rebuild — native decode is " +
                    "hung; leaking 1 worker thread + native context (total leaked this session: {N}).",
                    rx, Interlocked.Increment(ref _leakedWorkers));
            try { old?.Dispose(); } catch { } // Decoder.Dispose blocks if native is hung — fine, detached
            oldCts?.Dispose();
        });
    }

    public void Dispose() => Disable();

    // Per-RX decode state.
    private sealed class RxSession : IDisposable
    {
        public int Receiver { get; }
        public Ft8Protocol Protocol { get; }
        public Ft8Resampler Resampler { get; }
        public Ft8SlotAccumulator Accumulator { get; }
        public IFt8Decoder Decoder { get; }

        public RxSession(int receiver, Ft8Protocol protocol, double slotSeconds, IFt8Decoder decoder)
        {
            Receiver = receiver;
            Protocol = protocol;
            Resampler = new Ft8Resampler();
            Accumulator = new Ft8SlotAccumulator(slotSeconds);
            Decoder = decoder;
        }

        public void Dispose() => Decoder.Dispose();
    }

    private readonly record struct PendingSlot(
        int Receiver, Ft8Protocol Protocol, IFt8Decoder Decoder, Ft8Slot Slot, int Passes);
}
