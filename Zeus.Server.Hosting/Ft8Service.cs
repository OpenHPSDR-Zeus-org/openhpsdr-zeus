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
// This is the RX half. TX (encode + scheduling + keying) and the SignalR/UI
// surface come later; for now decodes are logged and raised via DecodesReady.

using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
    private readonly DspPipelineService _pipeline;
    private readonly ILogger<Ft8Service> _log;

    private readonly object _gate = new();
    private RxSession? _session;                 // single active session (RX0) for now
    private Channel<PendingSlot>? _decodeQueue;
    private Task? _decodeWorker;
    private CancellationTokenSource? _workerCts;

    /// <summary>How many decode passes to run (1 = NORMAL, &gt;1 = DEEP/MULTI).</summary>
    public int DecodePasses { get; set; } = 3;

    /// <summary>Raised on the worker thread when a slot has been decoded.</summary>
    public event Action<Ft8DecodeBatch>? DecodesReady;

    public Ft8Service(DspPipelineService pipeline, ILoggerFactory loggerFactory)
    {
        _pipeline = pipeline;
        _log = loggerFactory.CreateLogger<Ft8Service>();
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

    /// <summary>True if the native decoder is present on this platform.</summary>
    public bool NativeAvailable => Ft8Decoder.IsAvailable;

    /// <summary>True while a receiver is actively decoding.</summary>
    public bool IsEnabled { get { lock (_gate) return _session is not null; } }

    /// <summary>The receiver currently decoding, or -1 when disabled.</summary>
    public int ActiveReceiver { get { lock (_gate) return _session?.Receiver ?? -1; } }

    /// <summary>The protocol currently decoding (FT8 when disabled).</summary>
    public Ft8Protocol ActiveProtocol { get { lock (_gate) return _session?.Protocol ?? Ft8Protocol.Ft8; } }

    /// <summary>
    /// Enter FT8/FT4 decode on a receiver. Idempotent; re-enabling resets the
    /// in-progress slot. Returns false if the native decoder is unavailable.
    /// </summary>
    public bool Enable(int receiver = 0, Ft8Protocol protocol = Ft8Protocol.Ft8)
    {
        if (!Ft8Decoder.IsAvailable)
        {
            _log.LogWarning("FT8 decode requested but zeus_ft8 native library is unavailable.");
            return false;
        }

        lock (_gate)
        {
            _session?.Dispose();
            double slotSeconds = protocol == Ft8Protocol.Ft4 ? 7.5 : 15.0;
            _session = new RxSession(receiver, protocol, slotSeconds);

            if (_decodeWorker is null)
            {
                _workerCts = new CancellationTokenSource();
                _decodeQueue = Channel.CreateBounded<PendingSlot>(
                    new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.DropOldest });
                _decodeWorker = Task.Run(() => DecodeLoopAsync(_workerCts.Token));
            }
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
        }
        cts?.Cancel();
        try { worker?.Wait(TimeSpan.FromSeconds(2)); } catch { /* shutting down */ }
        toDispose?.Dispose();
        cts?.Dispose();
    }

    // Audio thread — must stay cheap: resample + append, hand decode to the worker.
    private void OnRxAudio(int receiver, int sampleRate, ReadOnlyMemory<float> block)
    {
        RxSession? s;
        Channel<PendingSlot>? queue;
        lock (_gate) { s = _session; queue = _decodeQueue; }
        if (s is null || queue is null || receiver != s.Receiver) return;

        if (sampleRate != Ft8Resampler.InputRate)
        {
            // The decimator is fixed 48k->12k; an unexpected rate would alias.
            // Skip rather than feed the decoder wrong-rate audio.
            return;
        }

        float[] decimated = s.Resampler.Process(block.Span);
        if (decimated.Length == 0) return;

        Ft8Slot? completed = s.Accumulator.Add(decimated, DateTime.UtcNow);
        if (completed is { } slot)
            queue.Writer.TryWrite(new PendingSlot(s.Receiver, s.Protocol, s.Decoder, slot, DecodePasses));
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
                    var decodes = pending.Decoder.Decode(pending.Slot.Samples, pending.Protocol, pending.Passes);
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
        catch (OperationCanceledException) { /* normal shutdown */ }
    }

    public void Dispose() => Disable();

    // Per-RX decode state.
    private sealed class RxSession : IDisposable
    {
        public int Receiver { get; }
        public Ft8Protocol Protocol { get; }
        public Ft8Resampler Resampler { get; }
        public Ft8SlotAccumulator Accumulator { get; }
        public Ft8Decoder Decoder { get; }

        public RxSession(int receiver, Ft8Protocol protocol, double slotSeconds)
        {
            Receiver = receiver;
            Protocol = protocol;
            Resampler = new Ft8Resampler();
            Accumulator = new Ft8SlotAccumulator(slotSeconds);
            Decoder = new Ft8Decoder();
        }

        public void Dispose() => Decoder.Dispose();
    }

    private readonly record struct PendingSlot(
        int Receiver, Ft8Protocol Protocol, Ft8Decoder Decoder, Ft8Slot Slot, int Passes);
}
