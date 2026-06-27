// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Globalization;
using System.Security;
using System.Text;
using Zeus.Contracts;

namespace Zeus.Server.Wsjtx;

/// <summary>
/// Builds the N1MM Logger+ "contactinfo" UDP datagram (XML) so loggers that
/// listen for the N1MM external-broadcast format — HRD Logbook "QSO Forwarding",
/// DXKeeper via the N1MM→DXKeeper Gateway — pick up Zeus's logged QSOs. This is
/// a DIFFERENT wire format from the WSJT-X type-12 LoggedADIF datagram (those
/// consumers do not parse the WSJT-X binary format).
///
/// Field order and the <c>rxfreq</c>/<c>txfreq</c> units (tens of Hz — i.e.
/// frequency in Hz divided by 10) follow the N1MM External UDP Broadcasts spec
/// (n1mmwp.hamdocs.com/appendices/external-udp-broadcasts, verified June 2026).
/// The full element set is emitted in order; fields Zeus does not have are left
/// empty so position/tag-indexed parsers still line up.
///
/// SEND-ONLY: this only produces a byte[]; it never opens a socket and never
/// triggers TX.
/// </summary>
public static class N1mmContactInfoEncoder
{
    /// <summary>Encode one logged QSO. <paramref name="myCall"/> is the operator
    /// station call (may be empty if unresolved).</summary>
    public static byte[] Encode(LogEntry entry, string myCall)
        => Encoding.UTF8.GetBytes(EncodeXml(entry, myCall));

    internal static string EncodeXml(LogEntry entry, string myCall)
    {
        // rxfreq / txfreq in tens of Hz. We log a single QSO frequency, so RX==TX.
        long freqTensHz = 0;
        if (entry.FrequencyMhz is { } mhz && mhz > 0)
            freqTensHz = (long)Math.Round(mhz * 100_000.0); // MHz -> tens of Hz (×1e6/10)

        var sb = new StringBuilder(1024);
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n");
        sb.Append("<contactinfo>");
        Tag(sb, "app", "Zeus");
        Tag(sb, "contestname", "");
        Tag(sb, "contestnr", "1");
        Tag(sb, "timestamp", entry.QsoDateTimeUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        Tag(sb, "mycall", myCall);
        Tag(sb, "band", NormalizeBand(entry.Band, entry.FrequencyMhz));
        Tag(sb, "rxfreq", freqTensHz.ToString(CultureInfo.InvariantCulture));
        Tag(sb, "txfreq", freqTensHz.ToString(CultureInfo.InvariantCulture));
        Tag(sb, "operator", myCall);
        Tag(sb, "mode", entry.Mode ?? "");
        Tag(sb, "call", entry.Callsign ?? "");
        Tag(sb, "countryprefix", "");
        Tag(sb, "wpxprefix", "");
        Tag(sb, "stationprefix", myCall);
        Tag(sb, "continent", "");
        Tag(sb, "snt", entry.RstSent ?? "");
        Tag(sb, "sntnr", "0");
        Tag(sb, "rcv", entry.RstRcvd ?? "");
        Tag(sb, "rcvnr", "0");
        Tag(sb, "gridsquare", entry.Grid ?? "");
        Tag(sb, "exchange1", "");
        Tag(sb, "section", "");
        Tag(sb, "comment", entry.Comment ?? "");
        Tag(sb, "qth", "");
        Tag(sb, "name", entry.Name ?? "");
        Tag(sb, "power", "");
        Tag(sb, "misctext", "");
        Tag(sb, "zone", entry.CqZone?.ToString(CultureInfo.InvariantCulture) ?? "0");
        Tag(sb, "prec", "");
        Tag(sb, "ck", "0");
        Tag(sb, "ismultiplier1", "0");
        Tag(sb, "ismultiplier2", "0");
        Tag(sb, "ismultiplier3", "0");
        Tag(sb, "points", "1");
        Tag(sb, "radionr", "1");
        Tag(sb, "run1run2", "1");
        Tag(sb, "RoverLocation", "");
        Tag(sb, "RadioInterfaced", "0");
        Tag(sb, "NetworkedCompNr", "0");
        Tag(sb, "IsOriginal", "True");
        Tag(sb, "NetBiosName", "");
        Tag(sb, "IsRunQSO", "0");
        Tag(sb, "StationName", "Zeus");
        Tag(sb, "ID", Guid.NewGuid().ToString("N"));
        Tag(sb, "IsClaimedQso", "1");
        Tag(sb, "oldtimestamp", "");
        Tag(sb, "oldcall", "");
        Tag(sb, "SentExchange", "");
        sb.Append("</contactinfo>");
        sb.Append("\r\n");
        return sb.ToString();
    }

    private static void Tag(StringBuilder sb, string name, string value)
        => sb.Append('<').Append(name).Append('>')
             .Append(SecurityElement.Escape(value) ?? "")
             .Append("</").Append(name).Append('>');

    // N1MM <band> is the MHz band designator (e.g. "3.5" for 80m, "14" for 20m),
    // NOT the band in meters. The N1MM External UDP Broadcasts spec example
    // datagram shows <band>3.5</band> for 80m and states the value is "2 or 3
    // characters that may include localized delimiters"
    // (n1mmwp.hamdocs.com/appendices/external-udp-broadcasts). Consumers that key
    // on <band> (HRD QSO-Forwarding / DXKeeper via the N1MM Gateway) expect MHz.
    //
    // Map the meters label to its MHz designator; if the label is unrecognised,
    // fall back to deriving the band from the QSO frequency, and finally to the
    // raw (meters-stripped) value so nothing crashes on an exotic band.
    internal static string NormalizeBand(string? band, double? frequencyMhz = null)
    {
        if (MetersLabelToMhz(band) is { } fromLabel) return fromLabel;

        if (frequencyMhz is { } mhz && mhz > 0)
        {
            var meters = BandUtils.FreqToBand((long)Math.Round(mhz * 1_000_000.0));
            if (MetersLabelToMhz(meters) is { } fromFreq) return fromFreq;
        }

        // Unknown band: best-effort — strip a trailing 'm' and pass it through.
        if (string.IsNullOrWhiteSpace(band)) return "";
        var b = band.Trim();
        if (b.EndsWith("m", StringComparison.OrdinalIgnoreCase))
            b = b[..^1];
        return b.Trim();
    }

    // Meters label (with/without trailing 'm') -> N1MM MHz band designator.
    // Decimal designators only where the band starts on a fractional MHz
    // (160/80/60), integers elsewhere — matching N1MM's "2 or 3 character" rule.
    private static string? MetersLabelToMhz(string? band)
    {
        if (string.IsNullOrWhiteSpace(band)) return null;
        var b = band.Trim();
        if (b.EndsWith("m", StringComparison.OrdinalIgnoreCase))
            b = b[..^1];
        return b.Trim() switch
        {
            "160" => "1.8",
            "80" => "3.5",
            "60" => "5.3",
            "40" => "7",
            "30" => "10",
            "20" => "14",
            "17" => "18",
            "15" => "21",
            "12" => "24",
            "10" => "28",
            "6" => "50",
            "4" => "70",
            "2" => "144",
            _ => null,
        };
    }
}
