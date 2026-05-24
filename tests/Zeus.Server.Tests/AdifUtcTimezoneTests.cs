// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// Regression for issue #486 (zeus-1sc): QRZ.com uploads (and ADIF
// exports) used to emit local-clock times because LiteDB's default
// BsonMapper round-trips DateTime with Kind=Local on read — even
// when the field was DateTime.UtcNow at write time. .ToString()
// formats the stored value without timezone conversion, so the
// local-kinded value reached QRZ as if it were UTC and stamped
// every QSO at the operator's wall-clock hour. Award credit and
// DXCC matching for anyone outside UTC was wrong.
//
// The fix is a defensive .ToUniversalTime() at both ADIF format
// sites. These tests pin the behaviour by passing a DateTime that
// has Kind=Local but represents a known UTC moment — exactly what
// LiteDB returns — and asserting the ADIF still carries UTC.

using System.Text;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class AdifUtcTimezoneTests
{
    // 19:30:00 UTC on 2026-05-24. Pick a wall-clock hour high enough that
    // a CET-summer browser (UTC+2) shifts it into the next day — without
    // the fix, ToString("yyyyMMdd") on a Local-kinded version would emit
    // either 20260524 or 20260525 depending on the test box's TZ, but
    // never the right value consistently. With the fix it's always
    // 20260524 / 193000.
    private static readonly DateTime QsoUtc =
        new(2026, 5, 24, 19, 30, 0, DateTimeKind.Utc);
    private const string ExpectedDate = "20260524";
    private const string ExpectedTime = "193000";

    /// <summary>Build a Kind=Local DateTime that represents <see cref="QsoUtc"/>
    /// in the test host's local zone. This is exactly what LiteDB's default
    /// BsonMapper hands back to <c>LogService</c> after the round-trip:
    /// the moment in time is right, but the <see cref="DateTime.Kind"/>
    /// is wrong. A naive <c>.ToString("HHmmss")</c> on this value would
    /// format the LOCAL clock, not the UTC clock.</summary>
    private static DateTime QsoSeenAsLocal => QsoUtc.ToLocalTime();

    [Fact]
    public void LogService_AdifRecord_EmitsUtcClock_EvenWhenKindIsLocal()
    {
        var doc = new LogEntryDocument
        {
            Id = "test-id",
            QsoDateTimeUtc = QsoSeenAsLocal,    // ← LiteDB shape
            Callsign = "EA5IUE",
            FrequencyMhz = 21.065,
            Band = "15m",
            Mode = "CW",
            RstSent = "599",
            RstRcvd = "599",
        };
        var sb = new StringBuilder();

        LogService.AppendAdifRecord(sb, doc);

        var adif = sb.ToString();
        Assert.Contains($"<QSO_DATE:8>{ExpectedDate}", adif);
        Assert.Contains($"<TIME_ON:6>{ExpectedTime}", adif);
    }

    [Fact]
    public void QrzService_AdifConversion_EmitsUtcClock_EvenWhenKindIsLocal()
    {
        // Same shape, but exercising the QRZ publish path which converts a
        // LogEntry DTO (not the LiteDB doc) — the DTO is built by
        // LogService.MapDocumentToDto which copies QsoDateTimeUtc through
        // unchanged, so the wrong Kind reaches QRZ the same way.
        var entry = new LogEntry(
            Id: "test-id",
            QsoDateTimeUtc: QsoSeenAsLocal,
            Callsign: "EA5IUE",
            Name: null,
            FrequencyMhz: 21.065,
            Band: "15m",
            Mode: "CW",
            RstSent: "599",
            RstRcvd: "599",
            Grid: null,
            Country: null,
            Dxcc: null,
            CqZone: null,
            ItuZone: null,
            State: null,
            Comment: null,
            CreatedUtc: DateTime.UtcNow);

        var adif = QrzService.ConvertLogEntryToAdif(entry);

        Assert.Contains($"<QSO_DATE:8>{ExpectedDate}", adif);
        Assert.Contains($"<TIME_ON:6>{ExpectedTime}", adif);
    }

    [Fact]
    public void LogService_AdifRecord_AlsoCorrectForActuallyUtcKind()
    {
        // Sanity check: when the DateTime ALREADY has Kind=Utc (which is
        // the case for freshly-created entries before any LiteDB
        // round-trip — see LogService.CreateLogEntryAsync setting
        // QsoDateTimeUtc = DateTime.UtcNow), the .ToUniversalTime() call
        // is a no-op and the output is still correct. This guards against
        // a future "optimisation" that strips the ToUniversalTime() call
        // believing it's redundant.
        var doc = new LogEntryDocument
        {
            Id = "test-id",
            QsoDateTimeUtc = QsoUtc,
            Callsign = "EA5IUE",
            FrequencyMhz = 21.065,
            Band = "15m",
            Mode = "CW",
            RstSent = "599",
            RstRcvd = "599",
        };
        var sb = new StringBuilder();

        LogService.AppendAdifRecord(sb, doc);

        var adif = sb.ToString();
        Assert.Contains($"<QSO_DATE:8>{ExpectedDate}", adif);
        Assert.Contains($"<TIME_ON:6>{ExpectedTime}", adif);
    }
}
