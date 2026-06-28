// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Text;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class AdifImportTests
{
    [Fact]
    public void Parser_HandlesHeaderAndLengthTaggedRecords()
    {
        const string adif = """
            ADIF Export
            <ADIF_VER:5>3.1.6<PROGRAMID:4>Zeus<EOH>
            <CALL:5>N9WAR<QSO_DATE:8>20260619<TIME_ON:6>142205<FREQ:6>14.250<BAND:3>20M<MODE:3>SSB<EOR>
            <CALL:5>EI6LF<QSO_DATE:8>20260620<TIME_ON:4>0830<BAND:3>40M<MODE:2>CW<EOR>
            """;

        var records = AdifParser.Parse(adif);

        Assert.Equal(2, records.Count);
        Assert.Equal("N9WAR", records[0].Fields["CALL"]);
        Assert.Equal("142205", records[0].Fields["TIME_ON"]);
        Assert.Equal("EI6LF", records[1].Fields["CALL"]);
        Assert.Equal("0830", records[1].Fields["TIME_ON"]);
    }

    [Fact]
    public void Mapping_ImportsBandOnlyQsoAndPreservesExtraAdifFields()
    {
        const string adif = """
            <ADIF_VER:5>3.1.6<EOH>
            <CALL:5>N9WAR<QSO_DATE:8>20260619<TIME_ON:4>1422<BAND:3>20M<MODE:3>SSB
            <RST_SENT:2>59<RST_RCVD:2>57<GRIDSQUARE:4>EN61<NOTES:11>net checkin
            <TIME_OFF:6>143000<STATION_CALLSIGN:5>N9WAR<APP_QRZLOG_LOGID:7>QRZ-123<EOR>
            """;
        var record = AdifParser.Parse(adif).Single();

        var ok = LogService.TryCreateDocumentFromAdifRecord(
            record,
            new DateTime(2026, 6, 19, 14, 30, 0, DateTimeKind.Utc),
            out var doc,
            out var error);

        Assert.True(ok, error);
        Assert.Equal("N9WAR", doc.Callsign);
        Assert.Equal(new DateTime(2026, 6, 19, 14, 22, 0, DateTimeKind.Utc), doc.QsoDateTimeUtc);
        Assert.Null(doc.FrequencyMhz);
        Assert.Equal("20M", doc.Band);
        Assert.Equal("SSB", doc.Mode);
        Assert.Equal("net checkin", doc.Comment);
        Assert.Equal("QRZ-123", doc.QrzLogId);
        Assert.NotNull(doc.QrzUploadedUtc);
        Assert.Equal("143000", doc.AdifFields!["TIME_OFF"]);
        Assert.Equal("N9WAR", doc.AdifFields["STATION_CALLSIGN"]);

        var sb = new StringBuilder();
        LogService.AppendAdifRecord(sb, doc);
        var exported = sb.ToString();

        Assert.DoesNotContain("<FREQ:", exported);
        Assert.Contains("<TIME_OFF:6>143000", exported);
        Assert.Contains("<STATION_CALLSIGN:5>N9WAR", exported);
        Assert.Contains("<APP_QRZLOG_LOGID:7>QRZ-123", exported);
    }

    [Fact]
    public void Export_Ft4_Emits_Mfsk_Mode_With_Ft4_Submode()
    {
        // FT4 is an MFSK SUBMODE in ADIF, not a MODE enum value. Strict parsers
        // (LoTW / Club Log / QRZ) reject MODE=FT4, so a native FT4 QSO must export
        // as MODE=MFSK + SUBMODE=FT4.
        var doc = new LogEntryDocument
        {
            Id = Guid.NewGuid().ToString(),
            QsoDateTimeUtc = new DateTime(2026, 6, 26, 12, 0, 0, DateTimeKind.Utc),
            Callsign = "K1ABC",
            Band = "20M",
            Mode = "FT4",
            RstSent = "-12",
            RstRcvd = "-07",
        };

        var sb = new StringBuilder();
        LogService.AppendAdifRecord(sb, doc);
        var exported = sb.ToString();

        Assert.Contains("<MODE:4>MFSK", exported);
        Assert.Contains("<SUBMODE:3>FT4", exported);
        Assert.DoesNotContain("<MODE:3>FT4", exported);
    }

    [Fact]
    public void Export_Ft8_Keeps_Ft8_Mode_With_No_Submode()
    {
        var doc = new LogEntryDocument
        {
            Id = Guid.NewGuid().ToString(),
            QsoDateTimeUtc = new DateTime(2026, 6, 26, 12, 0, 0, DateTimeKind.Utc),
            Callsign = "K1ABC",
            Band = "20M",
            Mode = "FT8",
            RstSent = "-12",
            RstRcvd = "-07",
        };

        var sb = new StringBuilder();
        LogService.AppendAdifRecord(sb, doc);
        var exported = sb.ToString();

        Assert.Contains("<MODE:3>FT8", exported);
        Assert.DoesNotContain("<SUBMODE:", exported);
    }

    [Fact]
    public void Mapping_RejectsRecordsMissingMinimumQsoFields()
    {
        var record = new AdifRecord(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CALL"] = "N0CALL",
            ["QSO_DATE"] = "20260619",
            ["TIME_ON"] = "1422",
            ["BAND"] = "20M",
        });

        var ok = LogService.TryCreateDocumentFromAdifRecord(
            record,
            DateTime.UtcNow,
            out _,
            out var error);

        Assert.False(ok);
        Assert.Equal("missing MODE", error);
    }

    [Fact]
    public void Mapping_ParsesFrequencyAndSixDigitUtcTime()
    {
        var record = new AdifRecord(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CALL"] = "ei6lf",
            ["QSO_DATE"] = "20260620",
            ["TIME_ON"] = "083015",
            ["FREQ"] = "7.185",
            ["MODE"] = "lsb",
            ["DXCC"] = "245",
            ["CQZ"] = "14",
            ["ITUZ"] = "27",
        });

        var ok = LogService.TryCreateDocumentFromAdifRecord(
            record,
            DateTime.UtcNow,
            out var doc,
            out var error);

        Assert.True(ok, error);
        Assert.Equal("EI6LF", doc.Callsign);
        Assert.Equal(new DateTime(2026, 6, 20, 8, 30, 15, DateTimeKind.Utc), doc.QsoDateTimeUtc);
        Assert.Equal(7.185, doc.FrequencyMhz);
        Assert.Equal(string.Empty, doc.Band);
        Assert.Equal("LSB", doc.Mode);
        Assert.Equal(245, doc.Dxcc);
        Assert.Equal(14, doc.CqZone);
        Assert.Equal(27, doc.ItuZone);
    }

    [Fact]
    public void Export_DeclaresFieldLengthInUtf8BytesNotChars()
    {
        // "José Muñoz" is 10 UTF-16 code units but 12 UTF-8 bytes (é and ñ are
        // two bytes each). ADIF length tags are octet counts; declaring 10
        // would make a spec-compliant importer (LoTW/ClubLog/QRZ/N1MM) read
        // short and desync the rest of the record.
        var doc = new LogEntryDocument
        {
            Id = "id-1",
            Callsign = "EA1ABC",
            QsoDateTimeUtc = new DateTime(2026, 6, 28, 12, 0, 0, DateTimeKind.Utc),
            Band = "20M",
            Mode = "SSB",
            RstSent = "59",
            RstRcvd = "59",
            Name = "José Muñoz",
            CreatedUtc = new DateTime(2026, 6, 28, 12, 0, 0, DateTimeKind.Utc),
        };

        var sb = new StringBuilder();
        LogService.AppendAdifRecord(sb, doc);
        var exported = sb.ToString();

        Assert.Equal(12, Encoding.UTF8.GetByteCount("José Muñoz"));
        Assert.Contains("<NAME:12>José Muñoz", exported);
        Assert.DoesNotContain("<NAME:10>", exported);
        // ASCII fields keep byte==char length.
        Assert.Contains("<CALL:6>EA1ABC", exported);
    }

    [Fact]
    public async Task ExportToAdifFileAsync_WritesAdifToTargetDirectory()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"zeus-export-test-{Guid.NewGuid():N}.db");
        var outDir = Path.Combine(Path.GetTempPath(), $"zeus-export-out-{Guid.NewGuid():N}");
        try
        {
            using var svc = new LogService(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<LogService>.Instance, dbPath);
            await svc.CreateLogEntryAsync(new Zeus.Contracts.CreateLogEntryRequest(
                Callsign: "K2ABC", Name: "Al", FrequencyMhz: 14.074, Band: "20M",
                Mode: "FT8", RstSent: "-12", RstRcvd: "-09"));

            var result = await svc.ExportToAdifFileAsync(outDir);

            Assert.Equal(1, result.Count);
            Assert.True(File.Exists(result.Path));
            Assert.Equal(outDir, Path.GetDirectoryName(result.Path));
            var text = await File.ReadAllTextAsync(result.Path);
            Assert.Contains("<CALL:5>K2ABC", text);
            Assert.Equal(new FileInfo(result.Path).Length, result.Bytes);
        }
        finally
        {
            try { File.Delete(dbPath); } catch { /* best effort */ }
            try { Directory.Delete(outDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void RoundTrip_NonAsciiName_PreservesNameAndFollowingFields()
    {
        // Export emits UTF-8 byte lengths; the parser must read by bytes too,
        // or "José Muñoz" (12 bytes / 10 chars) over-reads into the next field.
        var doc = new LogEntryDocument
        {
            Id = "id-rt",
            Callsign = "EA1ABC",
            QsoDateTimeUtc = new DateTime(2026, 6, 28, 12, 0, 0, DateTimeKind.Utc),
            Band = "20M",
            Mode = "SSB",
            RstSent = "59",
            RstRcvd = "59",
            Name = "José Muñoz",
            Grid = "IN80",
            CreatedUtc = new DateTime(2026, 6, 28, 12, 0, 0, DateTimeKind.Utc),
        };

        var sb = new StringBuilder();
        sb.AppendLine("<ADIF_VER:5>3.1.4<EOH>");
        LogService.AppendAdifRecord(sb, doc);

        var record = AdifParser.Parse(sb.ToString()).Single();

        Assert.Equal("José Muñoz", record.Fields["NAME"]);
        Assert.Equal("IN80", record.Fields["GRIDSQUARE"]);  // stays aligned after the multi-byte field
        Assert.Equal("EA1ABC", record.Fields["CALL"]);
    }
}
