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

using LiteDB;
using Microsoft.Extensions.Logging;

namespace Zeus.Server.Voyeur;

/// <summary>
/// LiteDB persistence + audio-file management for Voyeur Mode logs (zeus-la5).
/// This is the "save and delete these logs" surface: sessions and their
/// per-over segments live in a dedicated <c>voyeur.db</c> (no secrets, so no
/// password — unlike the credential DB), and segment audio lives under a
/// Voyeur root inside the OS Downloads folder so the operator can find/replay
/// the WAVs. Delete removes the records AND the on-disk audio directory; a
/// rolling retention cap prunes the oldest UNPINNED sessions so an unattended
/// multi-day run can't fill the disk.
///
/// All access is serialized under <c>_gate</c> — LiteDB is embedded and the
/// callers (REST thread + the Voyeur drain thread finalizing segments) are
/// distinct threads. None of this runs on the DSP/RX producer thread.
/// </summary>
public sealed class VoyeurStore : IDisposable
{
    private readonly object _gate = new();
    private readonly ILogger<VoyeurStore> _log;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<VoyeurSessionDocument> _sessions;
    private readonly ILiteCollection<VoyeurSegmentDocument> _segments;
    private readonly string _audioRoot;

    /// <summary>Hard cap on retained sessions; oldest UNPINNED beyond this are
    /// pruned (records + audio). Pinned ("saved") sessions never count toward
    /// eviction. Generous — transcripts/metadata are tiny; the prune mainly
    /// bounds segment-audio disk over long unattended use.</summary>
    private const int MaxRetainedSessions = 200;

    public VoyeurStore(ILogger<VoyeurStore> log)
    {
        _log = log;

        var appData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);
        var zeusDir = Path.Combine(appData, "Zeus");
        Directory.CreateDirectory(zeusDir);
        var dbPath = Path.Combine(zeusDir, "voyeur.db");
        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _sessions = _db.GetCollection<VoyeurSessionDocument>("sessions");
        _segments = _db.GetCollection<VoyeurSegmentDocument>("segments");
        _sessions.EnsureIndex(x => x.Id, unique: true);
        _sessions.EnsureIndex(x => x.StartedUtc);
        _segments.EnsureIndex(x => x.SessionId);

        _audioRoot = ResolveAudioRoot();
        Directory.CreateDirectory(_audioRoot);

        _log.LogInformation("VoyeurStore initialized db={Db} audio={Audio}", dbPath, _audioRoot);
    }

    /// <summary>Audio segments land under Downloads/zeus-voyeur so the operator
    /// finds them where recordings live; falls back to the home dir.</summary>
    private static string ResolveAudioRoot()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(home, "Downloads");
        var baseDir = Directory.Exists(downloads) ? downloads : home;
        return Path.Combine(baseDir, "zeus-voyeur");
    }

    public string AudioRoot => _audioRoot;

    /// <summary>Absolute, traversal-guarded directory for a session's audio.
    /// Created on demand. The guard mirrors WavRecorderService: the resolved
    /// path must stay under the Voyeur root, so a crafted session id can't
    /// escape it.</summary>
    public string SessionAudioDir(string sessionId)
    {
        var safe = SanitizeId(sessionId);
        var dir = Path.GetFullPath(Path.Combine(_audioRoot, safe));
        var rootFull = Path.GetFullPath(_audioRoot);
        if (!dir.StartsWith(rootFull, StringComparison.Ordinal))
            throw new InvalidOperationException("session audio path escaped the Voyeur root");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public VoyeurSessionDocument CreateSession(long freqHz, string mode, string band, bool keepAudio)
    {
        lock (_gate)
        {
            var s = new VoyeurSessionDocument
            {
                StartedUtc = DateTime.UtcNow,
                FreqHz = freqHz,
                Mode = mode,
                Band = band,
                Label = $"{freqHz / 1_000_000.0:F3} MHz {mode} — {DateTime.Now:yyyy-MM-dd HH:mm}",
            };
            if (keepAudio) s.AudioDir = SanitizeId(s.Id);
            _sessions.Insert(s);
            PruneLocked();
            return s;
        }
    }

    public void AddSegment(VoyeurSegmentDocument seg, double capturedSeconds, long droppedSamples)
    {
        lock (_gate)
        {
            _segments.Insert(seg);
            var s = _sessions.FindById(seg.SessionId);
            if (s is not null)
            {
                s.SegmentCount += 1;
                s.CapturedSeconds += capturedSeconds;
                s.DroppedSamples = droppedSamples;
                _sessions.Update(s);
            }
        }
    }

    public void FinalizeSession(string sessionId, long droppedSamples)
    {
        lock (_gate)
        {
            var s = _sessions.FindById(sessionId);
            if (s is null) return;
            s.EndedUtc = DateTime.UtcNow;
            s.DroppedSamples = droppedSamples;
            _sessions.Update(s);
        }
    }

    public IReadOnlyList<VoyeurSessionDto> ListSessions()
    {
        lock (_gate)
        {
            return _sessions.Query()
                .OrderByDescending(x => x.StartedUtc)
                .ToList()
                .Select(ToDto)
                .ToList();
        }
    }

    public VoyeurSessionDetailDto? GetSession(string id)
    {
        lock (_gate)
        {
            var s = _sessions.FindById(id);
            if (s is null) return null;
            var segs = _segments.Query()
                .Where(x => x.SessionId == id)
                .OrderBy(x => x.StartedUtc)
                .ToList()
                .Select(ToSegDto)
                .ToList();
            return new VoyeurSessionDetailDto(ToDto(s), segs);
        }
    }

    /// <summary>Rename and/or pin ("save") a session. Returns the updated DTO,
    /// or null if the session doesn't exist.</summary>
    public VoyeurSessionDto? Update(string id, string? label, bool? pinned)
    {
        lock (_gate)
        {
            var s = _sessions.FindById(id);
            if (s is null) return null;
            if (label is not null) s.Label = label.Trim();
            if (pinned is not null) s.Pinned = pinned.Value;
            _sessions.Update(s);
            return ToDto(s);
        }
    }

    /// <summary>Delete a session: its segment records AND its on-disk audio
    /// directory. Idempotent. Returns true if a session was removed.</summary>
    public bool Delete(string id)
    {
        lock (_gate)
        {
            var s = _sessions.FindById(id);
            if (s is null) return false;
            _segments.DeleteMany(x => x.SessionId == id);
            _sessions.Delete(id);
            DeleteAudioDir(s);
            _log.LogInformation("voyeur: deleted session {Id} ({Label})", id, s.Label);
            return true;
        }
    }

    private void PruneLocked()
    {
        // Evict oldest UNPINNED sessions beyond the cap. Pinned/saved logs are
        // never pruned — that's the whole point of the save flag.
        var unpinned = _sessions.Query()
            .Where(x => !x.Pinned)
            .OrderByDescending(x => x.StartedUtc)
            .ToList();
        if (unpinned.Count <= MaxRetainedSessions) return;
        foreach (var s in unpinned.Skip(MaxRetainedSessions))
        {
            _segments.DeleteMany(x => x.SessionId == s.Id);
            _sessions.Delete(s.Id);
            DeleteAudioDir(s);
            _log.LogInformation("voyeur: pruned old session {Id} (retention cap)", s.Id);
        }
    }

    private void DeleteAudioDir(VoyeurSessionDocument s)
    {
        if (s.AudioDir is null) return;
        try
        {
            var dir = Path.GetFullPath(Path.Combine(_audioRoot, s.AudioDir));
            var rootFull = Path.GetFullPath(_audioRoot);
            // Traversal guard: only ever delete inside the Voyeur root.
            if (dir.StartsWith(rootFull, StringComparison.Ordinal) && Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "voyeur: failed to delete audio dir for session {Id}", s.Id);
        }
    }

    private static string SanitizeId(string id)
    {
        // Ids are GUID-N (hex) by construction; this is defense-in-depth so a
        // hand-crafted id can never contain a path separator.
        Span<char> buf = stackalloc char[id.Length];
        int n = 0;
        foreach (var c in id)
            if (char.IsLetterOrDigit(c)) buf[n++] = c;
        return n == 0 ? "session" : new string(buf[..n]);
    }

    private VoyeurSessionDto ToDto(VoyeurSessionDocument s) => new(
        s.Id, s.Label, s.StartedUtc, s.EndedUtc, s.FreqHz, s.Mode, s.Band,
        s.SegmentCount, s.CapturedSeconds, s.DroppedSamples, s.Pinned,
        HasAudio: s.AudioDir is not null);

    private static VoyeurSegmentDto ToSegDto(VoyeurSegmentDocument x) => new(
        x.Id, x.StartedUtc, x.DurationMs, x.PeakDbfs,
        HasAudio: x.AudioFile is not null,
        x.Transcript, x.Callsign, x.CallsignState);

    public void Dispose() => _db.Dispose();
}
