// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Xunit;
using Zeus.Contracts;

namespace Zeus.Protocol2.Tests;

/// <summary>
/// Hermes-on-Protocol-2 RX DDC mapping (issue #171 Brick2; ANAN-10E follow-up).
///
/// Single-ADC Hermes-class radios — Hermes/Brick2 on wire byte 0x01 and
/// HermesII (ANAN-10E / ANAN-100B) on wire byte 0x02 — have one ADC and no
/// PureSignal feedback DDCs reserved at the front of the pool. receiver[i]
/// maps to DDC[i] (deskhpsdr <c>src/new_protocol.c:1692-1698</c> — only
/// <c>NEW_DEVICE_ANGELIA / ORION / ORION2 / SATURN</c> apply the
/// <c>ddc = 2 + i</c> offset). Every other family Zeus supports on P2 puts
/// the operator's RX0 at DDC2, so the OrionMkII default must remain the
/// wire shape we've been shipping.
///
/// These tests pin the byte-offsets so a Brick2 or ANAN-10E owner running
/// this branch sees DDC0 enabled, the DDC0 config block populated at
/// offset 17, and IQ arriving on UDP 1035 (instead of 1037).
/// </summary>
public class HermesP2DdcMappingTests
{
    [Fact]
    public void RxBaseDdc_Hermes_Is_Zero()
    {
        Assert.Equal(0, Protocol2Client.RxBaseDdc(HpsdrBoardKind.Hermes));
    }

    [Fact]
    public void RxBaseDdc_HermesII_Is_Zero()
    {
        // ANAN-10E / ANAN-100B firmware (wire byte 0x02): single ADC, no PS
        // DDCs reserved. Same DDC0 mapping as Hermes/Brick2 per deskhpsdr.
        Assert.Equal(0, Protocol2Client.RxBaseDdc(HpsdrBoardKind.HermesII));
    }

    [Theory]
    [InlineData(HpsdrBoardKind.OrionMkII)]
    [InlineData(HpsdrBoardKind.HermesC10)]
    [InlineData(HpsdrBoardKind.Orion)]
    [InlineData(HpsdrBoardKind.Angelia)]
    [InlineData(HpsdrBoardKind.HermesLite2)]
    [InlineData(HpsdrBoardKind.Metis)]
    [InlineData(HpsdrBoardKind.Unknown)]
    public void RxBaseDdc_NonHermes_Stays_At_Two(HpsdrBoardKind board)
    {
        // Anti-regression: only single-ADC Hermes-class wire bytes (0x01,
        // 0x02) flip to DDC0. Anything else MUST continue to report DDC2 —
        // that's what every shipped P2 wire format in Zeus assumes (every
        // test in PsWireFormatTests asserts on offset 29 = 17 + 12, i.e.
        // DDC2's config block).
        Assert.Equal(2, Protocol2Client.RxBaseDdc(board));
    }

    [Fact]
    public void CmdRx_Hermes_EnablesDdc0_Not_Ddc2()
    {
        var p = Protocol2Client.ComposeCmdRxBuffer(
            seq: 1, numAdc: 1, sampleRateKhz: 48, psEnabled: false,
            boardKind: HpsdrBoardKind.Hermes);

        // Bit 0 set = DDC0 enable; bit 2 (DDC2) MUST stay clear.
        Assert.Equal((byte)0x01, p[7]);
    }

    [Fact]
    public void CmdRx_Hermes_Writes_DDC0_Config_At_Offset_17()
    {
        var p = Protocol2Client.ComposeCmdRxBuffer(
            seq: 1, numAdc: 1, sampleRateKhz: 48, psEnabled: false,
            boardKind: HpsdrBoardKind.Hermes);

        // DDC0 config block: byte 17 = ADC0, bytes 18..19 = sample-rate BE,
        // byte 22 = bit depth (24).
        Assert.Equal((byte)0x00, p[17]);          // ADC0
        Assert.Equal((byte)0x00, p[18]);          // 48 kHz BE high
        Assert.Equal((byte)48,   p[19]);          // 48 kHz BE low
        Assert.Equal((byte)24,   p[22]);          // 24-bit

        // DDC2's config block (offset 29) MUST stay zero on Hermes — the
        // radio rejects the packet otherwise.
        Assert.Equal((byte)0x00, p[29]);
        Assert.Equal((byte)0x00, p[31]);
        Assert.Equal((byte)0x00, p[34]);
    }

    [Fact]
    public void CmdRx_Hermes_Does_Not_Set_DDC1_Sync_Or_PS_Block()
    {
        // psEnabled=true gets ignored for Hermes — the PS feedback layout is
        // OrionMkII-specific (DDC0+DDC1 paired with byte 1363 sync). If a
        // future code path tries to arm PS on Hermes we want to no-op the PS
        // bytes, not scribble OrionMkII shape onto the wire.
        var p = Protocol2Client.ComposeCmdRxBuffer(
            seq: 1, numAdc: 1, sampleRateKhz: 96, psEnabled: true,
            boardKind: HpsdrBoardKind.Hermes);

        Assert.Equal((byte)0x00, p[1363]);        // no DDC1→DDC0 sync
        Assert.Equal((byte)0x01, p[7]);           // only DDC0 enable
        Assert.Equal((byte)0x00, p[23]);          // DDC1 config block untouched
        Assert.Equal((byte)0x00, p[28]);
    }

    [Fact]
    public void CmdRx_HermesII_EnablesDdc0_Not_Ddc2()
    {
        // ANAN-10E (HermesII firmware) shares the single-ADC DDC0 wire shape
        // with Hermes/Brick2 — see deskhpsdr new_protocol.c:1692-1698.
        var p = Protocol2Client.ComposeCmdRxBuffer(
            seq: 1, numAdc: 1, sampleRateKhz: 48, psEnabled: false,
            boardKind: HpsdrBoardKind.HermesII);

        Assert.Equal((byte)0x01, p[7]);
    }

    [Fact]
    public void CmdRx_HermesII_Writes_DDC0_Config_At_Offset_17()
    {
        var p = Protocol2Client.ComposeCmdRxBuffer(
            seq: 1, numAdc: 1, sampleRateKhz: 48, psEnabled: false,
            boardKind: HpsdrBoardKind.HermesII);

        Assert.Equal((byte)0x00, p[17]);          // ADC0
        Assert.Equal((byte)0x00, p[18]);          // 48 kHz BE high
        Assert.Equal((byte)48,   p[19]);          // 48 kHz BE low
        Assert.Equal((byte)24,   p[22]);          // 24-bit
        Assert.Equal((byte)0x00, p[29]);          // DDC2 block stays zero
        Assert.Equal((byte)0x00, p[31]);
        Assert.Equal((byte)0x00, p[34]);
    }

    [Fact]
    public void CmdRx_HermesII_Does_Not_Set_DDC1_Sync_Or_PS_Block()
    {
        // Same single-ADC no-PS-hardware story as Hermes: psEnabled=true is a
        // no-op on HermesII so we don't scribble OrionMkII shape onto the wire.
        var p = Protocol2Client.ComposeCmdRxBuffer(
            seq: 1, numAdc: 1, sampleRateKhz: 96, psEnabled: true,
            boardKind: HpsdrBoardKind.HermesII);

        Assert.Equal((byte)0x00, p[1363]);
        Assert.Equal((byte)0x01, p[7]);
        Assert.Equal((byte)0x00, p[23]);
        Assert.Equal((byte)0x00, p[28]);
    }

    [Fact]
    public void CmdRx_DefaultBoardKind_Preserves_OrionMkII_WireShape()
    {
        // The 4-arg overload (no boardKind) was the entire P2 surface before
        // this change. Calling the 5-arg form WITHOUT specifying boardKind
        // must produce byte-identical output to the legacy shape. This is
        // what protects every existing G2 / 7000DLE / 8000DLE / Saturn
        // operator on the next release.
        var withDefault = Protocol2Client.ComposeCmdRxBuffer(
            seq: 42, numAdc: 2, sampleRateKhz: 192, psEnabled: false);
        var explicitG2 = Protocol2Client.ComposeCmdRxBuffer(
            seq: 42, numAdc: 2, sampleRateKhz: 192, psEnabled: false,
            boardKind: HpsdrBoardKind.OrionMkII);
        Assert.Equal(withDefault, explicitG2);

        // And the legacy single bit lights up DDC2.
        Assert.Equal((byte)0x04, withDefault[7]);
    }
}
