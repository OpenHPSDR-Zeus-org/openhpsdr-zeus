// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// WsprService — the live RX pipeline for native WSPR spotting. Mirrors
// Ft8Service: taps the post-demod RX audio (DspPipelineService.RxAudioAvailable),
// decimates 48k->12k, buffers UTC-aligned 120 s slots (naturally even-minute
// aligned), and decodes each completed slot on a background worker via the
// vendored K1JT/K9AN decoder (WsprDecoder). Decoded spots are logged and raised
// via SpotsReady (a WSPRnet reporter / UI consume them next).
//
// WSPR decode is global/serialised in the native layer and runs once per 120 s,
// so this is single-session (RX0) for now; the dial frequency is supplied on
// Enable since the decoded spot frequency = dial + audio offset.
//
// SELF-HEALING: like Ft8Service, the RxAudioAvailable tap is process-lifetime and
// survives every reconfigure, but the single decode worker can wedge (a hung
// native decode) and leave IsEnabled true with no spots. The watchdog rebuilds
// the session + worker automatically when audio is flowing but the worker has
// made no progress for ~2.5 slot periods. See Ft8Service for the full rationale.

using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Dsp.Ft8;

namespace Zeus.Server;

/// <summary>A completed WSPR slot's spots, with context.</summary>
public sealed record WsprSpotBatch(
    int Receiver,
    DateTime SlotStartUtc,
    double DialFreqMhz,
    IReadOnlyList<WsprSpot> Spots);

public sealed class WsprService : IHostedService, IDisposable
{
    private readonly DspPipelineService? _pipeline;
    private readonly ILogger<WsprService> _log;
    private readonly Func<float[], double, IReadOnlyList<WsprSpot>> _decode;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<bool> _nativeAvailable;

    private const double SlotSeconds = 120.0;

    private readonly object _gate = new();
    private RxSession? _session;
    private Channel<PendingSlot>? _decodeQueue;
    private Task? _decodeWorker;
    private CancellationTokenSource? _workerCts;

    private long _lastAudioTicks;
    private long _lastWorkerProgressTicks;
    private Timer? _watchdog;

    // Field-observability + leak-bounding for the watchdog (see Ft8Service for the
    // full rationale — the watchdog recovers a stall but the originating cause is
    // still unproven on the bench, so make recoveries visible and cap the leak).
    private int _rebuildCount;
    private int _leakedWorkers;
    private readonly Queue<DateTime> _recentRebuilds = new();
    private bool _degraded;

    private static readonly TimeSpan AudioFreshWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RebuildWindow = TimeSpan.FromMinutes(30);
    private const int MaxRebuildsPerWindow = 5;

    /// <summary>Raised on the worker thread when a slot has been decoded.</summary>
    public event Action<WsprSpotBatch>? SpotsReady;

    public WsprService(DspPipelineService pipeline, ILoggerFactory loggerFactory)
    {
        _pipeline = pipeline;
        _log = loggerFactory.CreateLogger<WsprService>();
        _decode = static (samples, dial) => WsprDecoder.Decode(samples, dial);
        _utcNow = static () => DateTime.UtcNow;
        _nativeAvailable = static () => WsprDecoder.IsAvailable;
    }

    /// <summary>Test seam: inject a fake decode + clock; no live audio tap.</summary>
    internal WsprService(
        Func<float[], double, IReadOnlyList<WsprSpot>> decode,
        Func<DateTime> utcNow,
        ILogger<WsprService>? log = null)
    {
        _pipeline = null;
        _decode = decode;
        _utcNow = utcNow;
        _nativeAvailable = static () => true;
        _log = log ?? NullLogger<WsprService>.Instance;
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

    public bool NativeAvailable => _nativeAvailable();
    public bool IsEnabled { get { lock (_gate) return _session is not null; } }
    public int ActiveReceiver { get { lock (_gate) return _session?.Receiver ?? -1; } }
    public double DialFreqMhz { get { lock (_gate) return _session?.DialMhz ?? 0; } }

    /// <summary>
    /// True if the watchdog stopped rebuilding (repeated stalls not clearing) to
    /// avoid leaking worker threads / native contexts. Decode stays dead until the
    /// operator disables and re-enables. See <see cref="Ft8Service.IsDegraded"/>.
    /// </summary>
    public bool IsDegraded { get { lock (_gate) return _degraded; } }

    /// <summary>Total watchdog rebuilds this session (diagnostic / test).</summary>
    internal int RebuildCount => Volatile.Read(ref _rebuildCount);

    /// <summary>
    /// Enter WSPR decode on a receiver at the given dial frequency (MHz, e.g.
    /// 14.0956 for 20 m). Idempotent: re-enabling on the same receiver+dial is a
    /// no-op; a new dial rebuilds the session (without disturbing the worker).
    /// Returns false if the native decoder is unavailable on this platform.
    /// </summary>
    public bool Enable(int receiver, double dialFreqMhz)
    {
        if (!_nativeAvailable())
        {
            _log.LogWarning("WSPR decode requested but zeus_wspr decode is unavailable on this platform.");
            return false;
        }

        lock (_gate)
        {
            // Tolerance, not exact equality: an arithmetic-derived dial that
            // differs by an ULP must still be treated as the same tuning (matches
            // the frontend's <=1 Hz guard) so it doesn't force a needless rebuild.
            if (_session is { } cur && cur.Receiver == receiver
                && Math.Abs(cur.DialMhz - dialFreqMhz) < 1e-9)
                return true;

            _session = new RxSession(receiver, dialFreqMhz);
            // Explicit operator (re-)enable clears degraded state + rebuild history.
            _degraded = false;
            _recentRebuilds.Clear();
            EnsureWorkerStarted();
            StartWatchdog();
        }
        _log.LogInformation("WSPR decode enabled on RX{Rx} at {Dial:F4} MHz.", receiver, dialFreqMhz);
        return true;
    }

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

    private void EnsureWorkerStarted()
    {
        if (_decodeWorker is not null) return;
        _workerCts = new CancellationTokenSource();
        _decodeQueue = Channel.CreateBounded<PendingSlot>(
            new BoundedChannelOptions(2) { FullMode = BoundedChannelFullMode.DropOldest });
        Interlocked.Exchange(ref _lastWorkerProgressTicks, _utcNow().Ticks);
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

    private void OnRxAudio(int receiver, int sampleRate, ReadOnlyMemory<float> block)
        => FeedAudio(receiver, sampleRate, block.Span);

    internal void FeedAudio(int receiver, int sampleRate, ReadOnlySpan<float> block)
    {
        RxSession? s;
        Channel<PendingSlot>? queue;
        lock (_gate) { s = _session; queue = _decodeQueue; }
        if (s is null || queue is null || receiver != s.Receiver) return;
        if (sampleRate != Ft8Resampler.InputRate) return; // fixed 48k->12k decimator

        float[] decimated = s.Resampler.Process(block);
        if (decimated.Length == 0) return;

        Interlocked.Exchange(ref _lastAudioTicks, _utcNow().Ticks);

        Ft8Slot? completed = s.Accumulator.Add(decimated, _utcNow());
        if (completed is { } slot)
            queue.Writer.TryWrite(new PendingSlot(s.Receiver, s.DialMhz, slot));
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
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var spots = _decode(pending.Slot.Samples, pending.DialMhz);
                    sw.Stop();
                    // Decode returned — refresh progress so a slow-but-completing
                    // worker is never mistaken for a wedged one on the next tick.
                    Interlocked.Exchange(ref _lastWorkerProgressTicks, _utcNow().Ticks);
                    if (sw.Elapsed.TotalSeconds > SlotSeconds)
                        _log.LogWarning(
                            "WSPR RX{Rx} decode took {Ms} ms — abnormally slow; watchdog stall threshold is {Stall:0}s.",
                            pending.Receiver, sw.ElapsedMilliseconds, SlotSeconds * 2.5);

                    if (spots.Count > 0)
                    {
                        foreach (var sp in spots)
                            _log.LogInformation(
                                "WSPR RX{Rx} {Time:HH:mm} {Snr,3:0} dB {Dt,4:0.0} {Freq:F6} MHz dr{Drift,2}  {Msg}",
                                pending.Receiver, pending.Slot.SlotStartUtc, sp.SnrDb, sp.DtSec,
                                sp.FreqMhz, sp.DriftHz, sp.Message);

                        SpotsReady?.Invoke(new WsprSpotBatch(
                            pending.Receiver, pending.Slot.SlotStartUtc, pending.DialMhz, spots));
                    }
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "WSPR slot decode failed (RX{Rx}).", pending.Receiver);
                }
            }
        }
        catch (OperationCanceledException) { /* normal shutdown / rebuild */ }
        catch (Exception ex) { _log.LogError(ex, "WSPR decode worker exited unexpectedly."); }
    }

    /// <summary>
    /// Watchdog tick: rebuild the session + worker if audio is still arriving but
    /// the worker has made no progress for ~2.5 slot periods (300 s). Internal so
    /// tests can drive it deterministically. Returns true if it rebuilt.
    /// </summary>
    internal bool RunWatchdogOnce(DateTime now)
    {
        lock (_gate)
        {
            if (_session is null) return false;
            if (_degraded) return false; // gave up — not leaking further

            long audio = Interlocked.Read(ref _lastAudioTicks);
            long prog = Interlocked.Read(ref _lastWorkerProgressTicks);

            if (audio == 0 || now - new DateTime(audio, DateTimeKind.Utc) > AudioFreshWindow)
                return false;

            var stall = TimeSpan.FromSeconds(SlotSeconds * 2.5);
            if (prog != 0 && now - new DateTime(prog, DateTimeKind.Utc) <= stall)
                return false;

            // Bound the leak (see Ft8Service) — a hung native decode can't be
            // joined, so cap rebuilds and surface a degraded state instead.
            while (_recentRebuilds.Count > 0 && now - _recentRebuilds.Peek() > RebuildWindow)
                _recentRebuilds.Dequeue();
            if (_recentRebuilds.Count >= MaxRebuildsPerWindow)
            {
                _degraded = true;
                _log.LogError(
                    "WSPR decode RX{Rx} stalled and rebuilds are not holding ({N} in {Win} min, {Leaked} worker(s) " +
                    "failed to exit) — giving up to avoid leaking threads/native contexts. Decode is DEGRADED until " +
                    "the operator disables and re-enables.",
                    _session.Receiver, _recentRebuilds.Count, RebuildWindow.TotalMinutes, Volatile.Read(ref _leakedWorkers));
                return false;
            }

            _recentRebuilds.Enqueue(now);
            int n = Interlocked.Increment(ref _rebuildCount);
            _log.LogWarning(
                "WSPR decode worker stalled (audio flowing, no progress for >{Stall:0}s) — rebuilding RX{Rx} (rebuild #{N}).",
                stall.TotalSeconds, _session.Receiver, n);
            RebuildSessionLocked(now);
            return true;
        }
    }

    private void RebuildSessionLocked(DateTime now)
    {
        var s = _session!;
        RxSession? old = s;
        Task? oldWorker = _decodeWorker;
        CancellationTokenSource? oldCts = _workerCts;

        _session = new RxSession(s.Receiver, s.DialMhz);
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
                    "WSPR old decode worker (RX{Rx}) did not exit within 2s after rebuild — native decode is " +
                    "hung; leaking 1 worker thread (total leaked this session: {N}).",
                    rx, Interlocked.Increment(ref _leakedWorkers));
            try { old?.Dispose(); } catch { }
            oldCts?.Dispose();
        });
    }

    public void Dispose() => Disable();

    private sealed class RxSession : IDisposable
    {
        public int Receiver { get; }
        public double DialMhz { get; }
        public Ft8Resampler Resampler { get; }
        public Ft8SlotAccumulator Accumulator { get; }

        public RxSession(int receiver, double dialMhz)
        {
            Receiver = receiver;
            DialMhz = dialMhz;
            Resampler = new Ft8Resampler();
            Accumulator = new Ft8SlotAccumulator(slotSeconds: SlotSeconds);
        }

        public void Dispose() { }
    }

    private readonly record struct PendingSlot(int Receiver, double DialMhz, Ft8Slot Slot);
}
