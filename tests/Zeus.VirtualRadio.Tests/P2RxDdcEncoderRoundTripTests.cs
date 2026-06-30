// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Zeus.Protocol2;          // internal DecodePsPairForTest, via InternalsVisibleTo
using Zeus.VirtualRadio.P2;

namespace Zeus.VirtualRadio.Tests;

/// <summary>
/// Anti-drift round-trip tests for <see cref="P2RxDdcEncoder"/>. The plain-RX
/// path is decoded exactly as <c>Protocol2Client.HandleDdcPacket</c> does
/// (int24-BE I/Q from byte 16, 1/2^23 scale); the PureSignal feedback path is
/// fed pair-by-pair through the REAL
/// <see cref="Protocol2Client.DecodePsPairForTest"/> — the canonical
/// coupler→rx / reference→tx de-interleave the live client uses — so a swap or
/// offset regression reds here. Socketless.
/// </summary>
public class P2RxDdcEncoderRoundTripTests
{
    private const double Int24Lsb = 1.0 / 8_388_608.0;
    private const double Tol = 2 * Int24Lsb;

    private static double[] MakeIq(int complex, double iScale, double qScale)
    {
        var iq = new double[2 * complex];
        for (int n = 0; n < complex; n++)
        {
            iq[2 * n] = iScale * Math.Sin(2 * Math.PI * n / 19.0);
            iq[2 * n + 1] = qScale * Math.Cos(2 * Math.PI * n / 29.0);
        }
        return iq;
    }

    [Fact]
    public void EncodeRxIq_SamplesSurvive_AsInt24BigEndian()
    {
        var enc = new P2RxDdcEncoder();
        var iq = MakeIq(P2RxDdcEncoder.RxSamplesPerPacket, 0.7, 0.5);

        var packet = new byte[P2Wire.BufLen];
        enc.EncodeRxIq(packet, sequence: 0xCAFEBABE, iq);

        // Sequence at [0..3] BE.
        Assert.Equal(0xCAu, packet[0]);
        Assert.Equal(0xFEu, packet[1]);

        // Decode exactly as Protocol2Client.HandleDdcPacket: 238 samples, int24
        // BE I/Q from byte 16, scaled by 1/2^23.
        const double scale = 1.0 / 8_388_608.0;
        for (int s = 0; s < P2RxDdcEncoder.RxSamplesPerPacket; s++)
        {
            int off = 16 + s * 6;
            int iRaw = SignExtend24((packet[off] << 16) | (packet[off + 1] << 8) | packet[off + 2]);
            int qRaw = SignExtend24((packet[off + 3] << 16) | (packet[off + 4] << 8) | packet[off + 5]);
            double iVal = iRaw * scale;
            double qVal = qRaw * scale;
            Assert.True(Math.Abs(iVal - iq[2 * s]) <= Tol, $"I[{s}] {iVal} != {iq[2 * s]}");
            Assert.True(Math.Abs(qVal - iq[2 * s + 1]) <= Tol, $"Q[{s}] {qVal} != {iq[2 * s + 1]}");
        }
    }

    [Fact]
    public void EncodePsFeedback_RoundTripsThroughDecodePsPairForTest_CouplerIsRx_ReferenceIsTx()
    {
        var enc = new P2RxDdcEncoder();
        // Distinct coupler / reference so a swap is unmistakable.
        var coupler = MakeIq(P2RxDdcEncoder.PsPairsPerPacket, 0.40, 0.30);  // → rx
        var reference = MakeIq(P2RxDdcEncoder.PsPairsPerPacket, 0.80, 0.60); // → tx

        var packet = new byte[P2Wire.BufLen];
        enc.EncodePsFeedback(packet, sequence: 7, coupler, reference);

        // samplesPerFrame marker = 238 (the client reads this at [14..15] →
        // 119 pairs).
        int spf = (packet[14] << 8) | packet[15];
        Assert.Equal(238, spf);

        for (int i = 0; i < P2RxDdcEncoder.PsPairsPerPacket; i++)
        {
            int off = 16 + i * 12;
            var (rxI, rxQ, txI, txQ) =
                Protocol2Client.DecodePsPairForTest(new ReadOnlySpan<byte>(packet, off, 12));

            Assert.True(Math.Abs(rxI - coupler[2 * i]) <= Tol, $"rxI[{i}]");
            Assert.True(Math.Abs(rxQ - coupler[2 * i + 1]) <= Tol, $"rxQ[{i}]");
            Assert.True(Math.Abs(txI - reference[2 * i]) <= Tol, $"txI[{i}]");
            Assert.True(Math.Abs(txQ - reference[2 * i + 1]) <= Tol, $"txQ[{i}]");
        }
    }

    [Theory]
    [InlineData(2.5)]   // over-range → clamp to +full-scale
    [InlineData(-2.5)]  // over-range → clamp to -full-scale
    public void EncodeRxIq_ClampsFullScale(double level)
    {
        var enc = new P2RxDdcEncoder();
        var iq = new double[2 * P2RxDdcEncoder.RxSamplesPerPacket];
        for (int k = 0; k < iq.Length; k++) iq[k] = level;

        var packet = new byte[P2Wire.BufLen];
        enc.EncodeRxIq(packet, 1, iq);

        const double scale = 1.0 / 8_388_608.0;
        int off = 16;
        int raw = SignExtend24((packet[off] << 16) | (packet[off + 1] << 8) | packet[off + 2]);
        double v = raw * scale;
        double expected = level > 0 ? 1.0 : -1.0;
        Assert.True(Math.Abs(v - expected) <= Tol, $"value {v} not clamped near {expected}");
    }

    [Fact]
    public void EncodeRxIq_RejectsWrongPacketLength()
    {
        var enc = new P2RxDdcEncoder();
        var iq = MakeIq(P2RxDdcEncoder.RxSamplesPerPacket, 0.5, 0.5);
        Assert.Throws<ArgumentException>(() =>
            enc.EncodeRxIq(new byte[P2Wire.BufLen - 1], 0, iq));
    }

    [Fact]
    public void EncodePsFeedback_RejectsShortBuffers()
    {
        var enc = new P2RxDdcEncoder();
        var ok = MakeIq(P2RxDdcEncoder.PsPairsPerPacket, 0.5, 0.5);
        var shortBuf = new double[2 * P2RxDdcEncoder.PsPairsPerPacket - 1];
        var packet = new byte[P2Wire.BufLen];
        Assert.Throws<ArgumentException>(() => enc.EncodePsFeedback(packet, 0, shortBuf, ok));
        Assert.Throws<ArgumentException>(() => enc.EncodePsFeedback(packet, 0, ok, shortBuf));
    }

    private static int SignExtend24(int raw)
    {
        if ((raw & 0x800000) != 0) raw |= unchecked((int)0xFF000000);
        return raw;
    }
}
