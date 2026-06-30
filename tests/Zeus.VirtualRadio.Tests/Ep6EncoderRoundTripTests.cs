// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Zeus.Protocol1;            // internal PacketParser, via InternalsVisibleTo
using Zeus.VirtualRadio.P1;      // internal Ep6Encoder, via InternalsVisibleTo
using Zeus.VirtualRadio.Rf;

namespace Zeus.VirtualRadio.Tests;

/// <summary>
/// Anti-drift round-trip tests for <see cref="Ep6Encoder"/>: every packet it
/// produces is fed back through the REAL <c>Zeus.Protocol1.PacketParser</c> — the
/// decoder the emulator inverts — and the samples + FWD/REF telemetry + PTT echo
/// are asserted to survive. Socketless: pure byte buffers, no UDP. If the EP6
/// wire format ever changes in Zeus, these break.
/// </summary>
public class Ep6EncoderRoundTripTests
{
    private const int ComplexSamples = 126;          // PacketParser.ComplexSamplesPerPacket
    private const double Int24Lsb = 1.0 / 8_388_608.0; // one int24 quantisation step
    private const double Tol = 2 * Int24Lsb;           // round-trip tolerance (≈ 2.4e-7)

    private static double[] MakeInterleavedIq()
    {
        // 126 complex samples → 252 interleaved doubles, all within [-1, +1].
        var iq = new double[2 * ComplexSamples];
        for (int n = 0; n < ComplexSamples; n++)
        {
            iq[2 * n] = 0.75 * Math.Sin(2 * Math.PI * n / 17.0);      // I
            iq[2 * n + 1] = 0.60 * Math.Cos(2 * Math.PI * n / 23.0);  // Q
        }
        return iq;
    }

    [Fact]
    public void Encode_RoundTripsThroughPacketParser_IqSurvives()
    {
        var enc = new Ep6Encoder();
        var iq = MakeInterleavedIq();
        var telemetry = new RfTelemetry(FwdAdc: 2000, RefAdc: 150,
            FwdWatts: 50.0, RefWatts: 0.5, Swr: 1.1);

        var packet = new byte[Ep6Encoder.Ep6PacketLength];
        enc.Encode(packet, sequence: 0xDEADBEEF, iq, telemetry);

        var outBuf = new double[2 * ComplexSamples];
        bool ok = PacketParser.TryParsePacket(packet, outBuf, out uint seq, out int samples);

        Assert.True(ok);
        Assert.Equal(0xDEADBEEFu, seq);
        Assert.Equal(ComplexSamples, samples);

        for (int k = 0; k < 2 * ComplexSamples; k++)
            Assert.True(Math.Abs(outBuf[k] - iq[k]) <= Tol,
                $"sample {k}: encoded {iq[k]} but parsed {outBuf[k]}");
    }

    [Fact]
    public void Encode_PlacesFwdRefTelemetry_WhereZeusReadsItBack()
    {
        var enc = new Ep6Encoder();
        var iq = MakeInterleavedIq();
        // RefAdc=150 (0x0096) keeps the C1[0]/C2[0] overload bit positions clear.
        var telemetry = new RfTelemetry(FwdAdc: 3210, RefAdc: 150,
            FwdWatts: 95.0, RefWatts: 0.4, Swr: 1.1);

        var packet = new byte[Ep6Encoder.Ep6PacketLength];
        enc.Encode(packet, sequence: 7, iq, telemetry);

        var outBuf = new double[2 * ComplexSamples];
        bool ok = PacketParser.TryParsePacket(packet, outBuf,
            out _, out _,
            out TelemetryReading tel0, out TelemetryReading tel1,
            out byte overload);

        Assert.True(ok);

        // Frame 0 → FWD slot (C0 address 1 → 0x08, PTT echo bit set because keyed).
        Assert.Equal(0x08, tel0.C0Address & 0x7E);
        Assert.Equal(telemetry.FwdAdc, tel0.Ain1);   // Zeus reads FWD from Ain1 of 0x08

        // Frame 1 → REF slot (C0 address 2 → 0x10).
        Assert.Equal(0x10, tel1.C0Address & 0x7E);
        Assert.Equal(telemetry.RefAdc, tel1.Ain0);   // Zeus reads REF from Ain0 of 0x10

        // Clean telemetry bytes here must not look like an ADC overload.
        Assert.Equal(0, overload);
    }

    [Fact]
    public void Encode_WhileKeyed_EchoesHardwarePtt()
    {
        var enc = new Ep6Encoder();
        var iq = MakeInterleavedIq();
        var telemetry = new RfTelemetry(FwdAdc: 1000, RefAdc: 50,
            FwdWatts: 25.0, RefWatts: 0.1, Swr: 1.05);

        var packet = new byte[Ep6Encoder.Ep6PacketLength];
        enc.Encode(packet, sequence: 1, iq, telemetry, pttEcho: true);

        Assert.True(PacketParser.ExtractHardwarePtt(packet));
    }

    [Fact]
    public void Encode_KeyedWithZeroDrive_StillEchoesHardwarePtt()
    {
        // Regression: C0[0] mirrors the firmware's debounced clean_PTT_in line,
        // which is asserted whenever keyed regardless of drive amplitude. A
        // keyed-with-zero-drive frame (drive slider at 0, or the transient between
        // a MOX-on frame and the first nonzero-drive frame) yields an all-zero
        // telemetry reading (FwdWatts == 0), yet the radio still echoes PTT. The
        // echo must follow the decoded MOX flag, not a forward-watts threshold.
        var enc = new Ep6Encoder();
        var iq = MakeInterleavedIq();
        var zeroDriveTelemetry = new RfTelemetry(FwdAdc: 0, RefAdc: 0,
            FwdWatts: 0.0, RefWatts: 0.0, Swr: 1.0);

        var packet = new byte[Ep6Encoder.Ep6PacketLength];
        enc.Encode(packet, sequence: 1, iq, zeroDriveTelemetry, pttEcho: true);

        Assert.True(PacketParser.ExtractHardwarePtt(packet));
    }

    [Fact]
    public void Encode_NotKeyed_NoPttEchoAndZeroTelemetry()
    {
        var enc = new Ep6Encoder();
        var iq = MakeInterleavedIq();
        // Receive-only: the telemetry model emits an all-zero reading at rest.
        var telemetry = new RfTelemetry(FwdAdc: 0, RefAdc: 0,
            FwdWatts: 0.0, RefWatts: 0.0, Swr: 1.0);

        var packet = new byte[Ep6Encoder.Ep6PacketLength];
        enc.Encode(packet, sequence: 2, iq, telemetry, pttEcho: false);

        Assert.False(PacketParser.ExtractHardwarePtt(packet));

        var outBuf = new double[2 * ComplexSamples];
        PacketParser.TryParsePacket(packet, outBuf, out _, out _,
            out TelemetryReading tel0, out TelemetryReading tel1, out _);
        Assert.Equal((ushort)0, tel0.Ain1); // FWD
        Assert.Equal((ushort)0, tel1.Ain0); // REF
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(-1.0)]
    [InlineData(2.5)]   // over-range positive → clamps to +full-scale
    [InlineData(-2.5)]  // over-range negative → clamps to -full-scale
    public void Encode_ClampsFullScale_WithoutOverflow(double level)
    {
        var enc = new Ep6Encoder();
        var iq = new double[2 * ComplexSamples];
        for (int k = 0; k < iq.Length; k++) iq[k] = level;

        var telemetry = new RfTelemetry(0, 0, 0, 0, 1.0);
        var packet = new byte[Ep6Encoder.Ep6PacketLength];
        enc.Encode(packet, sequence: 3, iq, telemetry);

        var outBuf = new double[2 * ComplexSamples];
        Assert.True(PacketParser.TryParsePacket(packet, outBuf, out _, out _));

        // Clamped full-scale lands within 1 LSB of ±1.0 (int24 max is 2^23-1).
        double expected = level > 0 ? 1.0 : -1.0;
        foreach (double v in outBuf)
            Assert.True(Math.Abs(v - expected) <= Tol, $"value {v} not clamped near {expected}");
    }

    [Fact]
    public void Encode_MicSamples_SurviveAsBigEndianInt16()
    {
        var enc = new Ep6Encoder();
        var iq = MakeInterleavedIq();
        var mic = new short[ComplexSamples];
        for (int n = 0; n < ComplexSamples; n++) mic[n] = (short)(n * 37 - 2000);

        var telemetry = new RfTelemetry(0, 0, 0, 0, 1.0);
        var packet = new byte[Ep6Encoder.Ep6PacketLength];
        enc.Encode(packet, sequence: 4, iq, telemetry, micSamples: mic);

        var micOut = new short[ComplexSamples];
        int n2 = PacketParser.ExtractMicSamples(packet, micOut);

        Assert.Equal(ComplexSamples, n2);
        for (int n = 0; n < ComplexSamples; n++) Assert.Equal(mic[n], micOut[n]);
    }

    [Fact]
    public void Encode_EmptyMic_EmitsSilence()
    {
        var enc = new Ep6Encoder();
        var iq = MakeInterleavedIq();
        var packet = new byte[Ep6Encoder.Ep6PacketLength];
        enc.Encode(packet, sequence: 5, iq, new RfTelemetry(0, 0, 0, 0, 1.0));

        var micOut = new short[ComplexSamples];
        PacketParser.ExtractMicSamples(packet, micOut);
        Assert.All(micOut, s => Assert.Equal((short)0, s));
    }

    [Fact]
    public void Encode_RejectsWrongPacketLength()
    {
        var enc = new Ep6Encoder();
        var iq = MakeInterleavedIq();
        Assert.Throws<ArgumentException>(() =>
            enc.Encode(new byte[Ep6Encoder.Ep6PacketLength - 1], 0, iq, new RfTelemetry(0, 0, 0, 0, 1.0)));
    }

    [Fact]
    public void Encode_RejectsShortIqBuffer()
    {
        var enc = new Ep6Encoder();
        var shortIq = new double[2 * ComplexSamples - 1];
        var packet = new byte[Ep6Encoder.Ep6PacketLength];
        Assert.Throws<ArgumentException>(() =>
            enc.Encode(packet, 0, shortIq, new RfTelemetry(0, 0, 0, 0, 1.0)));
    }

    [Fact]
    public void Encode_IncrementingSequence_RoundTripsMonotonic()
    {
        var enc = new Ep6Encoder();
        var iq = MakeInterleavedIq();
        var telemetry = new RfTelemetry(0, 0, 0, 0, 1.0);
        var packet = new byte[Ep6Encoder.Ep6PacketLength];
        var outBuf = new double[2 * ComplexSamples];

        for (uint s = 100; s < 110; s++)
        {
            enc.Encode(packet, s, iq, telemetry);
            Assert.True(PacketParser.TryParsePacket(packet, outBuf, out uint seq, out _));
            Assert.Equal(s, seq);
        }
    }
}
