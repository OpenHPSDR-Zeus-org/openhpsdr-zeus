// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Buffers.Binary;
using System.Text;
using Zeus.Server.Wsjtx;

namespace Zeus.Server.Tests;

public sealed class WsjtxMessageTests
{
    // Cursor over a WSJT-X NetworkMessage (Qt QDataStream: big-endian ints,
    // length-prefixed utf8 strings).
    private sealed class Reader(byte[] buf)
    {
        private int _pos;

        public uint UInt32()
        {
            var v = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(_pos, 4));
            _pos += 4;
            return v;
        }

        public int Int32() => unchecked((int)UInt32());

        public ulong UInt64()
        {
            var v = BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(_pos, 8));
            _pos += 8;
            return v;
        }

        public long Int64() => unchecked((long)UInt64());

        public double Double() => BitConverter.Int64BitsToDouble(Int64());

        public bool Bool()
        {
            var b = buf[_pos];
            _pos += 1;
            return b != 0;
        }

        public byte UInt8()
        {
            var b = buf[_pos];
            _pos += 1;
            return b;
        }

        public (long JulianDay, uint MsSinceMidnight, byte TimeSpec) QDateTime()
        {
            var jd = Int64();
            var ms = UInt32();
            var spec = UInt8();
            return (jd, ms, spec);
        }

        public string? Utf8()
        {
            var len = UInt32();
            if (len == 0xFFFFFFFF) return null;
            var s = Encoding.UTF8.GetString(buf, _pos, (int)len);
            _pos += (int)len;
            return s;
        }

        public int Remaining => buf.Length - _pos;
    }

    // Read past the common header (magic, schema, type, instance id) and assert it.
    private static Reader AfterHeader(byte[] bytes, uint expectedType, string expectedId)
    {
        var r = new Reader(bytes);
        Assert.Equal(0xADBCCBDAu, r.UInt32());
        Assert.Equal(WsjtxMessage.DefaultSchema, r.UInt32());
        Assert.Equal(expectedType, r.UInt32());
        Assert.Equal(expectedId, r.Utf8());
        return r;
    }

    [Fact]
    public void EncodeLoggedAdif_WritesHeaderThenIdThenAdif()
    {
        var bytes = WsjtxMessage.EncodeLoggedAdif("WSJT-X", "<call:5>K1ABC<eor>");

        var r = new Reader(bytes);
        Assert.Equal(0xADBCCBDAu, r.UInt32());                 // magic
        Assert.Equal(WsjtxMessage.DefaultSchema, r.UInt32());  // schema (2)
        Assert.Equal(WsjtxMessage.LoggedAdifType, r.UInt32()); // type 12
        Assert.Equal("WSJT-X", r.Utf8());                      // instance id
        Assert.Equal("<call:5>K1ABC<eor>", r.Utf8());          // adif payload
        Assert.Equal(0, r.Remaining);                          // nothing trailing
    }

    [Fact]
    public void EncodeLoggedAdif_FirstFourBytesAreTheMagic()
    {
        var bytes = WsjtxMessage.EncodeLoggedAdif("WSJT-X", "x");
        Assert.Equal(new byte[] { 0xAD, 0xBC, 0xCB, 0xDA }, bytes[..4]);
    }

    [Fact]
    public void EncodeLoggedAdif_RoundTripsUtf8AndEmptyString()
    {
        // Non-ASCII instance id + empty ADIF exercises utf8 length + zero-length.
        var bytes = WsjtxMessage.EncodeLoggedAdif("Zëus", string.Empty);

        var r = new Reader(bytes);
        r.UInt32();
        r.UInt32();
        r.UInt32();
        Assert.Equal("Zëus", r.Utf8());
        Assert.Equal(string.Empty, r.Utf8());
    }

    [Fact]
    public void EncodeLoggedAdif_HonoursSchemaOverride()
    {
        var bytes = WsjtxMessage.EncodeLoggedAdif("WSJT-X", "x", schema: 3);
        var r = new Reader(bytes);
        r.UInt32();
        Assert.Equal(3u, r.UInt32());
    }

    [Fact]
    public void EncodeHeartbeat_WritesFieldsInOrder()
    {
        var bytes = WsjtxMessage.EncodeHeartbeat("Zeus", maxSchema: 3, version: "1.2.3", revision: "");
        var r = AfterHeader(bytes, WsjtxMessage.HeartbeatType, "Zeus");
        Assert.Equal(3u, r.UInt32());      // max schema
        Assert.Equal("1.2.3", r.Utf8());   // version
        Assert.Equal("", r.Utf8());        // revision (empty, not null)
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void EncodeStatus_WritesEveryFieldInDeclaredOrder()
    {
        var bytes = WsjtxMessage.EncodeStatus(
            instanceId: "Zeus",
            dialFrequencyHz: 14_074_000,
            mode: "FT8",
            dxCall: "K1ABC",
            report: "-12",
            txMode: "FT8",
            txEnabled: true,
            transmitting: false,
            decoding: true,
            rxDf: 1500,
            txDf: 1234,
            deCall: "KB2UKA",
            deGrid: "FN12",
            dxGrid: "FN42",
            txWatchdog: false,
            subMode: "",
            fastMode: false,
            specialOperationMode: 0,
            frequencyTolerance: 0,
            trPeriod: 15000,
            configurationName: "Zeus",
            txMessage: "K1ABC KB2UKA FN12");

        var r = AfterHeader(bytes, WsjtxMessage.StatusType, "Zeus");
        Assert.Equal(14_074_000UL, r.UInt64());
        Assert.Equal("FT8", r.Utf8());
        Assert.Equal("K1ABC", r.Utf8());
        Assert.Equal("-12", r.Utf8());
        Assert.Equal("FT8", r.Utf8());
        Assert.True(r.Bool());   // txEnabled
        Assert.False(r.Bool());  // transmitting
        Assert.True(r.Bool());   // decoding
        Assert.Equal(1500u, r.UInt32());
        Assert.Equal(1234u, r.UInt32());
        Assert.Equal("KB2UKA", r.Utf8());
        Assert.Equal("FN12", r.Utf8());
        Assert.Equal("FN42", r.Utf8());
        Assert.False(r.Bool());  // txWatchdog
        Assert.Equal("", r.Utf8());
        Assert.False(r.Bool());  // fastMode
        Assert.Equal(0, r.UInt8());
        Assert.Equal(0u, r.UInt32());
        Assert.Equal(15000u, r.UInt32());
        Assert.Equal("Zeus", r.Utf8());
        Assert.Equal("K1ABC KB2UKA FN12", r.Utf8());
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void EncodeDecode_WritesDeltaTimeAsDouble_AndTimeAsMsU32()
    {
        var bytes = WsjtxMessage.EncodeDecode(
            "Zeus", isNew: true, timeMsSinceMidnight: 45_296_789, snr: -7,
            deltaTimeSec: 0.2, deltaFrequencyHz: 1500, mode: "FT4",
            message: "CQ K1ABC FN42", lowConfidence: false, offAir: false);

        var r = AfterHeader(bytes, WsjtxMessage.DecodeType, "Zeus");
        Assert.True(r.Bool());                       // new
        Assert.Equal(45_296_789u, r.UInt32());       // time (ms since midnight)
        Assert.Equal(-7, r.Int32());                 // snr
        Assert.Equal(0.2, r.Double(), 9);            // dt — 8-byte double
        Assert.Equal(1500u, r.UInt32());             // df
        Assert.Equal("FT4", r.Utf8());
        Assert.Equal("CQ K1ABC FN42", r.Utf8());
        Assert.False(r.Bool());                      // low confidence
        Assert.False(r.Bool());                      // off air
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void EncodeWsprDecode_WritesFrequencyAsU64Hz()
    {
        var bytes = WsjtxMessage.EncodeWsprDecode(
            "Zeus", isNew: true, timeMsSinceMidnight: 60_000, snr: -24,
            deltaTimeSec: 1.5, frequencyHz: 14_097_100, drift: -1,
            callsign: "KB2UKA", grid: "FN12", power: 30, offAir: false);

        var r = AfterHeader(bytes, WsjtxMessage.WsprDecodeType, "Zeus");
        Assert.True(r.Bool());
        Assert.Equal(60_000u, r.UInt32());
        Assert.Equal(-24, r.Int32());
        Assert.Equal(1.5, r.Double(), 9);
        Assert.Equal(14_097_100UL, r.UInt64());
        Assert.Equal(-1, r.Int32());
        Assert.Equal("KB2UKA", r.Utf8());
        Assert.Equal("FN12", r.Utf8());
        Assert.Equal(30, r.Int32());
        Assert.False(r.Bool());
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void EncodeQsoLogged_WritesQDateTimeAsJulianMsTimespec()
    {
        var off = new DateTime(2024, 6, 15, 12, 34, 56, 789, DateTimeKind.Utc);
        var on = new DateTime(2024, 6, 15, 12, 33, 0, 0, DateTimeKind.Utc);
        var bytes = WsjtxMessage.EncodeQsoLogged(
            instanceId: "Zeus",
            dateTimeOffUtc: off,
            dxCall: "K1ABC",
            dxGrid: "FN42",
            txFrequencyHz: 14_074_000,
            mode: "FT8",
            reportSent: "-12",
            reportReceived: "-07",
            txPower: "",
            comments: "",
            name: "",
            dateTimeOnUtc: on,
            operatorCall: "KB2UKA",
            myCall: "KB2UKA",
            myGrid: "FN12",
            exchangeSent: "",
            exchangeReceived: "",
            adifPropagationMode: "");

        var r = AfterHeader(bytes, WsjtxMessage.QsoLoggedType, "Zeus");

        var (jd, ms, spec) = r.QDateTime();
        Assert.Equal(WsjtxMessage.ToJulianDay(2024, 6, 15), jd);
        Assert.Equal(45_296_789u, ms);  // 12:34:56.789
        Assert.Equal(1, spec);          // UTC
        Assert.Equal("K1ABC", r.Utf8());
        Assert.Equal("FN42", r.Utf8());
        Assert.Equal(14_074_000UL, r.UInt64());
        Assert.Equal("FT8", r.Utf8());
        Assert.Equal("-12", r.Utf8());
        Assert.Equal("-07", r.Utf8());
        Assert.Equal("", r.Utf8());     // tx power
        Assert.Equal("", r.Utf8());     // comments
        Assert.Equal("", r.Utf8());     // name

        var (jd2, ms2, spec2) = r.QDateTime();
        Assert.Equal(WsjtxMessage.ToJulianDay(2024, 6, 15), jd2);
        Assert.Equal((uint)((12 * 60 + 33) * 60) * 1000u, ms2);
        Assert.Equal(1, spec2);
        Assert.Equal("KB2UKA", r.Utf8());  // operator
        Assert.Equal("KB2UKA", r.Utf8());  // my call
        Assert.Equal("FN12", r.Utf8());    // my grid
        Assert.Equal("", r.Utf8());        // exchange sent
        Assert.Equal("", r.Utf8());        // exchange recv
        Assert.Equal("", r.Utf8());        // adif prop mode
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void ToJulianDay_MatchesQtAnchor()
    {
        // QDate(2000,1,1).toJulianDay() == 2451545.
        Assert.Equal(2451545L, WsjtxMessage.ToJulianDay(2000, 1, 1));
    }
}
