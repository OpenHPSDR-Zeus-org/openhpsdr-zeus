// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using Xunit;
using Zeus.Contracts;

namespace Zeus.Protocol1.Tests;

/// <summary>
/// Wire-side plumbing for the classic 2-DDC dual-receiver layout (issue
/// #1226). Angelia (ANAN-100D) and Orion (ANAN-200D) have two RX ADCs, but
/// before this fix Protocol1Client always sent NumReceiversMinusOne=0 and
/// mirrored DDC1's NCO onto VFO A — so both "receivers" demodulated the
/// same slice. These tests pin the new behaviour: setting RX2 enabled on a
/// dual-ADC board raises NumReceiversMinusOne to 1 and forwards VFO B into
/// RxFreq2, while HL2 (single ADC) stays byte-identical.
/// </summary>
public class Protocol1Client_Rx2WiringTests
{
    [Fact]
    public void Default_State_Is_SingleDdc_And_Zero_VfoB()
    {
        using var client = new Protocol1Client();
        var s = client.SnapshotState();
        Assert.Equal((byte)0, s.NumReceiversMinusOne);
        Assert.Equal(0L, s.VfoBHz);
    }

    [Fact]
    public void Angelia_Rx2Enabled_RaisesNumReceiversAndForwardsVfoB()
    {
        using var client = new Protocol1Client();
        client.SetBoardKind(HpsdrBoardKind.Angelia);
        client.SetVfoAHz(14_200_000);
        client.SetVfoBHz(21_050_000);
        client.SetRx2Enabled(true);

        var s = client.SnapshotState();
        Assert.Equal((byte)1, s.NumReceiversMinusOne);
        Assert.Equal(14_200_000L, s.VfoAHz);
        Assert.Equal(21_050_000L, s.VfoBHz);
    }

    [Fact]
    public void Orion_Rx2Enabled_RaisesNumReceivers()
    {
        using var client = new Protocol1Client();
        client.SetBoardKind(HpsdrBoardKind.Orion);
        client.SetRx2Enabled(true);

        Assert.Equal((byte)1, client.SnapshotState().NumReceiversMinusOne);
    }

    [Fact]
    public void Hl2_Rx2Enabled_Ignored_On_Wire()
    {
        // HL2 has one ADC; its NumReceiversMinusOne channel is owned by the
        // PS 4-DDC path. Toggling RX2 must not disturb the HL2 wire — the
        // PS path is single-source-of-truth for numRxMinus1 on that board.
        using var client = new Protocol1Client();
        client.SetBoardKind(HpsdrBoardKind.HermesLite2);
        client.SetVfoBHz(21_050_000);
        client.SetRx2Enabled(true);

        var s = client.SnapshotState();
        Assert.Equal((byte)0, s.NumReceiversMinusOne);
        Assert.Equal(0L, s.VfoBHz);
    }

    [Fact]
    public void Angelia_Rx2Disabled_Falls_Back_To_Single_DDC()
    {
        using var client = new Protocol1Client();
        client.SetBoardKind(HpsdrBoardKind.Angelia);
        client.SetVfoBHz(21_050_000);
        client.SetRx2Enabled(true);
        Assert.Equal((byte)1, client.SnapshotState().NumReceiversMinusOne);

        client.SetRx2Enabled(false);
        var s = client.SnapshotState();
        Assert.Equal((byte)0, s.NumReceiversMinusOne);
        // VfoBHz is gated on Rx2Enabled+non-HL2 so a stale VFO B never reaches
        // the wire once RX2 is turned back off.
        Assert.Equal(0L, s.VfoBHz);
    }

    [Fact]
    public void PhaseRegisters_Rx2Armed_Emits_RxFreq2_In_Rx_Rotation()
    {
        // Sanity check: the widened 6-phase RX rotation actually contains at
        // least one RxFreq2 slot so DDC1's NCO tracks VFO B. Without this the
        // radio would sit on the last RxFreq2 value we happened to send
        // during connect.
        bool sawRxFreq2 = false;
        for (int phase = 0; phase < 6; phase++)
        {
            var (a, b) = Protocol1Client.PhaseRegisters(phase, mox: false, psArmed: false, rx2Armed: true);
            if (a == ControlFrame.CcRegister.RxFreq2 || b == ControlFrame.CcRegister.RxFreq2)
            {
                sawRxFreq2 = true;
                break;
            }
        }
        Assert.True(sawRxFreq2, "RxFreq2 must appear in the rx2Armed RX rotation");
    }
}
