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

namespace Zeus.Server.Voyeur;

/// <summary>
/// A Voyeur Mode monitoring session: one unattended "listen on a frequency"
/// run. Persisted in LiteDB. Phase 1 captures the per-"over" transmission
/// segments (timestamp / duration / level / audio file). The transcript and
/// callsign-roster fields are reserved for Phase 2 (ASR) and Phase 3
/// (enrichment) — they're nullable so those phases are purely additive.
/// </summary>
public sealed class VoyeurSessionDocument
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>Operator-editable name. Defaults to the freq + start time.</summary>
    public string Label { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public DateTime? EndedUtc { get; set; }
    public long FreqHz { get; set; }
    public string Mode { get; set; } = "";
    public string Band { get; set; } = "";
    public int SegmentCount { get; set; }
    /// <summary>Total captured "over" audio across the session, seconds.</summary>
    public double CapturedSeconds { get; set; }
    /// <summary>Samples the safety ring dropped during this session (consumer
    /// fell behind). Telemetry — non-zero means a busy CPU, never a radio fault.</summary>
    public long DroppedSamples { get; set; }
    /// <summary>Pinned sessions are protected from the rolling-retention prune
    /// (this is the operator's "save this log" flag).</summary>
    public bool Pinned { get; set; }
    /// <summary>Relative directory (under the Voyeur root) holding this
    /// session's segment WAVs. Null when audio retention is off.</summary>
    public string? AudioDir { get; set; }
    /// <summary>Phase 3: LLM-generated topic digest ("what was discussed").
    /// Null until the operator generates it.</summary>
    public string? Digest { get; set; }
}

/// <summary>One captured transmission ("over"). Phase 2 fills Transcript /
/// Callsign / CallsignState from ASR + QRZ validation.</summary>
public sealed class VoyeurSegmentDocument
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SessionId { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public int DurationMs { get; set; }
    public float PeakDbfs { get; set; }
    /// <summary>WAV file name within the session's AudioDir, or null.</summary>
    public string? AudioFile { get; set; }
    // ---- Phase 2+ (reserved; null in Phase 1) ----
    public string? Transcript { get; set; }
    public string? Callsign { get; set; }
    /// <summary>"confirmed" | "tentative" | "unknown" — QRZ-validation state.</summary>
    public string? CallsignState { get; set; }
    /// <summary>QRZ operator name when the callsign was confirmed (roster enrichment).</summary>
    public string? CallsignName { get; set; }
}

// ---- DTOs (wire shape for the REST API) ----

public sealed record VoyeurStatusDto(
    bool Active,
    string? SessionId,
    long FreqHz,
    string Mode,
    string Band,
    int SegmentCount,
    double CapturedSeconds,
    long DroppedSamples,
    int RingFillPct,
    bool Degraded,
    bool TranscriptionAvailable);

public sealed record VoyeurSessionDto(
    string Id,
    string Label,
    DateTime StartedUtc,
    DateTime? EndedUtc,
    long FreqHz,
    string Mode,
    string Band,
    int SegmentCount,
    double CapturedSeconds,
    long DroppedSamples,
    bool Pinned,
    bool HasAudio);

public sealed record VoyeurSegmentDto(
    string Id,
    DateTime StartedUtc,
    int DurationMs,
    float PeakDbfs,
    bool HasAudio,
    string? Transcript,
    string? Callsign,
    string? CallsignState,
    string? CallsignName);

public sealed record VoyeurSessionDetailDto(
    VoyeurSessionDto Session,
    IReadOnlyList<VoyeurSegmentDto> Segments);

public sealed record VoyeurUpdateRequest(string? Label, bool? Pinned);

// ---- Phase 3: report / roster / search ----

public sealed record VoyeurRosterEntry(
    string Callsign,
    string? Name,
    string State,          // confirmed | tentative
    int OverCount,
    DateTime FirstHeardUtc,
    DateTime LastHeardUtc);

public sealed record VoyeurReportDto(
    VoyeurSessionDto Session,
    IReadOnlyList<VoyeurRosterEntry> Roster,
    int UniqueStations,
    int ConfirmedStations,
    int TranscribedOvers,
    // LLM topic digest (Stage B); null until generated.
    string? Digest);

public sealed record VoyeurSearchHit(
    string SessionId,
    string SessionLabel,
    long FreqHz,
    DateTime StartedUtc,
    IReadOnlyList<VoyeurSegmentDto> Matches);
