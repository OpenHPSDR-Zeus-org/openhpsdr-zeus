// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Zeus.Contracts;
using Zeus.Protocol1.Discovery;

namespace Zeus.Protocol1.Tests;

/// <summary>
/// GOLDEN-BYTE CHARACTERIZATION tests for the Protocol-1 Config (0x00) frame
/// (external-ports plan, Phase 1). These lock the *current* emitted C0..C4
/// bytes for the external-port-relevant fields — RX antenna (C3[7:5]), OC
/// masks (C2[7:1]), duplex (C4[2]), and RX-count (C4[5:3]) — across boards
/// and MOX states.
///
/// These are the safety net for the IExternalPortEncoder refactor: the
/// encoder must reproduce these bytes EXACTLY. A single byte diff means the
/// refactor changed behaviour. Do not "fix" these to match new output —
/// they ARE the contract.
///
/// Co-tenancy is the point: C3 carries RxAntenna[7:5] alongside HL2 band-volts
/// [3] and preamp [4]; C4 carries duplex[2] + RX-count[5:3] and must NEVER pick
/// up TX-antenna bits for any supported board. The asserts below pin every
/// co-tenant so the refactor can't clobber one while moving another.
/// </summary>
public class ExternalPortGoldenTests
{
    // A clean ANAN-class (codec board, RX relays) state at 14.2 MHz, RX.
    private static ControlFrame.CcState HermesState() => new(
        VfoAHz: 14_200_000,
        Rate: HpsdrSampleRate.Rate48k,
        PreampOn: false,
        Atten: HpsdrAtten.Zero,
        RxAntenna: HpsdrAntenna.Ant1,
        Mox: false,
        EnableHl2BandVolts: false,
        Board: HpsdrBoardKind.Hermes);

    private static ControlFrame.CcState Hl2State() => new(
        VfoAHz: 14_200_000,
        Rate: HpsdrSampleRate.Rate48k,
        PreampOn: false,
        Atten: HpsdrAtten.Zero,
        RxAntenna: HpsdrAntenna.Ant1,
        Mox: false,
        EnableHl2BandVolts: false,
        Board: HpsdrBoardKind.HermesLite2);

    private static byte[] Config(in ControlFrame.CcState s)
    {
        Span<byte> cc = stackalloc byte[5];
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.Config, s);
        return cc.ToArray();
    }

    // ---- RX antenna C3[7:5] across Ant1/Ant2/Ant3 (Hermes / codec board) ----

    [Theory]
    [InlineData(HpsdrAntenna.Ant1, 0x00)] // 0 << 5
    [InlineData(HpsdrAntenna.Ant2, 0x20)] // 1 << 5
    [InlineData(HpsdrAntenna.Ant3, 0x40)] // 2 << 5
    public void Config_C3_RxAntenna_Bits_AreLocked(HpsdrAntenna ant, byte expectedC3)
    {
        var cc = Config(HermesState() with { RxAntenna = ant });

        // C0 = Config wire byte, RX (MOX off).
        Assert.Equal(0x00, cc[0]);
        // C1 = sample rate (48k = 0).
        Assert.Equal(0x00, cc[1]);
        // C2 = OC pins (none) << 1 = 0.
        Assert.Equal(0x00, cc[2]);
        // C3 = preamp off, band-volts off → just the RX-antenna bits.
        Assert.Equal(expectedC3, cc[3]);
        // C4 = duplex(1<<2) | rx-count(0).
        Assert.Equal(0x04, cc[4]);
    }

    // RxAntenna co-tenants C3 with preamp[4] and (HL2-only) band-volts[3].
    // Pin that the antenna shift never clobbers the neighbours.
    [Fact]
    public void Config_C3_RxAntenna_CoTenants_Preamp_AreLocked()
    {
        var cc = Config(HermesState() with
        {
            RxAntenna = HpsdrAntenna.Ant3,
            PreampOn = true,
        });

        // preamp[4]=0x10 | antenna Ant3 [7:5]=0x40 → 0x50.
        Assert.Equal(0x50, cc[3]);
        Assert.Equal(0x04, cc[4]);
    }

    [Fact]
    public void Config_Hl2_C3_RxAntenna_CoTenants_BandVolts_AreLocked()
    {
        // HL2 today emits C3[7:5] unconditionally (the value flows to the
        // N2ADR antenna pad). Lock TODAY's bytes; the Phase-2 clamp will
        // change this on purpose, with its own test.
        var cc = Config(Hl2State() with
        {
            RxAntenna = HpsdrAntenna.Ant2,
            EnableHl2BandVolts = true,
            PreampOn = true,
        });

        // band-volts[3]=0x08 | preamp[4]=0x10 | Ant2 [7:5]=0x20 → 0x38.
        Assert.Equal(0x38, cc[3]);
        Assert.Equal(0x04, cc[4]);
    }

    // ---- OC mask C2[7:1] — MOX selects TX vs RX mask ----

    [Fact]
    public void Config_C2_OcMask_Rx_IsShiftedLeftOne()
    {
        // UserOcRxMask 0x05 (7-bit) → C2 = 0x05 << 1 = 0x0A (RX, MOX off).
        var cc = Config(HermesState() with { UserOcRxMask = 0x05, UserOcTxMask = 0x42 });
        Assert.Equal(0x0A, cc[2]);
    }

    [Fact]
    public void Config_C2_OcMask_Tx_IsSelectedWhenMox()
    {
        // MOX on → TX mask 0x42 << 1 = 0x84.
        var cc = Config(HermesState() with
        {
            Mox = true,
            UserOcRxMask = 0x05,
            UserOcTxMask = 0x42,
        });
        Assert.Equal(0x01, cc[0]);    // Config + MOX bit
        Assert.Equal(0x84, cc[2]);
    }

    [Fact]
    public void Config_Hl2_C2_OcMask_OrsN2adrAutoMask()
    {
        // HL2 with N2ADR ORs the auto-filter mask first, then the user mask.
        // 14.2 MHz lives in the 20m N2ADR bucket; lock whatever that resolves
        // to today combined with a user RX bit.
        byte n2adr = N2adrBands.RxOcMask(14_200_000);
        var cc = Config(Hl2State() with { HasN2adr = true, UserOcRxMask = 0x01 });
        byte expectedC2 = (byte)(((n2adr | 0x01) & 0x7F) << 1);
        Assert.Equal(expectedC2, cc[2]);
    }

    // ---- C4 duplex + RX-count, never TX antenna ----

    [Theory]
    [InlineData((byte)0, 0x04)] // duplex only
    [InlineData((byte)1, 0x0C)] // duplex | (1<<3)
    [InlineData((byte)3, 0x1C)] // duplex | (3<<3)
    public void Config_C4_Duplex_And_RxCount_AreLocked(byte rxMinusOne, byte expectedC4)
    {
        var cc = Config(HermesState() with { NumReceiversMinusOne = rxMinusOne });
        Assert.Equal(expectedC4, cc[4]);
        // Bit 2 (duplex) always set.
        Assert.Equal(0x04, cc[4] & 0x04);
        // Bits [1:0] (TX antenna on legacy Alex) always clear — Zeus emits no
        // TX-antenna relay bits on the P1 Config frame for any board.
        Assert.Equal(0x00, cc[4] & 0x03);
    }

    [Fact]
    public void Config_C4_NeverCarriesTxAntenna_EvenWithRxAntennaSet()
    {
        // Selecting a non-default RX antenna must not bleed into C4.
        var cc = Config(HermesState() with
        {
            RxAntenna = HpsdrAntenna.Ant3,
            NumReceiversMinusOne = 1,
        });
        Assert.Equal(0x0C, cc[4]);          // duplex | rx-count, no TX-ant bits
        Assert.Equal(0x00, cc[4] & 0x03);
    }
}
