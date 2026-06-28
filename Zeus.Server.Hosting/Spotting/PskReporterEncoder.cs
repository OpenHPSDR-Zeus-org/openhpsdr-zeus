// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Text;

namespace Zeus.Server.Spotting;

/// <summary>
/// Pure, allocation-bounded encoder for the PSK Reporter IPFIX (NetFlow v10)
/// datagram. No I/O — it turns a receiver record + N sender records into the byte
/// buffer that <see cref="PskReporterReporter"/> sends to
/// report.pskreporter.info:4739. Kept separate and unit-tested because the
/// byte layout is the load-bearing, error-prone part.
///
/// Wire format (big-endian throughout):
///   16-byte message header
///   rxInfo options-template descriptor (WSJT-X-compatible rxInfo block)
///   txInfo template descriptor          (Zeus's own WSJT-X-compatible template)
///   rxInfo data set   (one receiver record)
///   txInfo data set   (N sender records packed back-to-back)
///
/// These are Zeus's own templates built from the enterprise-30351 element
/// registry, not byte-for-byte copies of a WSJT-X capture: the rxInfo template is
/// a 3-field subset (no antennaInformation 0x8009) and the txInfo template is a
/// 6-field variant. IPFIX is template-driven, so any self-consistent template
/// whose data records follow the declared field order is wire-valid; the exact
/// element IDs are listed per field below so a reader can verify them against the
/// registry without trusting this prose. Live PSK Reporter acceptance is a
/// separate bench-validation step.
///
/// Including both descriptors in every datagram (rather than tracking template
/// TTL) is simpler and immune to PSK Reporter's template-cache expiry; the cost
/// is ~90 fixed bytes per datagram. Field IDs use enterprise number 30351
/// (0x0000768F); the enterprise bit (0x8000) is set on those element IDs.
/// flowStartSeconds is the IANA standard element 150 (0x0096), no enterprise.
/// </summary>
public static class PskReporterEncoder
{
    /// <summary>IPFIX version (NetFlow v10).</summary>
    public const ushort Version = 0x000A;

    /// <summary>PSK Reporter ingest host.</summary>
    public const string Host = "report.pskreporter.info";

    /// <summary>PSK Reporter ingest UDP port.</summary>
    public const int Port = 4739;

    // rxInfo options-template descriptor. Set ID 3, template ID 0x9992, three
    // varlen fields: receiverCallsign (0x8002), receiverLocator (0x8004),
    // decodingSoftware (0x8008), all enterprise 30351. Trailing 0x0000 pads the
    // set to a 4-byte boundary. This is the WSJT-X-compatible rxInfo block (a
    // 3-field subset — WSJT-X also carries antennaInformation 0x8009, which PSK
    // Reporter treats as optional).
    public static readonly byte[] RxDescriptor =
    {
        0x00, 0x03, 0x00, 0x24, 0x99, 0x92, 0x00, 0x03, 0x00, 0x00,
        0x80, 0x02, 0xFF, 0xFF, 0x00, 0x00, 0x76, 0x8F,
        0x80, 0x04, 0xFF, 0xFF, 0x00, 0x00, 0x76, 0x8F,
        0x80, 0x08, 0xFF, 0xFF, 0x00, 0x00, 0x76, 0x8F,
        0x00, 0x00,
    };

    // txInfo template descriptor. Set ID 2, template ID 0x9993, six fields in
    // this exact order: senderCallsign (0x8001, varlen), senderLocator (0x8003,
    // varlen), frequency (0x8005, 4 B), sNR (0x8006, 1 B), mode (0x800A, varlen)
    // — all enterprise 30351 — then flowStartSeconds (0x0096, 4 B, IANA element
    // 150, no enterprise number). The data records below MUST follow this order.
    // Element IDs are the enterprise-30351 registry pairing where callsign IDs are
    // sender/receiver (0x8001/0x8002) and locator IDs are sender/receiver
    // (0x8003/0x8004) — so senderLocator is 0x8003 and frequency is 0x8005, the
    // same layout WSJT-X uses.
    public static readonly byte[] TxDescriptor =
    {
        0x00, 0x02, 0x00, 0x34, 0x99, 0x93, 0x00, 0x06,
        0x80, 0x01, 0xFF, 0xFF, 0x00, 0x00, 0x76, 0x8F,
        0x80, 0x03, 0xFF, 0xFF, 0x00, 0x00, 0x76, 0x8F,
        0x80, 0x05, 0x00, 0x04, 0x00, 0x00, 0x76, 0x8F,
        0x80, 0x06, 0x00, 0x01, 0x00, 0x00, 0x76, 0x8F,
        0x80, 0x0A, 0xFF, 0xFF, 0x00, 0x00, 0x76, 0x8F,
        0x00, 0x96, 0x00, 0x04,
    };

    private const ushort RxSetId = 0x9992;
    private const ushort TxSetId = 0x9993;

    /// <summary>One heard station to report.</summary>
    public readonly record struct SenderRecord(
        string Callsign,
        string? Grid,
        uint FrequencyHz,
        sbyte SnrDb,
        string Mode,
        uint FlowStartSeconds);

    /// <summary>Receiving station identity for the rxInfo record.</summary>
    public readonly record struct ReceiverInfo(
        string Callsign,
        string Locator,
        string DecodingSoftware);

    /// <summary>
    /// Encodes one IPFIX datagram. <paramref name="senders"/> must be non-empty.
    /// </summary>
    public static byte[] Encode(
        ReceiverInfo rx,
        IReadOnlyList<SenderRecord> senders,
        uint exportTimeSeconds,
        uint sequenceNumber,
        uint observationDomainId)
    {
        var buf = new List<byte>(256);

        // ---- 16-byte message header (length backpatched at the end) ----
        WriteU16(buf, Version);
        int lengthAt = buf.Count;
        WriteU16(buf, 0); // total length placeholder
        WriteU32(buf, exportTimeSeconds);
        WriteU32(buf, sequenceNumber);
        WriteU32(buf, observationDomainId);

        // ---- template descriptors (both included in every datagram) ----
        buf.AddRange(RxDescriptor);
        buf.AddRange(TxDescriptor);

        // ---- rxInfo data set ----
        WriteSet(buf, RxSetId, body =>
        {
            WriteVarString(body, rx.Callsign);
            WriteVarString(body, rx.Locator);
            WriteVarString(body, rx.DecodingSoftware);
        });

        // ---- txInfo data set (all sender records, field order = template) ----
        WriteSet(buf, TxSetId, body =>
        {
            foreach (var s in senders)
            {
                WriteVarString(body, s.Callsign);
                WriteVarString(body, s.Grid ?? "");
                WriteU32(body, s.FrequencyHz);
                body.Add(unchecked((byte)s.SnrDb));
                WriteVarString(body, s.Mode);
                WriteU32(body, s.FlowStartSeconds);
            }
        });

        // Backpatch total length.
        Patch16(buf, lengthAt, (ushort)buf.Count);
        return buf.ToArray();
    }

    // Writes a data set: set ID, length (backpatched), the body via <paramref
    // name="writeBody"/>, then zero-padding to a 4-byte boundary (IPFIX requires
    // every set length to be a multiple of 4). The pad is included in the length.
    private static void WriteSet(List<byte> buf, ushort setId, Action<List<byte>> writeBody)
    {
        int setStart = buf.Count;
        WriteU16(buf, setId);
        int lenAt = buf.Count;
        WriteU16(buf, 0); // set length placeholder
        writeBody(buf);

        int setLen = buf.Count - setStart;
        int pad = (4 - (setLen % 4)) % 4;
        for (int i = 0; i < pad; i++) buf.Add(0);

        Patch16(buf, lenAt, (ushort)(buf.Count - setStart));
    }

    // IPFIX variable-length string: 1 length byte + UTF-8 bytes (the short form,
    // valid for < 255 bytes — callsigns/grids/mode are always far shorter). An
    // empty/absent value encodes as a single 0x00 length byte (e.g. no grid).
    private static void WriteVarString(List<byte> buf, string value)
    {
        var bytes = string.IsNullOrEmpty(value) ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(value);
        if (bytes.Length > 254)
            bytes = bytes[..254]; // defensive — never reached for legit fields
        buf.Add((byte)bytes.Length);
        buf.AddRange(bytes);
    }

    private static void WriteU16(List<byte> buf, ushort v)
    {
        buf.Add((byte)(v >> 8));
        buf.Add((byte)(v & 0xFF));
    }

    private static void WriteU32(List<byte> buf, uint v)
    {
        buf.Add((byte)(v >> 24));
        buf.Add((byte)(v >> 16));
        buf.Add((byte)(v >> 8));
        buf.Add((byte)(v & 0xFF));
    }

    private static void Patch16(List<byte> buf, int at, ushort v)
    {
        buf[at] = (byte)(v >> 8);
        buf[at + 1] = (byte)(v & 0xFF);
    }
}
