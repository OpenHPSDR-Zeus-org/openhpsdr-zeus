// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Buffers.Binary;
using System.Text;

namespace Zeus.Server.Wsjtx;

/// <summary>
/// Encoder for the WSJT-X UDP "NetworkMessage" wire format — the de-facto
/// protocol that JTAlert / Log4OM / GridTracker / N1MM / JTDX consume. The wire
/// format is a Qt QDataStream: byte order big-endian, all integers big-endian,
/// floating point ALWAYS serialised as 8-byte IEEE-754 doubles (Qt's default
/// QDataStream::DoublePrecision — WSJT-X never overrides it, so even fields
/// declared <c>float</c> in NetworkMessage.hpp go on the wire as 8 bytes), bool =
/// one byte, and each string is a UTF-8 QByteArray written as a 4-byte big-endian
/// length followed by the bytes (length 0xFFFFFFFF = null, 0 = empty).
///
/// Common header (every message): magic uint32 0xADBCCBDA, schema uint32,
/// message-type uint32, then the instance id (utf8). Each message type appends
/// its own fields IN THE EXACT ORDER WSJT-X declares them (see NetworkMessage.hpp)
/// — field order is load-bearing; a single wrong-width field shifts everything
/// after it.
///
/// SEND-ONLY. Zeus emits these; it never parses inbound WSJT-X messages (no
/// listener anywhere in this namespace — Reply(4)/HaltTx(8)/FreeText(9) are
/// network TX-triggers into a real PA and are intentionally unsupported).
///
/// Pure + allocation-light so it is trivially unit-testable; no I/O lives here
/// (see WsjtxUdpBroadcaster for the send).
/// </summary>
public static class WsjtxMessage
{
    /// <summary>WSJT-X NetworkMessage magic number.</summary>
    public const uint Magic = 0xADBCCBDA;

    // Message type ids (WSJT-X NetworkMessage::MessageType).
    public const uint HeartbeatType = 0;
    public const uint StatusType = 1;
    public const uint DecodeType = 2;
    public const uint QsoLoggedType = 5;
    public const uint LoggedAdifType = 12;
    public const uint WsprDecodeType = 10;

    /// <summary>
    /// Schema number written into the header. WSJT-X has used schema 2 across the
    /// long-lived 2.x series and downstream tools accept it broadly; bumping is a
    /// one-line change if a future client demands 3. G2 bench: confirm JTAlert /
    /// GridTracker ingest before relying on this.
    /// </summary>
    public const uint DefaultSchema = 2;

    // ---- message encoders ----------------------------------------------------

    /// <summary>Encode a LoggedADIF (type 12) datagram for the given ADIF text.</summary>
    public static byte[] EncodeLoggedAdif(string instanceId, string adif, uint schema = DefaultSchema)
    {
        using var ms = new MemoryStream(64 + (adif?.Length ?? 0));
        WriteHeader(ms, schema, LoggedAdifType, instanceId);
        WriteUtf8(ms, adif);
        return ms.ToArray();
    }

    /// <summary>Encode a Heartbeat (type 0). GridTracker uses this to discover the
    /// running instance; emit it ~every 15 s while live decodes are enabled.</summary>
    public static byte[] EncodeHeartbeat(
        string instanceId, uint maxSchema, string version, string revision, uint schema = DefaultSchema)
    {
        using var ms = new MemoryStream(64);
        WriteHeader(ms, schema, HeartbeatType, instanceId);
        WriteUInt32(ms, maxSchema);
        WriteUtf8(ms, version);
        WriteUtf8(ms, revision);
        return ms.ToArray();
    }

    /// <summary>Encode a Status (type 1) — the rig/keyer state GridTracker and
    /// JTAlert track (dial freq, mode, TX/RX, DE/DX). Field order matches
    /// NetworkMessage.hpp Status.</summary>
    public static byte[] EncodeStatus(
        string instanceId,
        ulong dialFrequencyHz,
        string mode,
        string dxCall,
        string report,
        string txMode,
        bool txEnabled,
        bool transmitting,
        bool decoding,
        uint rxDf,
        uint txDf,
        string deCall,
        string deGrid,
        string dxGrid,
        bool txWatchdog,
        string subMode,
        bool fastMode,
        byte specialOperationMode,
        uint frequencyTolerance,
        uint trPeriod,
        string configurationName,
        string txMessage,
        uint schema = DefaultSchema)
    {
        using var ms = new MemoryStream(192);
        WriteHeader(ms, schema, StatusType, instanceId);
        WriteUInt64(ms, dialFrequencyHz);
        WriteUtf8(ms, mode);
        WriteUtf8(ms, dxCall);
        WriteUtf8(ms, report);
        WriteUtf8(ms, txMode);
        WriteBool(ms, txEnabled);
        WriteBool(ms, transmitting);
        WriteBool(ms, decoding);
        WriteUInt32(ms, rxDf);
        WriteUInt32(ms, txDf);
        WriteUtf8(ms, deCall);
        WriteUtf8(ms, deGrid);
        WriteUtf8(ms, dxGrid);
        WriteBool(ms, txWatchdog);
        WriteUtf8(ms, subMode);
        WriteBool(ms, fastMode);
        WriteUInt8(ms, specialOperationMode);
        WriteUInt32(ms, frequencyTolerance);
        WriteUInt32(ms, trPeriod);
        WriteUtf8(ms, configurationName);
        WriteUtf8(ms, txMessage);
        return ms.ToArray();
    }

    /// <summary>Encode a Decode (type 2) — one decoded FT8/FT4 line. <paramref
    /// name="timeMsSinceMidnight"/> is a QTime (ms since UTC midnight), NOT a
    /// QDateTime. <paramref name="deltaTimeSec"/> goes on the wire as a double.</summary>
    public static byte[] EncodeDecode(
        string instanceId,
        bool isNew,
        uint timeMsSinceMidnight,
        int snr,
        double deltaTimeSec,
        uint deltaFrequencyHz,
        string mode,
        string message,
        bool lowConfidence,
        bool offAir,
        uint schema = DefaultSchema)
    {
        using var ms = new MemoryStream(96);
        WriteHeader(ms, schema, DecodeType, instanceId);
        WriteBool(ms, isNew);
        WriteUInt32(ms, timeMsSinceMidnight);
        WriteInt32(ms, snr);
        WriteDouble(ms, deltaTimeSec);
        WriteUInt32(ms, deltaFrequencyHz);
        WriteUtf8(ms, mode);
        WriteUtf8(ms, message);
        WriteBool(ms, lowConfidence);
        WriteBool(ms, offAir);
        return ms.ToArray();
    }

    /// <summary>Encode a WSPRDecode (type 10) — one WSPR spot. Frequency is a u64
    /// in Hz (the absolute spot frequency), matching NetworkMessage.hpp.</summary>
    public static byte[] EncodeWsprDecode(
        string instanceId,
        bool isNew,
        uint timeMsSinceMidnight,
        int snr,
        double deltaTimeSec,
        ulong frequencyHz,
        int drift,
        string callsign,
        string grid,
        int power,
        bool offAir,
        uint schema = DefaultSchema)
    {
        using var ms = new MemoryStream(96);
        WriteHeader(ms, schema, WsprDecodeType, instanceId);
        WriteBool(ms, isNew);
        WriteUInt32(ms, timeMsSinceMidnight);
        WriteInt32(ms, snr);
        WriteDouble(ms, deltaTimeSec);
        WriteUInt64(ms, frequencyHz);
        WriteInt32(ms, drift);
        WriteUtf8(ms, callsign);
        WriteUtf8(ms, grid);
        WriteInt32(ms, power);
        WriteBool(ms, offAir);
        return ms.ToArray();
    }

    /// <summary>Encode a QSOLogged (type 5) — the structured logged-QSO message
    /// some tools prefer over (or alongside) the LoggedADIF type 12. Field order
    /// matches NetworkMessage.hpp QSOLogged. The two QDateTimes are written UTC
    /// (timespec byte = 1).</summary>
    public static byte[] EncodeQsoLogged(
        string instanceId,
        DateTime dateTimeOffUtc,
        string dxCall,
        string dxGrid,
        ulong txFrequencyHz,
        string mode,
        string reportSent,
        string reportReceived,
        string txPower,
        string comments,
        string name,
        DateTime dateTimeOnUtc,
        string operatorCall,
        string myCall,
        string myGrid,
        string exchangeSent,
        string exchangeReceived,
        string adifPropagationMode,
        uint schema = DefaultSchema)
    {
        using var ms = new MemoryStream(256);
        WriteHeader(ms, schema, QsoLoggedType, instanceId);
        WriteQDateTimeUtc(ms, dateTimeOffUtc);
        WriteUtf8(ms, dxCall);
        WriteUtf8(ms, dxGrid);
        WriteUInt64(ms, txFrequencyHz);
        WriteUtf8(ms, mode);
        WriteUtf8(ms, reportSent);
        WriteUtf8(ms, reportReceived);
        WriteUtf8(ms, txPower);
        WriteUtf8(ms, comments);
        WriteUtf8(ms, name);
        WriteQDateTimeUtc(ms, dateTimeOnUtc);
        WriteUtf8(ms, operatorCall);
        WriteUtf8(ms, myCall);
        WriteUtf8(ms, myGrid);
        WriteUtf8(ms, exchangeSent);
        WriteUtf8(ms, exchangeReceived);
        WriteUtf8(ms, adifPropagationMode);
        return ms.ToArray();
    }

    // ---- QDataStream primitives ----------------------------------------------

    private static void WriteHeader(Stream s, uint schema, uint messageType, string instanceId)
    {
        WriteUInt32(s, Magic);
        WriteUInt32(s, schema);
        WriteUInt32(s, messageType);
        WriteUtf8(s, instanceId);
    }

    private static void WriteUInt8(Stream s, byte value) => s.WriteByte(value);

    private static void WriteBool(Stream s, bool value) => s.WriteByte(value ? (byte)1 : (byte)0);

    private static void WriteUInt32(Stream s, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        s.Write(b);
    }

    private static void WriteInt32(Stream s, int value) => WriteUInt32(s, unchecked((uint)value));

    private static void WriteUInt64(Stream s, ulong value)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(b, value);
        s.Write(b);
    }

    private static void WriteDouble(Stream s, double value)
    {
        // Qt QDataStream defaults to DoublePrecision: floating point is always 8
        // bytes, big-endian (round-tripped through the IEEE-754 bit pattern).
        WriteUInt64(s, unchecked((ulong)BitConverter.DoubleToInt64Bits(value)));
    }

    private static void WriteUtf8(Stream s, string? value)
    {
        if (value is null)
        {
            // Qt encodes a null QByteArray as length 0xFFFFFFFF.
            WriteUInt32(s, 0xFFFFFFFF);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        WriteUInt32(s, (uint)bytes.Length);
        s.Write(bytes);
    }

    /// <summary>
    /// Write a QDateTime in the Qt_5_2+ QDataStream layout (which schema 2 uses):
    /// QDate as a qint64 Julian Day Number, QTime as a quint32 ms-since-midnight,
    /// then a quint8 timespec. We always emit UTC (timespec = 1), so no trailing
    /// offset/zone bytes are written. The input is normalised to UTC first.
    /// </summary>
    private static void WriteQDateTimeUtc(Stream s, DateTime dt)
    {
        var utc = dt.Kind switch
        {
            DateTimeKind.Utc => dt,
            DateTimeKind.Local => dt.ToUniversalTime(),
            _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
        };

        long julianDay = ToJulianDay(utc.Year, utc.Month, utc.Day);
        uint msSinceMidnight = (uint)(((utc.Hour * 60 + utc.Minute) * 60 + utc.Second) * 1000 + utc.Millisecond);

        WriteInt64(s, julianDay);     // QDate
        WriteUInt32(s, msSinceMidnight); // QTime
        WriteUInt8(s, 1);             // Qt::TimeSpec::UTC
    }

    private static void WriteInt64(Stream s, long value) => WriteUInt64(s, unchecked((ulong)value));

    // Gregorian-calendar Julian Day Number, matching QDate::toJulianDay().
    // 2000-01-01 -> 2451545.
    internal static long ToJulianDay(int year, int month, int day)
    {
        long a = (14 - month) / 12;
        long y = year + 4800 - a;
        long m = month + 12 * a - 3;
        return day + (153 * m + 2) / 5 + 365 * y + y / 4 - y / 100 + y / 400 - 32045;
    }
}
