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
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

// Per-band last-used (hz, mode). Lives in its own unencrypted LiteDB file
// (zeus-prefs.db) next to zeus.db; band memory isn't sensitive and sharing
// the encrypted credential file would mean either leaking the password or
// juggling two LiteDB connections to the same file.
public sealed class BandMemoryStore : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<BandMemoryEntry> _entries;
    private readonly ILogger<BandMemoryStore> _log;

    public BandMemoryStore(ILogger<BandMemoryStore> log)
    {
        _log = log;
        var dbPath = PrefsDbPath.Get();

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _entries = _db.GetCollection<BandMemoryEntry>("band_memory");
        // Pre-existing rows can violate the unique-Band invariant if they were
        // written by a build before EnsureIndex(unique:true) was added, or by a
        // race in Upsert before the index existed. Build will fail with
        // "duplicate key" and every subsequent request 500s on construction —
        // self-heal by collapsing duplicates (newest UpdatedUtc wins) before
        // asking LiteDB to enforce uniqueness.
        DedupeByBand();
        _entries.EnsureIndex(x => x.Band, unique: true);

        _log.LogInformation("BandMemoryStore initialized at {Path}", dbPath);
    }

    private void DedupeByBand()
    {
        var dupGroups = _entries.FindAll()
            .GroupBy(e => e.Band, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .ToList();
        foreach (var g in dupGroups)
        {
            var keeper = g.OrderByDescending(e => e.UpdatedUtc).First();
            var removed = 0;
            foreach (var dup in g)
            {
                if (dup.Id == keeper.Id) continue;
                _entries.Delete(dup.Id);
                removed++;
            }
            _log.LogWarning(
                "BandMemoryStore: collapsed {Removed} duplicate row(s) for band {Band}; kept Id={KeptId} (UpdatedUtc={Updated:o})",
                removed, g.Key, keeper.Id, keeper.UpdatedUtc);
        }
    }

    public IReadOnlyList<BandMemoryDto> GetAll()
    {
        return _entries
            .FindAll()
            .Select(e => new BandMemoryDto(e.Band, e.Hz, e.Mode))
            .ToArray();
    }

    public BandMemoryDto? Get(string band)
    {
        var e = _entries.FindOne(x => x.Band == band);
        return e is null ? null : new BandMemoryDto(e.Band, e.Hz, e.Mode);
    }

    public void Upsert(string band, long hz, RxMode mode)
    {
        var existing = _entries.FindOne(x => x.Band == band);
        if (existing is null)
        {
            // Concurrent PUTs for the same band (debounced batches, StrictMode
            // double-effects) can both observe FindOne == null and race into
            // Insert; the unique-Band index then trips one of them. Catch the
            // collision and fall through to the update path with a re-fetch.
            try
            {
                _entries.Insert(new BandMemoryEntry
                {
                    Band = band,
                    Hz = hz,
                    Mode = mode,
                    UpdatedUtc = DateTime.UtcNow,
                });
                return;
            }
            catch (LiteException ex) when (ex.ErrorCode == LiteException.INDEX_DUPLICATE_KEY)
            {
                existing = _entries.FindOne(x => x.Band == band);
                if (existing is null) throw;
            }
        }

        existing.Hz = hz;
        existing.Mode = mode;
        existing.UpdatedUtc = DateTime.UtcNow;
        _entries.Update(existing);
    }

    public void Dispose() => _db.Dispose();

}

public sealed class BandMemoryEntry
{
    public int Id { get; set; }
    public string Band { get; set; } = string.Empty;
    public long Hz { get; set; }
    public RxMode Mode { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
