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

using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
    private readonly DspPipelineService _pipeline;
    private readonly ILogger<WsprService> _log;

    private readonly object _gate = new();
    private RxSession? _session;
    private Channel<PendingSlot>? _decodeQueue;
    private Task? _decodeWorker;
    private CancellationTokenSource? _workerCts;

    /// <summary>Raised on the worker thread when a slot has been decoded.</summary>
    public event Action<WsprSpotBatch>? SpotsReady;

    public WsprService(DspPipelineService pipeline, ILoggerFactory loggerFactory)
    {
        _pipeline = pipeline;
        _log = loggerFactory.CreateLogger<WsprService>();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _pipeline.RxAudioAvailable += OnRxAudio;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _pipeline.RxAudioAvailable -= OnRxAudio;
        Disable();
        return Task.CompletedTask;
    }

    public bool NativeAvailable => WsprDecoder.IsAvailable;
    public bool IsEnabled { get { lock (_gate) return _session is not null; } }
    public int ActiveReceiver { get { lock (_gate) return _session?.Receiver ?? -1; } }
    public double DialFreqMhz { get { lock (_gate) return _session?.DialMhz ?? 0; } }

    /// <summary>
    /// Enter WSPR decode on a receiver at the given dial frequency (MHz, e.g.
    /// 14.0956 for 20 m). Idempotent. Returns false if the native decoder is
    /// unavailable on this platform (Windows ships encode-only today).
    /// </summary>
    public bool Enable(int receiver, double dialFreqMhz)
    {
        if (!WsprDecoder.IsAvailable)
        {
            _log.LogWarning("WSPR decode requested but zeus_wspr decode is unavailable on this platform.");
            return false;
        }

        lock (_gate)
        {
            _session = new RxSession(receiver, dialFreqMhz);
            if (_decodeWorker is null)
            {
                _workerCts = new CancellationTokenSource();
                _decodeQueue = Channel.CreateBounded<PendingSlot>(
                    new BoundedChannelOptions(2) { FullMode = BoundedChannelFullMode.DropOldest });
                _decodeWorker = Task.Run(() => DecodeLoopAsync(_workerCts.Token));
            }
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
        }
        cts?.Cancel();
        try { worker?.Wait(TimeSpan.FromSeconds(2)); } catch { /* shutting down */ }
        toDispose?.Dispose();
        cts?.Dispose();
    }

    private void OnRxAudio(int receiver, int sampleRate, ReadOnlyMemory<float> block)
    {
        RxSession? s;
        Channel<PendingSlot>? queue;
        lock (_gate) { s = _session; queue = _decodeQueue; }
        if (s is null || queue is null || receiver != s.Receiver) return;
        if (sampleRate != Ft8Resampler.InputRate) return; // fixed 48k->12k decimator

        float[] decimated = s.Resampler.Process(block.Span);
        if (decimated.Length == 0) return;

        Ft8Slot? completed = s.Accumulator.Add(decimated, DateTime.UtcNow);
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
                try
                {
                    var spots = WsprDecoder.Decode(pending.Slot.Samples, pending.DialMhz);
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
        catch (OperationCanceledException) { /* normal shutdown */ }
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
            Accumulator = new Ft8SlotAccumulator(slotSeconds: 120.0);
        }

        public void Dispose() { }
    }

    private readonly record struct PendingSlot(int Receiver, double DialMhz, Ft8Slot Slot);
}
