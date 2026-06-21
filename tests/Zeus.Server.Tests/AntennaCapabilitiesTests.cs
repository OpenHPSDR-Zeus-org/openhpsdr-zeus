// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Per-board antenna capability matrix (external-ports plan — antenna slice,
// #804). Pins HasTxAntennaRelays / HasRxAntennaRelays / RxAuxInputs /
// HasRx2AntennaPath per board so the wire gates (and the REST 409 gates) stay
// aligned with the hardware. Audio / GPIO caps are out of this slice's scope.

using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class AntennaCapabilitiesTests
{
    [Fact]
    public void TxAntennaRelays_Only_OrionMkII_0x0A_Family()
    {
        // Only the 0x0A / Saturn family has switchable TX antenna relays.
        // Every variant in that family (incl. Apache OrionMkII original) does.
        foreach (var variant in Enum.GetValues<OrionMkIIVariant>())
            Assert.True(BoardCapabilitiesTable.For(HpsdrBoardKind.OrionMkII, variant).HasTxAntennaRelays);

        // No P1 board and not HL2.
        foreach (var board in new[] {
            HpsdrBoardKind.Metis, HpsdrBoardKind.Hermes, HpsdrBoardKind.HermesII,
            HpsdrBoardKind.Angelia, HpsdrBoardKind.Orion, HpsdrBoardKind.HermesC10,
            HpsdrBoardKind.HermesLite2 })
            Assert.False(BoardCapabilitiesTable.For(board).HasTxAntennaRelays);
    }

    [Fact]
    public void RxAntennaRelays_Every_ANAN_Not_HL2()
    {
        // Every ANAN / Hermes-class board has Alex RX-antenna relays; HL2 does
        // not (single jack → N2ADR pad, clamped to ANT1 at the wire layer).
        foreach (var board in new[] {
            HpsdrBoardKind.Metis, HpsdrBoardKind.Hermes, HpsdrBoardKind.HermesII,
            HpsdrBoardKind.Angelia, HpsdrBoardKind.Orion, HpsdrBoardKind.OrionMkII,
            HpsdrBoardKind.HermesC10 })
            Assert.True(BoardCapabilitiesTable.For(board).HasRxAntennaRelays);

        Assert.False(BoardCapabilitiesTable.For(HpsdrBoardKind.HermesLite2).HasRxAntennaRelays);
    }

    [Fact]
    public void RxAuxInputs_All_On_Alex_Boards_None_On_HL2()
    {
        // Every Alex-class P1 board and the 0x0A family exposes the full RX-aux
        // input set (EXT1/EXT2/XVTR/BYPASS). HL2 exposes NONE — the jacks don't
        // physically exist.
        foreach (var board in new[] {
            HpsdrBoardKind.Metis, HpsdrBoardKind.Hermes, HpsdrBoardKind.HermesII,
            HpsdrBoardKind.Angelia, HpsdrBoardKind.Orion, HpsdrBoardKind.OrionMkII,
            HpsdrBoardKind.HermesC10 })
            Assert.Equal(RxAuxInputs.All, BoardCapabilitiesTable.For(board).RxAuxInputs);

        Assert.Equal(RxAuxInputs.None, BoardCapabilitiesTable.For(HpsdrBoardKind.HermesLite2).RxAuxInputs);
    }

    [Fact]
    public void Rx2AntennaPath_Only_DualAdc_Boards()
    {
        // The dual-ADC boards (100D / 200D / 0x0A family) have a second RX
        // antenna path; single-RX boards (Hermes-class, G2E, HL2) do not.
        foreach (var board in new[] {
            HpsdrBoardKind.Angelia, HpsdrBoardKind.Orion, HpsdrBoardKind.OrionMkII })
            Assert.True(BoardCapabilitiesTable.For(board).HasRx2AntennaPath);

        foreach (var board in new[] {
            HpsdrBoardKind.Metis, HpsdrBoardKind.Hermes, HpsdrBoardKind.HermesII,
            HpsdrBoardKind.HermesC10, HpsdrBoardKind.HermesLite2 })
            Assert.False(BoardCapabilitiesTable.For(board).HasRx2AntennaPath);
    }

    [Fact]
    public void All_0x0A_Variants_Share_Antenna_Semantics()
    {
        // The 0x0A family shares HpsdrBoardKind.OrionMkII, so every variant must
        // report the same antenna fingerprint — the band rows are board-agnostic
        // and round-trip across variants.
        foreach (var variant in Enum.GetValues<OrionMkIIVariant>())
        {
            var caps = BoardCapabilitiesTable.For(HpsdrBoardKind.OrionMkII, variant);
            Assert.True(caps.HasTxAntennaRelays);
            Assert.True(caps.HasRxAntennaRelays);
            Assert.Equal(RxAuxInputs.All, caps.RxAuxInputs);
            Assert.True(caps.HasRx2AntennaPath);
        }
    }

    [Fact]
    public void UnknownDefaults_Advertise_No_Antenna_Ports()
    {
        // Conservative: an unrecognised board must not claim any antenna port.
        var u = BoardCapabilities.UnknownDefaults;
        Assert.False(u.HasTxAntennaRelays);
        Assert.False(u.HasRxAntennaRelays);
        Assert.Equal(RxAuxInputs.None, u.RxAuxInputs);
        Assert.False(u.HasRx2AntennaPath);
    }
}
