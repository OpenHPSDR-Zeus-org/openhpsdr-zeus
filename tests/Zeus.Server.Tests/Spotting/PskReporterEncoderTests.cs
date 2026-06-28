// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Byte-layout pins for the PSK Reporter IPFIX encoder — the load-bearing,
// error-prone part of spotting. These prove OUR intended bytes (the WSJT-X-
// compatible field IDs, varlen lengths, IANA-vs-enterprise distinction, 4-byte
// set padding, and the backpatched total length); live PSK Reporter acceptance
// is a separate wire-validation step (bench-gated).

using Zeus.Server.Spotting;

namespace Zeus.Server.Tests.Spotting;

public sealed class PskReporterEncoderTests
{
    private static ushort ReadU16(byte[] b, int at) => (ushort)((b[at] << 8) | b[at + 1]);
    private static uint ReadU32(byte[] b, int at) =>
        (uint)((b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3]);

    [Fact]
    public void Descriptors_Have_Expected_Field_Layout()
    {
        // rxInfo options template: set ID 3, template 0x9992, length 36.
        Assert.Equal(0x0003, ReadU16(PskReporterEncoder.RxDescriptor, 0));
        Assert.Equal(36, ReadU16(PskReporterEncoder.RxDescriptor, 2));
        Assert.Equal(36, PskReporterEncoder.RxDescriptor.Length);
        Assert.Equal(0x9992, ReadU16(PskReporterEncoder.RxDescriptor, 4));

        // txInfo template: set ID 2, template 0x9993, length 52, 6 fields.
        Assert.Equal(0x0002, ReadU16(PskReporterEncoder.TxDescriptor, 0));
        Assert.Equal(52, ReadU16(PskReporterEncoder.TxDescriptor, 2));
        Assert.Equal(52, PskReporterEncoder.TxDescriptor.Length);
        Assert.Equal(0x9993, ReadU16(PskReporterEncoder.TxDescriptor, 4));
        Assert.Equal(6, ReadU16(PskReporterEncoder.TxDescriptor, 6));

        // Field 2 (offset 8 + 8 = 16) is senderLocator: element 0x8003, varlen.
        // Field 3 (offset 8 + 2*8 = 24) is frequency: element 0x8005, fixed 4-byte,
        // enterprise 30351. The sender/receiver pairing (callsign 0x8001/0x8002,
        // locator 0x8003/0x8004) forces this ordering — the data write order in
        // Encode() (Callsign, Grid, FrequencyHz, ...) MUST match it. Swapping these
        // two IDs ships every spot's grid into the frequency element and vice
        // versa, so these assertions lock the IDs in place.
        Assert.Equal(0x8003, ReadU16(PskReporterEncoder.TxDescriptor, 16)); // senderLocator
        Assert.Equal(0xFFFF, ReadU16(PskReporterEncoder.TxDescriptor, 18)); // varlen
        Assert.Equal(0x8005, ReadU16(PskReporterEncoder.TxDescriptor, 24)); // frequency (enterprise bit set)
        Assert.Equal(0x0004, ReadU16(PskReporterEncoder.TxDescriptor, 26)); // 4-byte length
        Assert.Equal(30351u, ReadU32(PskReporterEncoder.TxDescriptor, 28)); // enterprise number
        // flowStartSeconds is the last field: element 0x0096 (150), length 4, no
        // enterprise number follows.
        Assert.Equal(0x0096, ReadU16(PskReporterEncoder.TxDescriptor, 48));
        Assert.Equal(0x0004, ReadU16(PskReporterEncoder.TxDescriptor, 50));
    }

    [Fact]
    public void Single_Decode_Encodes_Expected_Layout()
    {
        var rx = new PskReporterEncoder.ReceiverInfo("K1ABC", "FN42", "Zeus");
        var sender = new PskReporterEncoder.SenderRecord(
            Callsign: "W1AW",
            Grid: "FN31",
            FrequencyHz: 14074500,
            SnrDb: -10,
            Mode: "FT8",
            FlowStartSeconds: 1700000000);

        var dg = PskReporterEncoder.Encode(
            rx, new[] { sender },
            exportTimeSeconds: 1700000123,
            sequenceNumber: 7,
            observationDomainId: 0x12345678);

        // ---- header ----
        Assert.Equal(0x000A, ReadU16(dg, 0));            // version
        Assert.Equal(dg.Length, ReadU16(dg, 2));         // backpatched total length
        Assert.Equal(1700000123u, ReadU32(dg, 4));       // exportTime
        Assert.Equal(7u, ReadU32(dg, 8));                // sequence number
        Assert.Equal(0x12345678u, ReadU32(dg, 12));      // observation domain id

        // ---- descriptors follow verbatim ----
        int pos = 16;
        Assert.Equal(PskReporterEncoder.RxDescriptor,
            dg[pos..(pos + PskReporterEncoder.RxDescriptor.Length)]);
        pos += PskReporterEncoder.RxDescriptor.Length;
        Assert.Equal(PskReporterEncoder.TxDescriptor,
            dg[pos..(pos + PskReporterEncoder.TxDescriptor.Length)]);
        pos += PskReporterEncoder.TxDescriptor.Length;

        // ---- rxInfo data set ----
        Assert.Equal(0x9992, ReadU16(dg, pos));          // rx set ID
        // body: "K1ABC"(1+5) + "FN42"(1+4) + "Zeus"(1+4) = 16; set = 4 + 16 = 20,
        // already a multiple of 4 (no padding).
        Assert.Equal(20, ReadU16(dg, pos + 2));
        int rb = pos + 4;
        Assert.Equal(5, dg[rb]);                         // varlen length byte
        Assert.Equal("K1ABC", System.Text.Encoding.UTF8.GetString(dg, rb + 1, 5));
        rb += 6;
        Assert.Equal(4, dg[rb]);
        Assert.Equal("FN42", System.Text.Encoding.UTF8.GetString(dg, rb + 1, 4));
        rb += 5;
        Assert.Equal(4, dg[rb]);
        Assert.Equal("Zeus", System.Text.Encoding.UTF8.GetString(dg, rb + 1, 4));
        pos += 20;

        // ---- txInfo data set ----
        Assert.Equal(0x9993, ReadU16(dg, pos));          // tx set ID
        // record: "W1AW"(1+4) + "FN31"(1+4) + freq(4) + snr(1) + "FT8"(1+3) +
        // flow(4) = 23; set = 4 + 23 = 27, padded to 28 (one 0x00 byte).
        Assert.Equal(28, ReadU16(dg, pos + 2));
        int tb = pos + 4;
        Assert.Equal(4, dg[tb]);
        Assert.Equal("W1AW", System.Text.Encoding.UTF8.GetString(dg, tb + 1, 4));
        tb += 5;
        Assert.Equal(4, dg[tb]);
        Assert.Equal("FN31", System.Text.Encoding.UTF8.GetString(dg, tb + 1, 4));
        tb += 5;
        Assert.Equal(14074500u, ReadU32(dg, tb));        // frequency
        tb += 4;
        Assert.Equal(0xF6, dg[tb]);                      // sNR -10 as int8
        tb += 1;
        Assert.Equal(3, dg[tb]);
        Assert.Equal("FT8", System.Text.Encoding.UTF8.GetString(dg, tb + 1, 3));
        tb += 4;
        Assert.Equal(1700000000u, ReadU32(dg, tb));      // flowStartSeconds
        tb += 4;
        Assert.Equal(0, dg[tb]);                         // 4-byte pad

        // Total length is internally consistent.
        Assert.Equal(pos + 28, dg.Length);
    }

    [Fact]
    public void Empty_Grid_Encodes_As_Zero_Length_Varstring()
    {
        var rx = new PskReporterEncoder.ReceiverInfo("K1ABC", "FN42", "Zeus");
        var sender = new PskReporterEncoder.SenderRecord(
            "W1AW", null, 14074500, 0, "FT4", 1700000000);

        var dg = PskReporterEncoder.Encode(rx, new[] { sender }, 1, 1, 1);

        int pos = 16 + PskReporterEncoder.RxDescriptor.Length + PskReporterEncoder.TxDescriptor.Length;
        pos += 20; // rxInfo set (same fixed size as above)
        int tb = pos + 4;
        Assert.Equal(4, dg[tb]);                 // "W1AW"
        tb += 5;
        Assert.Equal(0, dg[tb]);                 // empty grid -> single 0x00 length byte
    }

    [Fact]
    public void Snr_Boundaries_Encode_As_Signed_Bytes()
    {
        var rx = new PskReporterEncoder.ReceiverInfo("K1ABC", "FN42", "Zeus");
        var lo = PskReporterEncoder.Encode(rx,
            new[] { new PskReporterEncoder.SenderRecord("W1AW", "FN31", 14074000, sbyte.MinValue, "FT8", 1) }, 1, 1, 1);
        var hi = PskReporterEncoder.Encode(rx,
            new[] { new PskReporterEncoder.SenderRecord("W1AW", "FN31", 14074000, sbyte.MaxValue, "FT8", 1) }, 1, 1, 1);

        // sNR offset within the tx record: setStart + 4 (set header) + "W1AW"(1+4)
        // + "FN31"(1+4) + freq(4).
        int snrAt = 16 + PskReporterEncoder.RxDescriptor.Length + PskReporterEncoder.TxDescriptor.Length
                    + 20 /* rx set */ + 4 + 5 + 5 + 4;
        Assert.Equal(0x80, lo[snrAt]); // -128
        Assert.Equal(0x7F, hi[snrAt]); // +127
    }

    [Fact]
    public void Multiple_Senders_Pack_Into_One_Tx_Set()
    {
        var rx = new PskReporterEncoder.ReceiverInfo("K1ABC", "FN42", "Zeus");
        var senders = new[]
        {
            new PskReporterEncoder.SenderRecord("W1AW", "FN31", 14074000, -1, "FT8", 1700000000),
            new PskReporterEncoder.SenderRecord("G0XYZ", "IO91", 14074200, -5, "FT8", 1700000000),
        };
        var dg = PskReporterEncoder.Encode(rx, senders, 1, 1, 1);

        int pos = 16 + PskReporterEncoder.RxDescriptor.Length + PskReporterEncoder.TxDescriptor.Length + 20;
        Assert.Equal(0x9993, ReadU16(dg, pos)); // one tx set holds both records
        ushort setLen = ReadU16(dg, pos + 2);
        Assert.Equal(dg.Length, pos + setLen);  // tx set runs to end of datagram
    }
}
