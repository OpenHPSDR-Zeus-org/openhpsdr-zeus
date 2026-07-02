// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Zeus.Contracts;
using Zeus.Protocol1.Discovery;

namespace Zeus.Protocol1.Tests;

/// <summary>
/// HermesC10 (ANAN-G2E) P1 PureSignal — golden byte-identity + positive
/// locks, on the <see cref="ExternalPortGoldenTests"/> pattern.
///
/// Part 1 (the load-bearing suite): with <c>PsEnabled=true, Mox=true</c>,
/// EVERY P1 board except HermesC10 — Metis, Hermes, HermesII, Angelia,
/// Orion, OrionMkII, and HermesLite2 — must emit Config / Attenuator(0x14) /
/// DriveFilter(0x12) / 0x1c payloads byte-for-byte identical to what develop
/// produced before the G2E work. HL2's own PS bytes are locked to their
/// CURRENT (PS-on) values, not the PS-off baseline. A single byte diff means
/// the HermesC10 branches leaked into another board's wire — do not "fix"
/// these to match new output; they ARE the contract.
///
/// Part 2: HermesC10 positive locks — C2[6] puresignal_run when armed,
/// Config C3 RX-BYPASS override only while armed+keyed (operator antenna
/// restored otherwise), Config C4[5:3]=3 only while armed+keyed, and the
/// 0x1c atten_on_Tx payload (operator value clamped 0..31; sentinel-unset →
/// 31, the silicon reset default — never an unrequested 0 dB).
///
/// Part 3: rotation — HermesC10 armed selects the 16-phase PS rotation;
/// every other non-HL2 board armed stays on the 5-phase rotation (its wire
/// traffic is byte-identical to a disarmed session).
///
/// Gateware cites (docs/references/firmware/hermesc10_anan-g2e/P1-gateware/
/// Hermes_3.3_C10_P1_Mk2PA/Hermes.v): PureSignal_enable = 0x0a[22]
/// (:2170-2173), IF_RX_relay = Config C3[6:5] (:2144) → Mk2PA Alex SPI
/// bit 11 (:2492-2494), atten_on_Tx = 0x0e C3[4:0] (:2187, reset 31 :2127),
/// IF_last_chan = Config C4[5:3] (:2151).
/// </summary>
public class ControlFramePsHermesC10GoldenTests
{
    // Canonical PS-armed keyed state at 14.2 MHz. Everything else at
    // defaults, exactly like the ExternalPortGoldenTests base states.
    private static ControlFrame.CcState PsKeyedState(HpsdrBoardKind board) => new(
        VfoAHz: 14_200_000,
        Rate: HpsdrSampleRate.Rate48k,
        PreampOn: false,
        Atten: HpsdrAtten.Zero,
        RxAntenna: HpsdrAntenna.Ant1,
        Mox: true,
        EnableHl2BandVolts: false,
        Board: board,
        PsEnabled: true);

    private static byte[] Cc(ControlFrame.CcRegister register, in ControlFrame.CcState s)
    {
        Span<byte> cc = stackalloc byte[5];
        ControlFrame.WriteCcBytes(cc, register, s);
        return cc.ToArray();
    }

    // Every P1 board that must stay byte-identical with PS armed, except HL2
    // (which has its own PS-on goldens below) and HermesC10 (the board under
    // change, positive-locked in part 2).
    public static TheoryData<HpsdrBoardKind> NonHl2NonC10Boards => new()
    {
        HpsdrBoardKind.Metis,
        HpsdrBoardKind.Hermes,
        HpsdrBoardKind.HermesII,
        HpsdrBoardKind.Angelia,
        HpsdrBoardKind.Orion,
        HpsdrBoardKind.OrionMkII,
    };

    // ---- Part 1a: non-HL2 boards, PS armed + keyed — develop's exact bytes --

    [Theory]
    [MemberData(nameof(NonHl2NonC10Boards))]
    public void Golden_Config_NonHl2_PsKeyed_IsByteIdentical(HpsdrBoardKind board)
    {
        // develop: C0=Config|MOX, C1=rate48k(0), C2=OC-TX mask 0 << 1,
        // C3 = preamp/dither/random off + Ant1 (0), C4 = duplex only —
        // NumReceiversMinusOne stays 0 (SnapshotState never bumps it for
        // these boards; pinned end-to-end in part 3).
        Assert.Equal(new byte[] { 0x01, 0x00, 0x00, 0x00, 0x04 },
            Cc(ControlFrame.CcRegister.Config, PsKeyedState(board)));
    }

    [Theory]
    [MemberData(nameof(NonHl2NonC10Boards))]
    public void Golden_Attenuator_NonHl2_PsKeyed_IsByteIdentical(HpsdrBoardKind board)
    {
        // develop: C4 = 0x20 | (0 dB & 0x1F); C1..C3 zero — in particular NO
        // C2[6] puresignal_run (that bit is HL2 + HermesC10 only).
        Assert.Equal(new byte[] { 0x15, 0x00, 0x00, 0x00, 0x20 },
            Cc(ControlFrame.CcRegister.Attenuator, PsKeyedState(board)));
    }

    [Theory]
    [MemberData(nameof(NonHl2NonC10Boards))]
    public void Golden_DriveFilter_NonHl2_PsKeyed_IsByteIdentical(HpsdrBoardKind board)
    {
        // develop: C1 = drive byte; C2 = 0 (mic_boost/mic_linein/ATU off;
        // the 0x08 PA-enable bit is HL2-only).
        var s = PsKeyedState(board) with { DriveLevel = 200 };
        Assert.Equal(new byte[] { 0x13, 200, 0x00, 0x00, 0x00 },
            Cc(ControlFrame.CcRegister.DriveFilter, s));
    }

    [Theory]
    [MemberData(nameof(NonHl2NonC10Boards))]
    public void Golden_LnaTxGainStable_NonHl2_PsKeyed_IsAllZero(HpsdrBoardKind board)
    {
        // develop: the 0x1c payload is all-zero on every board. The
        // HermesC10 atten_on_Tx branch must not leak here — an accidental
        // C3 write would land in these boards' en_tx_gain field.
        Assert.Equal(new byte[] { 0x1D, 0x00, 0x00, 0x00, 0x00 },
            Cc(ControlFrame.CcRegister.LnaTxGainStable, PsKeyedState(board)));
    }

    [Theory]
    [InlineData(HpsdrBoardKind.Metis, HpsdrAntenna.Ant2, 0x20)]
    [InlineData(HpsdrBoardKind.Hermes, HpsdrAntenna.Ant2, 0x20)]
    [InlineData(HpsdrBoardKind.Hermes, HpsdrAntenna.Ant3, 0x40)]
    [InlineData(HpsdrBoardKind.HermesII, HpsdrAntenna.Ant3, 0x40)]
    [InlineData(HpsdrBoardKind.Angelia, HpsdrAntenna.Ant2, 0x20)]
    [InlineData(HpsdrBoardKind.Orion, HpsdrAntenna.Ant3, 0x40)]
    [InlineData(HpsdrBoardKind.OrionMkII, HpsdrAntenna.Ant2, 0x20)]
    public void Golden_Config_NonHl2_PsKeyed_KeepsOperatorRxAntenna(
        HpsdrBoardKind board, HpsdrAntenna ant, byte expectedC3)
    {
        // The HermesC10 RX-BYPASS override must not hijack C3[7:5] on any
        // other board: PS armed + keyed still emits the operator's antenna.
        var cc = Cc(ControlFrame.CcRegister.Config,
            PsKeyedState(board) with { RxAntenna = ant });
        Assert.Equal(expectedC3, cc[3]);
    }

    // ---- Part 1b: HL2, PS armed + keyed — CURRENT PS bytes, locked ---------

    [Fact]
    public void Golden_Attenuator_Hl2_PsKeyed_IsByteIdentical()
    {
        // develop's live HL2 PS wire: C2 = 0x40 (puresignal_run, reg 0x0a
        // bit 22), C4 = 0x40 | (60 - 0 dB) = 0x7C (AD9866 gain-reduction
        // form; TX-attn sentinel untouched keeps the RX-side encoding).
        Assert.Equal(new byte[] { 0x15, 0x00, 0x40, 0x00, 0x7C },
            Cc(ControlFrame.CcRegister.Attenuator, PsKeyedState(HpsdrBoardKind.HermesLite2)));
    }

    [Fact]
    public void Golden_Attenuator_Hl2_PsKeyed_TxAttnDance_IsByteIdentical()
    {
        // develop's auto-attenuate dance wire: with Hl2TxAttnDb=10 set and
        // MOX on, C4 swaps to the TX-PGA form (31 - 10) | 0x40 = 0x55
        // (mi0bot console.cs:10947-10948). PS bit stays in C2.
        var s = PsKeyedState(HpsdrBoardKind.HermesLite2) with { Hl2TxAttnDb = 10 };
        Assert.Equal(new byte[] { 0x15, 0x00, 0x40, 0x00, 0x55 },
            Cc(ControlFrame.CcRegister.Attenuator, s));
    }

    [Fact]
    public void Golden_Config_Hl2_PsKeyed_IsByteIdentical()
    {
        // develop: C3 = 0 (band-volts off, antenna clamped to ANT1), C4 =
        // duplex | numRx. At the state level NumReceiversMinusOne is an
        // input; the 4-DDC bump itself is pinned end-to-end below.
        Assert.Equal(new byte[] { 0x01, 0x00, 0x00, 0x00, 0x04 },
            Cc(ControlFrame.CcRegister.Config, PsKeyedState(HpsdrBoardKind.HermesLite2)));
        var s = PsKeyedState(HpsdrBoardKind.HermesLite2) with { NumReceiversMinusOne = 3 };
        Assert.Equal(new byte[] { 0x01, 0x00, 0x00, 0x00, 0x1C },
            Cc(ControlFrame.CcRegister.Config, s));
    }

    [Fact]
    public void Golden_DriveFilter_Hl2_PsKeyed_IsByteIdentical()
    {
        // develop: C2 = 0x08 (HL2 PA-enable while MOX), drive in C1.
        var s = PsKeyedState(HpsdrBoardKind.HermesLite2) with { DriveLevel = 200 };
        Assert.Equal(new byte[] { 0x13, 200, 0x08, 0x00, 0x00 },
            Cc(ControlFrame.CcRegister.DriveFilter, s));
    }

    [Fact]
    public void Golden_LnaTxGainStable_Hl2_PsKeyed_IsAllZero()
    {
        // develop: HL2's 0x1c stays all-zero (en_tx_gain=0 — the AD9866
        // PGA-stability write PS convergence depends on, Issue #172). The
        // HermesC10 atten branch would write 31 here if the board gate
        // regressed; that byte-diff is exactly what this pins.
        Assert.Equal(new byte[] { 0x1D, 0x00, 0x00, 0x00, 0x00 },
            Cc(ControlFrame.CcRegister.LnaTxGainStable, PsKeyedState(HpsdrBoardKind.HermesLite2)));
    }

    [Fact]
    public void Golden_Hl2_PsKeyed_NumRx_StaysFourDdc()
    {
        // End-to-end through the real client: HL2 + PS + MOX still requests
        // the 4-DDC layout (NumReceiversMinusOne = 3) — the HermesC10 gate
        // widening must not have disturbed the HL2 leg.
        using var client = new Protocol1Client();
        client.SetBoardKind(HpsdrBoardKind.HermesLite2);
        client.SetPsEnabled(true);
        client.SetMox(true);
        Assert.Equal(3, client.SnapshotState().NumReceiversMinusOne);
    }

    [Theory]
    [MemberData(nameof(NonHl2NonC10Boards))]
    public void Golden_NonHl2NonC10_PsKeyed_NumRx_StaysSingleDdc(HpsdrBoardKind board)
    {
        // End-to-end through the real client: no other P1 board may request
        // the 4-DDC layout, PS armed or not — its EP6 stream shape (and the
        // parser gate that mirrors it) must stay byte-identical to today.
        using var client = new Protocol1Client();
        client.SetBoardKind(board);
        client.SetPsEnabled(true);
        client.SetMox(true);
        Assert.Equal(0, client.SnapshotState().NumReceiversMinusOne);
    }

    // ---- Part 2: HermesC10 positive locks ----------------------------------

    [Theory]
    [InlineData(false)]  // armed at rest — gateware mux acts only under FPGA_PTT
    [InlineData(true)]   // armed + keyed
    public void C10_Attenuator_PsEnabled_Sets_C2_Bit6_Not_C3(bool mox)
    {
        // Hermes.v:2170-2173 — PureSignal_enable <= IF_Rx_ctrl_2[6] under
        // addr 0001_010. Same PR #119 guard as HL2: the bit must be in C2,
        // never C3.
        var s = PsKeyedState(HpsdrBoardKind.HermesC10) with { Mox = mox };
        var cc = Cc(ControlFrame.CcRegister.Attenuator, s);
        Assert.Equal(1 << 6, cc[2] & (1 << 6));
        Assert.Equal(0, cc[3] & (1 << 6));
    }

    [Fact]
    public void C10_Attenuator_PsDisabled_Does_Not_Set_C2_Bit6()
    {
        var s = PsKeyedState(HpsdrBoardKind.HermesC10) with { PsEnabled = false };
        Assert.Equal(0, Cc(ControlFrame.CcRegister.Attenuator, s)[2] & (1 << 6));
    }

    [Fact]
    public void C10_Attenuator_PsEnabled_Keeps_C4_StepAttenuator()
    {
        // The HermesC10 keeps the non-HL2 C4 encoding 0x20 | db — PS arming
        // must not disturb the co-tenant RX step-attenuator byte.
        var s = PsKeyedState(HpsdrBoardKind.HermesC10) with { Atten = new HpsdrAtten(20) };
        Assert.Equal(0x20 | 20, Cc(ControlFrame.CcRegister.Attenuator, s)[4]);
    }

    [Fact]
    public void C10_Config_ArmedAndKeyed_OverridesC3WithRxBypass()
    {
        // Armed + keyed → C3[6:5] = 01 (RX BYPASS relay, Hermes.v:2144 →
        // Mk2PA Alex SPI bit 11). Ant3 (0x40) is deliberately chosen so the
        // override (0x20) is distinguishable from the operator selection.
        // C3[7] (IF_Rout) must stay 0 — decoded but unused on the Mk2PA
        // build, byte-minimal wins over Thetis parity.
        var s = PsKeyedState(HpsdrBoardKind.HermesC10) with { RxAntenna = HpsdrAntenna.Ant3 };
        var cc = Cc(ControlFrame.CcRegister.Config, s);
        Assert.Equal(0b001 << 5, cc[3]);
        Assert.Equal(0, cc[3] & 0x80);
    }

    [Theory]
    [InlineData(true, false)]   // armed at rest — antenna restored
    [InlineData(false, true)]   // keyed, PS off — antenna untouched
    [InlineData(false, false)]  // idle
    public void C10_Config_NotArmedAndKeyed_KeepsOperatorRxAntenna(bool psEnabled, bool mox)
    {
        // The rotation re-sends Config with MOX=0 at every unkey; the
        // gateware has no PTT term on this relay, so this host-driven
        // restore is what parks the bypass. Ant3 → C3[7:5] = 0x40.
        var s = PsKeyedState(HpsdrBoardKind.HermesC10) with
        {
            RxAntenna = HpsdrAntenna.Ant3,
            PsEnabled = psEnabled,
            Mox = mox,
        };
        Assert.Equal(0x40, Cc(ControlFrame.CcRegister.Config, s)[3]);
    }

    [Theory]
    // (board, psEnabled, mox) → expected C3[7:5] byte for RxAntenna=Ant3.
    [InlineData(HpsdrBoardKind.HermesC10, true, true, 0x20)]   // bypass override
    [InlineData(HpsdrBoardKind.HermesC10, true, false, 0x40)]  // antenna restored
    [InlineData(HpsdrBoardKind.HermesC10, false, true, 0x40)]  // PS off
    [InlineData(HpsdrBoardKind.Hermes, true, true, 0x40)]      // other board: no override
    [InlineData(HpsdrBoardKind.HermesLite2, true, true, 0x00)] // HL2: clamped to ANT1
    public void C10_EncodePsBypassOrRxAntennaC3Bits_Matrix(
        HpsdrBoardKind board, bool psEnabled, bool mox, byte expected)
    {
        Assert.Equal(expected,
            ControlFrame.EncodePsBypassOrRxAntennaC3Bits(HpsdrAntenna.Ant3, board, psEnabled, mox));
    }

    [Theory]
    [InlineData(true, true, 3)]    // armed + keyed → 4-DDC (IF_last_chan = 3, Hermes.v:2151)
    [InlineData(true, false, 0)]   // armed at rest → single DDC, byte-identical wire
    [InlineData(false, true, 0)]   // keyed, PS off → single DDC
    [InlineData(false, false, 0)]  // idle
    public void C10_NumRx_FourDdc_OnlyWhenArmedAndKeyed(bool psEnabled, bool mox, byte expected)
    {
        using var client = new Protocol1Client();
        client.SetBoardKind(HpsdrBoardKind.HermesC10);
        client.SetPsEnabled(psEnabled);
        client.SetMox(mox);
        Assert.Equal(expected, client.SnapshotState().NumReceiversMinusOne);
    }

    [Fact]
    public void C10_LnaTxGainStable_SentinelUnset_Emits31()
    {
        // Operator never set a value → emit 31, the silicon reset default
        // (Hermes.v:2127) — an honest no-op, NEVER an unrequested 0 dB
        // (0 dB is the ADC-clip extreme on a hot feedback tap).
        var cc = Cc(ControlFrame.CcRegister.LnaTxGainStable, PsKeyedState(HpsdrBoardKind.HermesC10));
        Assert.Equal(new byte[] { 0x1D, 0x00, 0x00, 31, 0x00 }, cc);
    }

    [Theory]
    [InlineData(0, 0)]     // operator floor
    [InlineData(12, 12)]   // mid-range passes through
    [InlineData(31, 31)]   // operator ceiling
    [InlineData(-5, 0)]    // clamp low
    [InlineData(99, 31)]   // clamp high
    public void C10_LnaTxGainStable_C3_CarriesClampedAttenOnTx(int db, byte expectedC3)
    {
        var s = PsKeyedState(HpsdrBoardKind.HermesC10) with { PsTxAttnOnTxDb = db };
        var cc = Cc(ControlFrame.CcRegister.LnaTxGainStable, s);
        Assert.Equal(expectedC3, cc[3]);
        // C1 / C2 / C4 are reserved at 0x0e on Hermes v3.3 — stay zero.
        Assert.Equal(0, cc[1]);
        Assert.Equal(0, cc[2]);
        Assert.Equal(0, cc[4]);
    }

    [Theory]
    [InlineData(12, 12)]
    [InlineData(-5, 0)]    // setter clamps low
    [InlineData(99, 31)]   // setter clamps high
    public void C10_SetPsTxAttenOnTxDb_PlumbsThroughClientToWire(int db, byte expectedC3)
    {
        // Full plumbing: setter → SnapshotState → 0x1c payload, and the
        // read-back property the auto-attenuate arm-edge baseline uses.
        using var client = new Protocol1Client();
        client.SetBoardKind(HpsdrBoardKind.HermesC10);
        client.SetPsTxAttenOnTxDb(db);
        Assert.Equal(expectedC3, client.PsTxAttenOnTxDb);

        Span<byte> cc = stackalloc byte[5];
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.LnaTxGainStable, client.SnapshotState());
        Assert.Equal(expectedC3, cc[3]);
    }

    [Fact]
    public void C10_PsTxAttenOnTxDb_UnsetReadsSiliconDefault31()
    {
        // The sentinel surfaces as 31 so the PS-arm baseline sync reads the
        // value the radio is actually holding, never a phantom 0.
        using var client = new Protocol1Client();
        client.SetBoardKind(HpsdrBoardKind.HermesC10);
        Assert.Equal(31, client.PsTxAttenOnTxDb);
    }

    // ---- Part 3: rotation --------------------------------------------------

    [Fact]
    public void Rotation_C10Armed_SelectsPsRotation()
    {
        using var client = new Protocol1Client();
        client.SetBoardKind(HpsdrBoardKind.HermesC10);
        client.SetPsEnabled(true);
        Assert.True(Protocol1Client.PsArmedRotation(client.SnapshotState()));
    }

    [Fact]
    public void Rotation_C10Disarmed_StaysOnFivePhase()
    {
        using var client = new Protocol1Client();
        client.SetBoardKind(HpsdrBoardKind.HermesC10);
        Assert.False(Protocol1Client.PsArmedRotation(client.SnapshotState()));
    }

    [Fact]
    public void Rotation_Hl2Armed_StillSelectsPsRotation()
    {
        using var client = new Protocol1Client();
        client.SetBoardKind(HpsdrBoardKind.HermesLite2);
        client.SetPsEnabled(true);
        Assert.True(Protocol1Client.PsArmedRotation(client.SnapshotState()));
    }

    [Theory]
    [MemberData(nameof(NonHl2NonC10Boards))]
    public void Rotation_OtherBoardsArmed_StayOnFivePhase(HpsdrBoardKind board)
    {
        using var client = new Protocol1Client();
        client.SetBoardKind(board);
        client.SetPsEnabled(true);
        Assert.False(Protocol1Client.PsArmedRotation(client.SnapshotState()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Rotation_PsArmed_16Phase_CarriesPsRegisters(bool mox)
    {
        // The PS rotation the C10 shares with HL2 must keep scheduling the
        // registers the G2E feature rides on: RxFreq3/RxFreq4 (feedback +
        // TX-reference NCOs) and LnaTxGainStable (atten_on_Tx carrier).
        var seen = new HashSet<ControlFrame.CcRegister>();
        for (int phase = 0; phase < 16; phase++)
        {
            var (first, second) = Protocol1Client.PhaseRegisters(phase, mox, psArmed: true);
            seen.Add(first);
            seen.Add(second);
        }
        Assert.Contains(ControlFrame.CcRegister.RxFreq3, seen);
        Assert.Contains(ControlFrame.CcRegister.RxFreq4, seen);
        Assert.Contains(ControlFrame.CcRegister.LnaTxGainStable, seen);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Rotation_NotArmed_FivePhase_NeverEmitsPsRegisters(bool mox)
    {
        // The 5-phase rotation (every armed non-HL2-non-C10 board lands
        // here) wraps modulo 5 and never schedules the PS registers — that
        // is what keeps those boards' armed wire byte-identical to today.
        for (int phase = 0; phase < 16; phase++)
        {
            var expected = Protocol1Client.PhaseRegisters(phase % 5, mox, psArmed: false);
            var actual = Protocol1Client.PhaseRegisters(phase, mox, psArmed: false);
            Assert.Equal(expected, actual);
            foreach (var reg in new[] { actual.first, actual.second })
            {
                Assert.NotEqual(ControlFrame.CcRegister.RxFreq3, reg);
                Assert.NotEqual(ControlFrame.CcRegister.RxFreq4, reg);
                Assert.NotEqual(ControlFrame.CcRegister.LnaTxGainStable, reg);
            }
        }
    }
}
