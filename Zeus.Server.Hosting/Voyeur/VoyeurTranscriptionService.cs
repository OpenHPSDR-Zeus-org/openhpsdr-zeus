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

using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zeus.Contracts;

namespace Zeus.Server.Voyeur;

/// <summary>
/// Voyeur Mode (zeus-la5) Phase 2 — the transcription + enrichment pipeline.
/// Captured "overs" (Phase 1) are enqueued here; a SINGLE background worker
/// pulls each one, runs whisper.cpp (in a supervised child process — see
/// <see cref="WhisperTranscriber"/>), extracts candidate callsigns, validates
/// them against QRZ, and writes the transcript + the best attribution back to
/// the segment record.
///
/// This runs entirely OFF the audio/DSP path — it consumes already-saved WAV
/// files, never the live ring — so nothing here can affect RX/PS/TX. A single
/// worker bounds CPU/RAM (whisper at ~20× realtime keeps up with a net easily);
/// the queue is bounded and drops oldest on overflow so a slow run can't grow
/// memory without limit. QRZ lookups are de-duplicated per session and rate-
/// limited, because <c>QrzService.LookupAsync</c> serializes every caller on a
/// single gate — an unthrottled roster storm would starve the operator's own
/// manual lookups.
/// </summary>
public sealed class VoyeurTranscriptionService : BackgroundService
{
    public readonly record struct Job(string SessionId, string SegmentId, string WavPath, int DurationMs);

    private readonly WhisperTranscriber _whisper;
    private readonly VoyeurStore _store;
    private readonly QrzService _qrz;
    private readonly ILogger<VoyeurTranscriptionService> _log;

    private readonly Channel<Job> _queue = Channel.CreateBounded<Job>(
        new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    // Per-callsign QRZ cache + a min interval between live lookups. The worker
    // is single-threaded so this needs no locking.
    private readonly Dictionary<string, (QrzStation? Station, bool Valid)> _qrzCache = new(StringComparer.Ordinal);
    private DateTime _lastQrzLookup = DateTime.MinValue;
    private static readonly TimeSpan QrzMinInterval = TimeSpan.FromMilliseconds(600);

    public VoyeurTranscriptionService(
        WhisperTranscriber whisper,
        VoyeurStore store,
        QrzService qrz,
        ILogger<VoyeurTranscriptionService> log)
    {
        _whisper = whisper;
        _store = store;
        _qrz = qrz;
        _log = log;
    }

    public bool Available => _whisper.Available;

    /// <summary>Queue a captured over for transcription. Non-blocking; drops
    /// silently if transcription is unavailable (capture-only mode).</summary>
    public void Enqueue(Job job)
    {
        if (!_whisper.Available) return;
        _queue.Writer.TryWrite(job);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed transcription must never take the worker down — the
                // over just stays untranscribed.
                _log.LogWarning(ex, "voyeur.transcribe job failed seg={Seg}", job.SegmentId);
            }
        }
    }

    private async Task ProcessAsync(Job job, CancellationToken ct)
    {
        // Generous timeout: whisper runs ~20× realtime, so even a long over
        // finishes in seconds. Cap at the larger of 60 s or 2× the over length
        // and kill+drop past that (a wedged child must never block the queue).
        var timeout = TimeSpan.FromMilliseconds(Math.Max(60_000, job.DurationMs * 2));
        var transcript = await _whisper.TranscribeAsync(job.WavPath, timeout, ct);

        if (string.IsNullOrWhiteSpace(transcript))
        {
            // Whisper found no speech (quiet/garbled over) — record nothing to
            // attribute; leave the segment as captured-only.
            _store.UpdateSegmentTranscript(job.SegmentId, transcript: null,
                callsign: null, callsignState: "unknown", callsignName: null);
            return;
        }

        var (callsign, state, name) = await AttributeAsync(transcript, ct);
        _store.UpdateSegmentTranscript(job.SegmentId, transcript, callsign, state, name);
        _log.LogDebug("voyeur.transcribe seg={Seg} call={Call} state={State}", job.SegmentId, callsign, state);
    }

    // Pick the best callsign from the transcript and validate it against QRZ.
    // confirmed = QRZ resolves it (real licensee); tentative = well-formed but
    // QRZ has no record (DX/foreign/unlisted); unknown = nothing decodable.
    private async Task<(string? callsign, string state, string? name)> AttributeAsync(
        string transcript, CancellationToken ct)
    {
        var candidates = CallsignExtractor.Extract(transcript);
        if (candidates.Count == 0) return (null, "unknown", null);

        // Try candidates in rank order; the first QRZ-confirmed one wins
        // (longest-validated, per the Phase-0 fragment-collision finding).
        foreach (var cand in candidates.Take(5))
        {
            var (station, valid) = await QrzLookupAsync(cand, ct);
            if (valid)
            {
                var name = station?.FirstName ?? station?.Name;
                return (cand, "confirmed", name);
            }
        }

        // None confirmed — surface the top candidate as tentative so the
        // operator sees a best-guess they can verify, clearly marked.
        return (candidates[0], "tentative", null);
    }

    private async Task<(QrzStation? station, bool valid)> QrzLookupAsync(string callsign, CancellationToken ct)
    {
        if (_qrzCache.TryGetValue(callsign, out var cached)) return (cached.Station, cached.Valid);

        // Rate-limit: never hammer QRZ's shared gate and starve manual lookups.
        var since = DateTime.UtcNow - _lastQrzLookup;
        if (since < QrzMinInterval)
            await Task.Delay(QrzMinInterval - since, ct);
        _lastQrzLookup = DateTime.UtcNow;

        QrzStation? station = null;
        try { station = await _qrz.LookupAsync(callsign, ct); }
        catch (Exception ex) { _log.LogDebug(ex, "voyeur.qrz lookup failed {Call}", callsign); }

        bool valid = station is not null;
        _qrzCache[callsign] = (station, valid);
        return (station, valid);
    }
}
