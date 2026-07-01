// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
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

using System.Globalization;
using System.Text;
using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

public sealed class LogService : IDisposable
{
    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<LogEntryDocument> _logs;
    private readonly ILogger<LogService> _log;

    public LogService(ILogger<LogService> log, string? dbPathOverride = null)
    {
        _log = log;
        // The QSO logbook lives in its own plaintext DB (zeus-logbook.db),
        // separate from both the prefs profiles and the retired encrypted
        // zeus.db. Legacy rows are folded in once at startup by
        // LegacyZeusDbMigration.
        var dbPath = dbPathOverride ?? PrefsDbPath.LogbookPath();

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _logs = _db.GetCollection<LogEntryDocument>("logs");
        _logs.EnsureIndex(x => x.Id, unique: true);
        _logs.EnsureIndex(x => x.QsoDateTimeUtc);
        _logs.EnsureIndex(x => x.Callsign);

        _log.LogInformation("LogService initialized at {Path}", dbPath);
    }

    public async Task<LogEntry> CreateLogEntryAsync(CreateLogEntryRequest request, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var doc = new LogEntryDocument
            {
                Id = Guid.NewGuid().ToString(),
                QsoDateTimeUtc = request.QsoDateTimeUtc ?? DateTime.UtcNow,
                Callsign = request.Callsign.ToUpperInvariant(),
                Name = request.Name,
                // 0/negative MHz means "dial unknown" (e.g. a manual log while
                // disconnected) — store null so ADIF omits FREQ and falls back to
                // BAND, rather than exporting a junk FREQ:0.000000.
                FrequencyMhz = request.FrequencyMhz > 0 ? request.FrequencyMhz : null,
                Band = request.Band,
                Mode = request.Mode,
                RstSent = request.RstSent,
                RstRcvd = request.RstRcvd,
                Grid = request.Grid,
                Country = request.Country,
                Dxcc = request.Dxcc,
                CqZone = request.CqZone,
                ItuZone = request.ItuZone,
                State = request.State,
                Comment = request.Comment,
                CreatedUtc = DateTime.UtcNow
            };

            _logs.Insert(doc);
            _log.LogInformation("Created log entry for {Callsign} at {QsoTime}", doc.Callsign, doc.QsoDateTimeUtc);

            return DocumentToEntry(doc);
        }, ct);
    }

    public async Task<LogEntriesResponse> GetLogEntriesAsync(int skip = 0, int take = 100, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var totalCount = _logs.Count();
            var docs = _logs.Query()
                .OrderByDescending(x => x.QsoDateTimeUtc)
                .Skip(skip)
                .Limit(take)
                .ToList();

            var entries = docs.Select(DocumentToEntry).ToList();
            return new LogEntriesResponse(entries, totalCount);
        }, ct);
    }

    public async Task<WorkedCallsignSummary> GetWorkedCallsignSummaryAsync(
        string callsign,
        int recentTake = 5,
        CancellationToken ct = default)
    {
        var normalized = NormalizeCallsign(callsign);
        return await Task.Run(() =>
        {
            if (string.IsNullOrEmpty(normalized))
            {
                return BuildWorkedSummary(normalized, [], recentTake);
            }

            var docs = _logs.Query()
                .Where(x => x.Callsign == normalized)
                .ToList();

            return BuildWorkedSummary(normalized, docs, recentTake);
        }, ct);
    }

    /// <summary>
    /// The set of callsigns ever worked on a DIGITAL voice-of-data mode (FT8 or
    /// FT4 only), upper-cased. This is the authoritative "worked before" source
    /// for the FT8 workspace highlight: a station counts as worked-before only if
    /// a prior QSO was FT8/FT4 — NOT SSB/CW/AM/etc. Distinct from
    /// <see cref="GetWorkedCallsignSummaryAsync"/>, which is all-modes. The
    /// returned set uses case-insensitive comparison so callers can probe a raw
    /// decoded sender directly. Reads through the shared LiteDB lease.
    /// </summary>
    public async Task<IReadOnlySet<string>> GetDigitalWorkedCallsignsAsync(CancellationToken ct = default)
    {
        return await Task.Run<IReadOnlySet<string>>(() =>
        {
            var docs = _logs.Query()
                .Where(x => x.Mode == "FT8" || x.Mode == "FT4")
                .ToList();

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var doc in docs)
            {
                var call = NormalizeCallsign(doc.Callsign);
                if (call.Length > 0)
                    set.Add(call);
            }
            return set;
        }, ct);
    }

    public async Task<LogEntry?> GetLogEntryAsync(string id, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var doc = _logs.FindById(id);
            return doc != null ? DocumentToEntry(doc) : null;
        }, ct);
    }

    public async Task<IEnumerable<LogEntry>> GetLogEntriesByIdsAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var docs = _logs.Query()
                .Where(x => ids.Contains(x.Id))
                .ToList();

            return docs.Select(DocumentToEntry).ToList();
        }, ct);
    }

    public async Task UpdateQrzUploadStatusAsync(string id, string qrzLogId, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            var doc = _logs.FindById(id);
            if (doc != null)
            {
                doc.QrzLogId = qrzLogId;
                doc.QrzUploadedUtc = DateTime.UtcNow;
                _logs.Update(doc);
                _log.LogInformation("Updated QRZ upload status for log entry {Id}", id);
            }
        }, ct);
    }

    /// <summary>
    /// Permanently delete the given log entries. Returns the number of rows
    /// actually removed (ids that no longer exist are simply skipped, so a
    /// double-click or a stale selection deletes what it can and reports the
    /// true count). A null/empty id list is a no-op.
    /// </summary>
    public async Task<int> DeleteLogEntriesAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        var idList = ids?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
        if (idList == null || idList.Count == 0)
            return 0;

        return await Task.Run(() =>
        {
            var deleted = _logs.DeleteMany(x => idList.Contains(x.Id));
            _log.LogInformation("Deleted {Count} log entr(y/ies)", deleted);
            return deleted;
        }, ct);
    }

    public async Task<string> ExportToAdifAsync(IEnumerable<string>? logEntryIds = null, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var docs = logEntryIds != null
                ? _logs.Query().Where(x => logEntryIds.Contains(x.Id)).ToList()
                : _logs.Query().ToList();

            var sb = new StringBuilder();
            sb.AppendLine("ADIF Export from Zeus");
            sb.AppendLine("<ADIF_VER:5>3.1.4");
            sb.AppendLine("<PROGRAMID:4>Zeus");
            sb.AppendLine("<PROGRAMVERSION:5>1.0.0");
            sb.AppendLine("<EOH>");
            sb.AppendLine();

            foreach (var doc in docs)
            {
                AppendAdifRecord(sb, doc);
            }

            return sb.ToString();
        }, ct);
    }

    /// <summary>
    /// Render the logbook (or the given subset) to ADIF and write it as a
    /// timestamped .adi file in <paramref name="directory"/>. A null/blank
    /// directory targets the operator's Downloads folder. Returns the absolute
    /// path written, the QSO count, and the byte size so the UI can confirm
    /// exactly where the file landed.
    /// </summary>
    public async Task<AdifExportToFileResponse> ExportToAdifFileAsync(
        string? directory = null,
        IEnumerable<string>? logEntryIds = null,
        CancellationToken ct = default)
    {
        var ids = logEntryIds?.ToList();
        var count = ids != null
            ? _logs.Query().Where(x => ids.Contains(x.Id)).Count()
            : _logs.Count();
        var adif = await ExportToAdifAsync(ids, ct).ConfigureAwait(false);
        var bytes = Encoding.UTF8.GetBytes(adif);

        var targetDir = ResolveExportDirectory(directory);
        Directory.CreateDirectory(targetDir);
        var fileName = $"zeus-log-{DateTime.UtcNow:yyyyMMdd-HHmmss}.adi";
        var fullPath = Path.Combine(targetDir, fileName);

        await File.WriteAllBytesAsync(fullPath, bytes, ct).ConfigureAwait(false);
        _log.LogInformation("Exported {Count} QSOs to ADIF file {Path} ({Bytes} bytes)",
            count, fullPath, bytes.Length);

        return new AdifExportToFileResponse(fullPath, count, bytes.Length);
    }

    /// <summary>
    /// Resolve a writable export directory. A caller-supplied path wins (after
    /// expanding a leading ~). Otherwise default to the operator's Downloads
    /// folder — there is no <see cref="Environment.SpecialFolder"/> for it, so
    /// derive it from the user-profile home and fall back to the profile root
    /// itself if Downloads can't be created. Cross-platform: %USERPROFILE% on
    /// Windows, $HOME on macOS/Linux.
    /// </summary>
    private static string ResolveExportDirectory(string? directory)
    {
        if (!string.IsNullOrWhiteSpace(directory))
        {
            var trimmed = directory.Trim();
            if (trimmed == "~" || trimmed.StartsWith("~/", StringComparison.Ordinal) ||
                trimmed.StartsWith("~\\", StringComparison.Ordinal))
            {
                var rest = trimmed.Length > 1 ? trimmed[2..] : string.Empty;
                trimmed = Path.Combine(HomeDirectory(), rest);
            }
            return trimmed;
        }

        var home = HomeDirectory();
        var downloads = Path.Combine(home, "Downloads");
        return downloads;
    }

    private static string HomeDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home)) return home;
        home = Environment.GetEnvironmentVariable("HOME")
               ?? Environment.GetEnvironmentVariable("USERPROFILE");
        return string.IsNullOrEmpty(home) ? Directory.GetCurrentDirectory() : home;
    }

    public async Task<AdifImportResponse> ImportAdifAsync(string adif, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var records = AdifParser.Parse(adif);
            var existingKeys = _logs.FindAll()
                .Select(BuildImportKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var errors = new List<AdifImportError>();
            var imported = 0;
            var duplicates = 0;
            var skipped = 0;
            var importedUtc = DateTime.UtcNow;

            for (var i = 0; i < records.Count; i++)
            {
                var recordNumber = i + 1;
                if (!TryCreateDocumentFromAdifRecord(records[i], importedUtc, out var doc, out var error))
                {
                    skipped++;
                    if (errors.Count < 25)
                        errors.Add(new AdifImportError(recordNumber, error));
                    continue;
                }

                var key = BuildImportKey(doc);
                if (existingKeys.Contains(key))
                {
                    duplicates++;
                    continue;
                }

                _logs.Insert(doc);
                existingKeys.Add(key);
                imported++;
            }

            _log.LogInformation(
                "Imported ADIF logbook records total={Total} imported={Imported} duplicates={Duplicates} skipped={Skipped}",
                records.Count,
                imported,
                duplicates,
                skipped);

            return new AdifImportResponse(
                TotalRecords: records.Count,
                ImportedCount: imported,
                DuplicateCount: duplicates,
                SkippedCount: skipped,
                Errors: errors);
        }, ct);
    }

    public void Dispose()
    {
        _dbLease.Dispose();
    }

    private static LogEntry DocumentToEntry(LogEntryDocument doc) => new(
        Id: doc.Id,
        QsoDateTimeUtc: doc.QsoDateTimeUtc,
        Callsign: doc.Callsign,
        Name: doc.Name,
        FrequencyMhz: doc.FrequencyMhz,
        Band: doc.Band,
        Mode: doc.Mode,
        RstSent: doc.RstSent,
        RstRcvd: doc.RstRcvd,
        Grid: doc.Grid,
        Country: doc.Country,
        Dxcc: doc.Dxcc,
        CqZone: doc.CqZone,
        ItuZone: doc.ItuZone,
        State: doc.State,
        Comment: doc.Comment,
        CreatedUtc: doc.CreatedUtc,
        QrzLogId: doc.QrzLogId,
        QrzUploadedUtc: doc.QrzUploadedUtc);

    internal static WorkedCallsignSummary BuildWorkedSummary(
        string callsign,
        IEnumerable<LogEntryDocument> docs,
        int recentTake = 5)
    {
        var normalized = NormalizeCallsign(callsign);
        var ordered = docs
            .Where(d => NormalizeCallsign(d.Callsign) == normalized)
            .OrderByDescending(d => ToUtc(d.QsoDateTimeUtc))
            .ToList();

        var last = ordered.FirstOrDefault();
        var boundedRecentTake = Math.Clamp(recentTake, 1, 10);
        var recent = ordered
            .Take(boundedRecentTake)
            .Select(d => new WorkedCallsignRecentQso(
                QsoDateTimeUtc: ToUtc(d.QsoDateTimeUtc),
                Band: EmptyToNull(d.Band),
                Mode: EmptyToNull(d.Mode),
                FrequencyMhz: d.FrequencyMhz,
                RstSent: EmptyToNull(d.RstSent),
                RstRcvd: EmptyToNull(d.RstRcvd),
                Name: EmptyToNull(d.Name),
                Grid: EmptyToNull(d.Grid),
                Country: EmptyToNull(d.Country),
                State: EmptyToNull(d.State),
                Comment: EmptyToNull(d.Comment),
                QrzLogId: EmptyToNull(d.QrzLogId)))
            .ToList();

        return new WorkedCallsignSummary(
            Callsign: normalized,
            WorkedBefore: ordered.Count > 0,
            TotalCount: ordered.Count,
            LastWorkedUtc: last is null ? null : ToUtc(last.QsoDateTimeUtc),
            LastBand: last is null ? null : EmptyToNull(last.Band),
            LastMode: last is null ? null : EmptyToNull(last.Mode),
            LastFrequencyMhz: last?.FrequencyMhz,
            LastRstSent: last is null ? null : EmptyToNull(last.RstSent),
            LastRstRcvd: last is null ? null : EmptyToNull(last.RstRcvd),
            LastName: last is null ? null : EmptyToNull(last.Name),
            LastGrid: last is null ? null : EmptyToNull(last.Grid),
            LastCountry: last is null ? null : EmptyToNull(last.Country),
            LastState: last is null ? null : EmptyToNull(last.State),
            LastComment: last is null ? null : EmptyToNull(last.Comment),
            Bands: DistinctNonEmpty(ordered.Select(d => d.Band)),
            Modes: DistinctNonEmpty(ordered.Select(d => d.Mode)),
            RecentQsos: recent);
    }

    internal static bool TryCreateDocumentFromAdifRecord(
        AdifRecord record,
        DateTime createdUtc,
        out LogEntryDocument doc,
        out string error)
    {
        doc = new LogEntryDocument();
        error = string.Empty;

        var fields = record.Fields;
        var callsign = FieldValue(fields, "CALL");
        if (string.IsNullOrWhiteSpace(callsign))
        {
            error = "missing CALL";
            return false;
        }

        var qsoDate = FieldValue(fields, "QSO_DATE");
        if (string.IsNullOrWhiteSpace(qsoDate))
        {
            error = "missing QSO_DATE";
            return false;
        }

        var timeOn = FieldValue(fields, "TIME_ON");
        if (string.IsNullOrWhiteSpace(timeOn))
        {
            error = "missing TIME_ON";
            return false;
        }

        if (!TryParseAdifDateTime(qsoDate, timeOn, out var qsoUtc))
        {
            error = "invalid QSO_DATE or TIME_ON";
            return false;
        }

        var mode = FieldValue(fields, "MODE");
        if (string.IsNullOrWhiteSpace(mode))
        {
            error = "missing MODE";
            return false;
        }

        var band = FieldValue(fields, "BAND");
        var frequencyMhz = ParseAdifNumber(FieldValue(fields, "FREQ"));
        if (string.IsNullOrWhiteSpace(band) && !frequencyMhz.HasValue)
        {
            error = "missing BAND or FREQ";
            return false;
        }

        var qrzLogId = FieldValue(fields, "APP_QRZLOG_LOGID", "APP_QRZ_LOGID");
        doc = new LogEntryDocument
        {
            Id = Guid.NewGuid().ToString(),
            QsoDateTimeUtc = qsoUtc,
            Callsign = callsign.Trim().ToUpperInvariant(),
            Name = FieldValue(fields, "NAME"),
            FrequencyMhz = frequencyMhz,
            Band = band ?? string.Empty,
            Mode = mode.Trim().ToUpperInvariant(),
            RstSent = FieldValue(fields, "RST_SENT") ?? string.Empty,
            RstRcvd = FieldValue(fields, "RST_RCVD") ?? string.Empty,
            Grid = FieldValue(fields, "GRIDSQUARE", "GRID"),
            Country = FieldValue(fields, "COUNTRY"),
            Dxcc = ParseAdifInteger(FieldValue(fields, "DXCC")),
            CqZone = ParseAdifInteger(FieldValue(fields, "CQZ", "CQ_ZONE")),
            ItuZone = ParseAdifInteger(FieldValue(fields, "ITUZ", "ITU_ZONE")),
            State = FieldValue(fields, "STATE"),
            Comment = FieldValue(fields, "COMMENT", "NOTES"),
            CreatedUtc = createdUtc,
            QrzLogId = qrzLogId,
            QrzUploadedUtc = string.IsNullOrWhiteSpace(qrzLogId) ? null : createdUtc,
            AdifFields = fields
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Key))
                .ToDictionary(kv => kv.Key.Trim().ToUpperInvariant(), kv => kv.Value, StringComparer.OrdinalIgnoreCase),
        };

        return true;
    }

    private static string BuildImportKey(LogEntryDocument doc)
    {
        var bandOrFreq = !string.IsNullOrWhiteSpace(doc.Band)
            ? doc.Band.Trim().ToUpperInvariant()
            : doc.FrequencyMhz?.ToString("F6", CultureInfo.InvariantCulture) ?? string.Empty;

        return string.Join(
            "|",
            NormalizeCallsign(doc.Callsign),
            ToUtc(doc.QsoDateTimeUtc).Ticks.ToString(CultureInfo.InvariantCulture),
            bandOrFreq,
            EmptyToNull(doc.Mode)?.ToUpperInvariant() ?? string.Empty);
    }

    private static string? FieldValue(IReadOnlyDictionary<string, string> fields, params string[] names)
    {
        foreach (var name in names)
        {
            if (fields.TryGetValue(name, out var value))
            {
                var trimmed = value.Trim();
                if (trimmed.Length > 0)
                    return trimmed;
            }
        }

        return null;
    }

    private static bool TryParseAdifDateTime(string qsoDate, string timeOn, out DateTime qsoUtc)
    {
        qsoUtc = default;
        if (!DateTime.TryParseExact(
                qsoDate.Trim(),
                "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            return false;
        }

        var time = timeOn.Trim();
        if (time.Length != 4 && time.Length != 6)
            return false;

        if (!int.TryParse(time[..2], NumberStyles.None, CultureInfo.InvariantCulture, out var hour)
            || !int.TryParse(time.Substring(2, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var minute))
        {
            return false;
        }

        var second = 0;
        if (time.Length == 6
            && !int.TryParse(time.Substring(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out second))
        {
            return false;
        }

        if (hour is < 0 or > 23 || minute is < 0 or > 59 || second is < 0 or > 59)
            return false;

        qsoUtc = new DateTime(date.Year, date.Month, date.Day, hour, minute, second, DateTimeKind.Utc);
        return true;
    }

    private static double? ParseAdifNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (!double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return null;

        return parsed > 0 && double.IsFinite(parsed) ? parsed : null;
    }

    private static int? ParseAdifInteger(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>Internal so AdifUtcTimezoneTests can pin the
    /// <see cref="DateTime.Kind"/> behaviour without standing up a LiteDB
    /// round-trip. The method itself remains a private detail of the ADIF
    /// export path.</summary>
    internal static void AppendAdifRecord(StringBuilder sb, LogEntryDocument doc)
    {
        AppendAdifField(sb, "CALL", doc.Callsign);
        // .ToUniversalTime() is defensive: LiteDB's default BsonMapper
        // serialises DateTime as Local on write and returns Kind=Local on
        // read, so `doc.QsoDateTimeUtc` round-trips through the store with
        // the wrong Kind even though we wrote DateTime.UtcNow at creation
        // time. .ToString() doesn't do timezone conversion — it just
        // formats whatever value the DateTime holds. Without the explicit
        // ToUniversalTime() ADIF would emit local-time clocks, which broke
        // the QRZ.com upload (the field name is qsoDateTimeUtc precisely
        // because callers downstream rely on it being UTC).
        AppendAdifField(sb, "QSO_DATE", doc.QsoDateTimeUtc.ToUniversalTime().ToString("yyyyMMdd"));
        AppendAdifField(sb, "TIME_ON", doc.QsoDateTimeUtc.ToUniversalTime().ToString("HHmmss"));
        if (doc.FrequencyMhz.HasValue)
            AppendAdifField(sb, "FREQ", doc.FrequencyMhz.Value.ToString("F6", CultureInfo.InvariantCulture));
        AppendAdifField(sb, "BAND", doc.Band);
        // FT4 is an MFSK SUBMODE in ADIF (3.1.x), not a top-level MODE enum value;
        // strict parsers (LoTW / Club Log / QRZ) reject MODE=FT4. Emit it the
        // canonical way. FT8 is a valid MODE and passes through unchanged.
        if (string.Equals(doc.Mode, "FT4", StringComparison.OrdinalIgnoreCase))
        {
            AppendAdifField(sb, "MODE", "MFSK");
            // Don't duplicate a SUBMODE already carried in the imported fields
            // (those are re-emitted verbatim by AppendAdditionalAdifFields).
            var hasSubmode = doc.AdifFields is not null && doc.AdifFields.ContainsKey("SUBMODE");
            if (!hasSubmode)
                AppendAdifField(sb, "SUBMODE", "FT4");
        }
        else
        {
            AppendAdifField(sb, "MODE", doc.Mode);
        }
        AppendAdifField(sb, "RST_SENT", doc.RstSent);
        AppendAdifField(sb, "RST_RCVD", doc.RstRcvd);

        if (!string.IsNullOrEmpty(doc.Name))
            AppendAdifField(sb, "NAME", doc.Name);
        if (!string.IsNullOrEmpty(doc.Grid))
            AppendAdifField(sb, "GRIDSQUARE", doc.Grid);
        if (!string.IsNullOrEmpty(doc.Country))
            AppendAdifField(sb, "COUNTRY", doc.Country);
        if (doc.Dxcc.HasValue)
            AppendAdifField(sb, "DXCC", doc.Dxcc.Value.ToString());
        if (doc.CqZone.HasValue)
            AppendAdifField(sb, "CQZ", doc.CqZone.Value.ToString());
        if (doc.ItuZone.HasValue)
            AppendAdifField(sb, "ITUZ", doc.ItuZone.Value.ToString());
        if (!string.IsNullOrEmpty(doc.State))
            AppendAdifField(sb, "STATE", doc.State);
        if (!string.IsNullOrEmpty(doc.Comment))
            AppendAdifField(sb, "COMMENT", doc.Comment);

        AppendAdditionalAdifFields(sb, doc);
        sb.AppendLine("<EOR>");
    }

    private static void AppendAdditionalAdifFields(StringBuilder sb, LogEntryDocument doc)
    {
        if (doc.AdifFields is null || doc.AdifFields.Count == 0)
            return;

        foreach (var (fieldName, value) in doc.AdifFields.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (ManagedAdifFields.Contains(fieldName))
                continue;

            AppendAdifField(sb, fieldName, value);
        }
    }

    private static readonly HashSet<string> ManagedAdifFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "CALL",
        "QSO_DATE",
        "TIME_ON",
        "FREQ",
        "BAND",
        "MODE",
        "RST_SENT",
        "RST_RCVD",
        "NAME",
        "GRIDSQUARE",
        "COUNTRY",
        "DXCC",
        "CQZ",
        "ITUZ",
        "STATE",
        "COMMENT",
    };

    private static void AppendAdifField(StringBuilder sb, string fieldName, string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        // ADIF declares each field length as an OCTET count, not a UTF-16
        // code-unit count. Using value.Length corrupts any record with a
        // non-ASCII byte (e.g. "José" emits <NAME:4> but writes 5 bytes),
        // desyncing byte-based importers (LoTW, ClubLog, QRZ, N1MM). Emit
        // the UTF-8 byte length so the field is spec-correct.
        sb.Append($"<{fieldName}:{Encoding.UTF8.GetByteCount(value)}>{value} ");
    }

    private static DateTime ToUtc(DateTime dt) => dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();

    private static string NormalizeCallsign(string? callsign) =>
        (callsign ?? string.Empty).Trim().ToUpperInvariant();

    private static string? EmptyToNull(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static IReadOnlyList<string> DistinctNonEmpty(IEnumerable<string?> values)
    {
        return values
            .Select(EmptyToNull)
            .Where(v => v is not null)
            .Select(v => v!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

}

internal sealed class LogEntryDocument
{
    public string Id { get; set; } = string.Empty;
    public DateTime QsoDateTimeUtc { get; set; }
    public string Callsign { get; set; } = string.Empty;
    public string? Name { get; set; }
    public double? FrequencyMhz { get; set; }
    public string Band { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
    public string RstSent { get; set; } = string.Empty;
    public string RstRcvd { get; set; } = string.Empty;
    public string? Grid { get; set; }
    public string? Country { get; set; }
    public int? Dxcc { get; set; }
    public int? CqZone { get; set; }
    public int? ItuZone { get; set; }
    public string? State { get; set; }
    public string? Comment { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string? QrzLogId { get; set; }
    public DateTime? QrzUploadedUtc { get; set; }
    public Dictionary<string, string>? AdifFields { get; set; }
}

public sealed record WorkedCallsignSummary(
    string Callsign,
    bool WorkedBefore,
    int TotalCount,
    DateTime? LastWorkedUtc,
    string? LastBand,
    string? LastMode,
    double? LastFrequencyMhz,
    string? LastRstSent,
    string? LastRstRcvd,
    string? LastName,
    string? LastGrid,
    string? LastCountry,
    string? LastState,
    string? LastComment,
    IReadOnlyList<string> Bands,
    IReadOnlyList<string> Modes,
    IReadOnlyList<WorkedCallsignRecentQso> RecentQsos);

public sealed record WorkedCallsignRecentQso(
    DateTime QsoDateTimeUtc,
    string? Band,
    string? Mode,
    double? FrequencyMhz,
    string? RstSent,
    string? RstRcvd,
    string? Name,
    string? Grid,
    string? Country,
    string? State,
    string? Comment,
    string? QrzLogId);
