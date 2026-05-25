// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Simone Fabris (IU3QEZ), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Buffers.Binary;
using Zeus.Contracts;

namespace Zeus.Protocol1.Tests;

public class ControlFrameRxTxSplitTests
{
    private static ControlFrame.CcState SplitState(long rxHz, long txHz) => new(
        RxFreqAHz: rxHz,
        TxFreqAHz: txHz,
        Rate: HpsdrSampleRate.Rate48k,
        PreampOn: false,
        Atten: HpsdrAtten.Zero,
        RxAntenna: HpsdrAntenna.Ant1,
        Mox: false,
        EnableHl2BandVolts: false,
        Board: HpsdrBoardKind.HermesLite2);

    [Fact]
    public void RxFreq_EncodesSplitRxFrequency()
    {
        var state = SplitState(rxHz: 14_050_250, txHz: 14_050_000);
        Span<byte> cc = stackalloc byte[5];
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.RxFreq, state);
        Assert.Equal(14_050_250u, BinaryPrimitives.ReadUInt32BigEndian(cc[1..5]));
    }

    [Fact]
    public void TxFreq_EncodesSplitTxFrequency()
    {
        var state = SplitState(rxHz: 14_050_250, txHz: 14_050_000);
        Span<byte> cc = stackalloc byte[5];
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.TxFreq, state);
        Assert.Equal(14_050_000u, BinaryPrimitives.ReadUInt32BigEndian(cc[1..5]));
    }

    [Fact]
    public void RxFreq2_FollowsRxFreq()
    {
        var state = SplitState(rxHz: 7_074_250, txHz: 7_074_000);
        Span<byte> cc = stackalloc byte[5];
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.RxFreq2, state);
        Assert.Equal(7_074_250u, BinaryPrimitives.ReadUInt32BigEndian(cc[1..5]));
    }

    [Fact]
    public void WhenEqual_AllRegistersMatchOriginalBehavior()
    {
        var state = SplitState(rxHz: 14_200_000, txHz: 14_200_000);
        Span<byte> cc = stackalloc byte[5];

        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.TxFreq, state);
        uint txVal = BinaryPrimitives.ReadUInt32BigEndian(cc[1..5]);

        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.RxFreq, state);
        uint rxVal = BinaryPrimitives.ReadUInt32BigEndian(cc[1..5]);

        Assert.Equal(txVal, rxVal);
        Assert.Equal(14_200_000u, txVal);
    }
}
