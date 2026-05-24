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

using System.Globalization;
using System.Text;
using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

public sealed class LogService : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<LogEntryDocument> _logs;
    private readonly ILogger<LogService> _log;

    public LogService(ILogger<LogService> log, CredentialStore credStore)
    {
        _log = log;
        // Use the same database as CredentialStore for consistency
        var dbPath = GetDatabasePath();
        var dbPassword = GetDatabasePassword();

        var connectionString = $"Filename={dbPath};Password={dbPassword};Connection=shared";
        _db = new LiteDatabase(connectionString);
        _logs = _db.GetCollection<LogEntryDocument>("logs");
        _logs.EnsureIndex(x => x.Id, unique: true);
        _logs.EnsureIndex(x => x.QsoDateTimeUtc);
        _logs.EnsureIndex(x => x.Callsign);

        _log.LogInformation("LogService initialized");
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
                FrequencyMhz = request.FrequencyMhz,
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

    public void Dispose()
    {
        _db?.Dispose();
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
        AppendAdifField(sb, "FREQ", doc.FrequencyMhz.ToString("F6", CultureInfo.InvariantCulture));
        AppendAdifField(sb, "BAND", doc.Band);
        AppendAdifField(sb, "MODE", doc.Mode);
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

        sb.AppendLine("<EOR>");
    }

    private static void AppendAdifField(StringBuilder sb, string fieldName, string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        sb.Append($"<{fieldName}:{value.Length}>{value} ");
    }

    private static string GetDatabasePath()
    {
        var appDataDir = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);

        var zeusDir = Path.Combine(appDataDir, "Zeus");
        return Path.Combine(zeusDir, "zeus.db");
    }

    private static string GetDatabasePassword()
    {
        // Read the same password that CredentialStore uses
        var appDataDir = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);

        var zeusDir = Path.Combine(appDataDir, "Zeus");
        var keyPath = Path.Combine(zeusDir, ".dbkey");

        if (File.Exists(keyPath))
        {
            return File.ReadAllText(keyPath);
        }

        throw new InvalidOperationException("Database key not found. CredentialStore must be initialized first.");
    }
}

internal sealed class LogEntryDocument
{
    public string Id { get; set; } = string.Empty;
    public DateTime QsoDateTimeUtc { get; set; }
    public string Callsign { get; set; } = string.Empty;
    public string? Name { get; set; }
    public double FrequencyMhz { get; set; }
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
}
