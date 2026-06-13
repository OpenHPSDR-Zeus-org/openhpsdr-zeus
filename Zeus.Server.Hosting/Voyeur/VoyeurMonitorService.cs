// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see ATTRIBUTIONS.md for provenance.

using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zeus.Contracts;
using Zeus.Server.Voyeur;
using Zeus.Server.Wav;

namespace Zeus.Server;

/// <summary>
/// Voyeur Mode (zeus-la5) — Phase 1. Unattended passive monitor: park the
/// radio on a frequency, capture each transmission ("over") to a log the
/// operator can review, save, and delete later. Phase 2 adds whisper.cpp ASR
/// (in a supervised child process) + callsign/QRZ enrichment; Phase 1 lands
/// the safe foundation and the per-over capture + log management.
///
/// SAFETY — "cannot break anything" (zeus-la5 prime directive):
/// • Default OFF. We only subscribe to <see cref="DspPipelineService.RxAudioAvailable"/>
///   while a session is active, so an idle Voyeur Mode adds ZERO code to the
///   RX path.
/// • <see cref="OnRxAudio"/> runs on the DSP/RX producer thread and does ONLY
///   a lock-free <see cref="VoyeurAudioRing.Write"/> (bounded memcpy + two
///   volatile ops) — no alloc, no lock, no IO, no segmenting, no whisper. It is
///   wrapped in try/catch that swallows everything (the producer's Invoke site
///   has no per-delegate guard), and DETACHES on repeated fault so RX returns
///   to a bit-identical no-op.
/// • The ring has NO back-pressure: when full it drops the block and counts it.
///   A stalled/slow consumer is invisible to RX — worst case is a capture gap.
/// • All real work (segment detection, WAV writing, persistence) happens on the
///   drain thread at BELOW-NORMAL priority, so it can never preempt the
///   realtime WDSP / DSP / TX threads.
/// • RX-only, receiver 0; touches neither the audio sink, the TX/IQ path, nor
///   PureSignal.
/// </summary>
public sealed class VoyeurMonitorService : BackgroundService, IDisposable
{
    private readonly DspPipelineService _pipeline;
    private readonly RadioService _radio;
    private readonly VoyeurStore _store;
    private readonly Zeus.Server.Voyeur.VoyeurTranscriptionService _transcription;
    private readonly ILogger<VoyeurMonitorService> _log;

    // Ring sized for ~6 s at 48 kHz — far more than the drain thread ever needs,
    // so an occasional GC pause in the consumer never drops audio.
    private const int RingSamples = 48_000 * 6;
    private const int FaultDetachThreshold = 8;

    private readonly object _ctrl = new();
    private volatile bool _active;
    private volatile bool _degraded;
    private VoyeurAudioRing? _ring;
    private Thread? _drain;
    private CancellationTokenSource? _drainCts;
    private VoyeurSessionDocument? _session;
    private int _faultCount;

    // Live status (written by the drain thread, read by REST — volatile/Interlocked).
    private int _segmentCount;
    private long _capturedMs;
    private int _sampleRate = DspPipelineService.AudioOutputRateHz;

    public VoyeurMonitorService(
        DspPipelineService pipeline,
        RadioService radio,
        VoyeurStore store,
        Zeus.Server.Voyeur.VoyeurTranscriptionService transcription,
        ILogger<VoyeurMonitorService> log)
    {
        _pipeline = pipeline;
        _radio = radio;
        _store = store;
        _transcription = transcription;
        _log = log;
    }

    // No background loop — the drain thread is spun up per session in Start().
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;

    /// <summary>Begin monitoring the radio's current frequency. Idempotent-ish:
    /// returns the existing status if already active.</summary>
    public VoyeurStatusDto Start(bool keepAudio = true)
    {
        lock (_ctrl)
        {
            if (_active) return Status();

            var s = _radio.Snapshot();
            string band = BandUtils.FreqToBand(s.VfoHz) ?? "";
            var session = _store.CreateSession(s.VfoHz, s.Mode.ToString(), band, keepAudio);

            _session = session;
            _segmentCount = 0;
            _capturedMs = 0;
            _faultCount = 0;
            _degraded = false;
            _ring = new VoyeurAudioRing(RingSamples);
            _drainCts = new CancellationTokenSource();

            // Subscribe LAST, after all state is built, so the first OnRxAudio
            // can't see a half-initialized ring.
            _pipeline.RxAudioAvailable += OnRxAudio;
            _active = true;

            var ct = _drainCts.Token;
            _drain = new Thread(() => DrainLoop(session, keepAudio, ct))
            {
                IsBackground = true,
                Name = "voyeur-drain",
                Priority = ThreadPriority.BelowNormal, // never preempt realtime DSP/TX
            };
            _drain.Start();

            _log.LogInformation(
                "voyeur: started session {Id} freq={Freq} mode={Mode} band={Band} keepAudio={Keep}",
                session.Id, s.VfoHz, s.Mode, band, keepAudio);
            return Status();
        }
    }

    /// <summary>Stop monitoring and finalize the current session.</summary>
    public VoyeurStatusDto Stop()
    {
        Thread? drain;
        VoyeurAudioRing? ring;
        VoyeurSessionDocument? session;
        lock (_ctrl)
        {
            if (!_active) return Status();
            // Detach the tap FIRST so no more audio enters the ring, then let
            // the drain thread finish the in-flight over and exit.
            _pipeline.RxAudioAvailable -= OnRxAudio;
            _active = false;
            _drainCts?.Cancel();
            drain = _drain;
            ring = _ring;
            session = _session;
            _drain = null;
        }

        drain?.Join(TimeSpan.FromSeconds(3));
        if (session is not null)
            _store.FinalizeSession(session.Id, ring?.DroppedSamples ?? 0);

        lock (_ctrl)
        {
            _ring = null;
            _session = null;
            _drainCts?.Dispose();
            _drainCts = null;
        }
        _log.LogInformation("voyeur: stopped session {Id}", session?.Id);
        return Status();
    }

    public VoyeurStatusDto Status()
    {
        var session = _session;
        var ring = _ring;
        int fill = ring is null ? 0 : (int)(100L * ring.Available / Math.Max(1, ring.Capacity));
        return new VoyeurStatusDto(
            Active: _active,
            SessionId: session?.Id,
            FreqHz: session?.FreqHz ?? 0,
            Mode: session?.Mode ?? "",
            Band: session?.Band ?? "",
            SegmentCount: Volatile.Read(ref _segmentCount),
            CapturedSeconds: Interlocked.Read(ref _capturedMs) / 1000.0,
            DroppedSamples: ring?.DroppedSamples ?? 0,
            RingFillPct: fill,
            Degraded: _degraded,
            TranscriptionAvailable: _transcription.Available);
    }

    // ---- PRODUCER (DSP/RX thread) — keep this trivially cheap and crash-proof ----
    private void OnRxAudio(int receiver, int sampleRateHz, ReadOnlyMemory<float> samples)
    {
        // receiver 0 only; ignore anything else (single-radio scope).
        if (receiver != 0) return;
        var ring = _ring;
        if (ring is null) return;
        try
        {
            // The ONLY work on the producer thread: a bounded copy into the ring.
            ring.Write(samples.Span);
            _sampleRate = sampleRateHz;
        }
        catch
        {
            // The producer's Invoke site has no per-delegate guard, so an
            // escaping throw would abort the rest of the DSP tick. Swallow it,
            // and after repeated faults detach entirely so RX returns to a
            // bit-identical no-op (the feature self-disables; the radio runs on).
            if (Interlocked.Increment(ref _faultCount) >= FaultDetachThreshold)
            {
                try { _pipeline.RxAudioAvailable -= OnRxAudio; } catch { /* ignore */ }
                _degraded = true;
            }
        }
    }

    // ---- CONSUMER (drain thread, below-normal priority) — all real work here ----
    private void DrainLoop(VoyeurSessionDocument session, bool keepAudio, CancellationToken ct)
    {
        var ring = _ring;
        if (ring is null) return;

        var seg = new VoyeurSegmenter(_sampleRate);
        var scratch = new float[4096];
        string? audioDir = keepAudio ? _store.SessionAudioDir(session.Id) : null;

        WavWriter? writer = null;
        DateTime overStart = default;

        void CloseOver(VoyeurSegmenter.Result r)
        {
            string? file = null;
            if (writer is not null)
            {
                file = Path.GetFileName(writer.Path);
                writer.Dispose();
                writer = null;
            }
            var doc = new VoyeurSegmentDocument
            {
                SessionId = session.Id,
                StartedUtc = overStart,
                DurationMs = r.DurationMs,
                PeakDbfs = r.PeakDbfs,
                AudioFile = file,
            };
            Interlocked.Increment(ref _segmentCount);
            Interlocked.Add(ref _capturedMs, r.DurationMs);
            _store.AddSegment(doc, r.DurationMs / 1000.0, ring.DroppedSamples);
            // Phase 2: hand the saved over to the transcription pipeline (off
            // this thread, off the audio path). No-op when whisper isn't
            // installed (capture-only) or the over had no audio file.
            if (file is not null && audioDir is not null)
            {
                _transcription.Enqueue(new Zeus.Server.Voyeur.VoyeurTranscriptionService.Job(
                    session.Id, doc.Id, Path.Combine(audioDir, file), r.DurationMs));
            }
            _log.LogDebug("voyeur: captured over {Dur}ms peak={Peak:F1}dBFS", r.DurationMs, r.PeakDbfs);
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = ring.Read(scratch);
                if (n == 0)
                {
                    Thread.Sleep(20); // ring empty — yield; below-normal priority
                    continue;
                }
                var block = scratch.AsSpan(0, n);
                var r = seg.Process(block);
                switch (r.Transition)
                {
                    case VoyeurSegmenter.Transition.Started:
                        overStart = DateTime.UtcNow;
                        if (audioDir is not null)
                        {
                            var path = Path.Combine(audioDir, $"over-{overStart:yyyyMMdd-HHmmss-fff}.wav");
                            writer = new WavWriter(path, _sampleRate);
                            writer.Append(block);
                        }
                        break;
                    case VoyeurSegmenter.Transition.Continuing:
                        writer?.Append(block);
                        break;
                    case VoyeurSegmenter.Transition.Ended:
                        CloseOver(r);
                        break;
                }
            }

            // Session stopping — flush any in-flight over.
            var tail = seg.Flush();
            if (tail.Transition == VoyeurSegmenter.Transition.Ended)
                CloseOver(tail);
            else
                writer?.Dispose();
        }
        catch (Exception ex)
        {
            // A crash in the consumer must not affect RX (the tap is already
            // a fire-and-forget ring write). Mark degraded and exit cleanly.
            _degraded = true;
            writer?.Dispose();
            _log.LogError(ex, "voyeur: drain loop failed — monitor degraded, RX unaffected");
        }
    }

    public override void Dispose()
    {
        if (_active) Stop();
        base.Dispose();
    }
}
