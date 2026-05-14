// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.

using Xunit;
using Zeus.Contracts;
using Zeus.Protocol1.Discovery;
using Zeus.Server;

namespace Zeus.Server.Tests;

/// <summary>
/// <see cref="BoardCapabilitiesTable.For"/> dispatches every recognised
/// <see cref="HpsdrBoardKind"/> to the static fingerprint Thetis records
/// in <c>clsHardwareSpecific.cs</c>. Pinned per-board so a regression in
/// the table cannot silently flip a feature panel on or off.
///
/// Source-of-truth: <c>docs/references/protocol-1/thetis-board-matrix.md</c>.
/// </summary>
public class BoardCapabilitiesTableTests
{
    [Fact]
    public void Hermes_Class_Single_RX_LRSwap_PathIllustrator_On()
    {
        // Thetis clsHardwareSpecific.cs:87-121 — HERMES, ANAN-10, ANAN-10E,
        // ANAN-100, ANAN-100B all share these facts.
        foreach (var board in new[] {
            HpsdrBoardKind.Metis,
            HpsdrBoardKind.Hermes,
            HpsdrBoardKind.HermesII,
        })
        {
            var caps = BoardCapabilitiesTable.For(board);
            Assert.Equal(1, caps.RxAdcCount);
            Assert.False(caps.MkiiBpf);
            Assert.Equal(33, caps.AdcSupplyMv);
            Assert.True(caps.LrAudioSwap);
            Assert.False(caps.HasVolts);
            Assert.False(caps.HasAmps);
            Assert.False(caps.HasAudioAmplifier);
            Assert.True(caps.SupportsPathIllustrator);
        }
    }

    [Fact]
    public void Angelia_DualAdc_HermesSupply_NoMkii()
    {
        // Thetis clsHardwareSpecific.cs:122-128 — ANAN-100D was the first
        // dual-ADC board but kept the 33 mV / no-MKII Hermes-class supply.
        var caps = BoardCapabilitiesTable.For(HpsdrBoardKind.Angelia);
        Assert.Equal(2, caps.RxAdcCount);
        Assert.False(caps.MkiiBpf);
        Assert.Equal(33, caps.AdcSupplyMv);
        Assert.False(caps.LrAudioSwap);
        Assert.True(caps.HasSteppedAttenuationRx2);
        Assert.True(caps.SupportsPathIllustrator);
    }

    [Fact]
    public void Orion_DualAdc_HighPowerSupply_NoMkii()
    {
        // Thetis clsHardwareSpecific.cs:136-142 — ANAN-200D first 50 mV
        // board, still no MKII BPF.
        var caps = BoardCapabilitiesTable.For(HpsdrBoardKind.Orion);
        Assert.Equal(2, caps.RxAdcCount);
        Assert.False(caps.MkiiBpf);
        Assert.Equal(50, caps.AdcSupplyMv);
        Assert.True(caps.HasSteppedAttenuationRx2);
        Assert.True(caps.SupportsPathIllustrator);
    }

    [Fact]
    public void OrionMkII_Saturn_Class_Defaults_Match_G2()
    {
        // 0x0A wire byte aliases G2 / 7000DLE / 8000DLE / G2-1K /
        // ANVELINA-PRO3 / Red Pitaya. Default fingerprint = G2-class.
        // Issue #218 will fan this out per operator-selected variant.
        var caps = BoardCapabilitiesTable.For(HpsdrBoardKind.OrionMkII);
        Assert.Equal(2, caps.RxAdcCount);
        Assert.True(caps.MkiiBpf);
        Assert.Equal(50, caps.AdcSupplyMv);
        Assert.True(caps.HasVolts);
        Assert.True(caps.HasAmps);
        Assert.True(caps.HasAudioAmplifier);
        Assert.True(caps.HasSteppedAttenuationRx2);
        Assert.False(caps.SupportsPathIllustrator);
    }

    [Fact]
    public void HermesC10_G2E_Hybrid_SingleRx_MkiiOn()
    {
        // ANAN-G2E (N1GP firmware) — Thetis clsHardwareSpecific.cs:129-135.
        // Hybrid: single RX + 33 mV supply (Hermes-class) but MKII BPF on
        // and Saturn-class telemetry / audio amp.
        var caps = BoardCapabilitiesTable.For(HpsdrBoardKind.HermesC10);
        Assert.Equal(1, caps.RxAdcCount);
        Assert.True(caps.MkiiBpf);
        Assert.Equal(33, caps.AdcSupplyMv);
        Assert.False(caps.LrAudioSwap);
        Assert.True(caps.HasVolts);
        Assert.True(caps.HasAmps);
        Assert.True(caps.HasAudioAmplifier);
        Assert.False(caps.HasSteppedAttenuationRx2); // single RX
        Assert.False(caps.SupportsPathIllustrator);
    }

    [Fact]
    public void HermesLite2_Defaults_Are_Conservative()
    {
        // HL2 is mi0bot-territory in Zeus; no Alex, no telemetry, no
        // path illustrator. Single-RX so RX2 attenuation flag is moot.
        var caps = BoardCapabilitiesTable.For(HpsdrBoardKind.HermesLite2);
        Assert.Equal(1, caps.RxAdcCount);
        Assert.False(caps.MkiiBpf);
        Assert.False(caps.HasVolts);
        Assert.False(caps.HasAmps);
        Assert.False(caps.HasAudioAmplifier);
        Assert.False(caps.SupportsPathIllustrator);
    }

    [Fact]
    public void HermesLite2_Exposes_Hl2OptionalToggles()
    {
        // Issue #279: HL2-only optional toggles (Band Volts PWM, future
        // mi0bot HL2 fields) are gated by this flag so the frontend
        // doesn't render the panel for boards that ignore them.
        var caps = BoardCapabilitiesTable.For(HpsdrBoardKind.HermesLite2);
        Assert.True(caps.HasHl2OptionalToggles);
    }

    [Theory]
    [InlineData(HpsdrBoardKind.Metis)]
    [InlineData(HpsdrBoardKind.Hermes)]
    [InlineData(HpsdrBoardKind.HermesII)]
    [InlineData(HpsdrBoardKind.Angelia)]
    [InlineData(HpsdrBoardKind.Orion)]
    [InlineData(HpsdrBoardKind.OrionMkII)]
    [InlineData(HpsdrBoardKind.HermesC10)]
    [InlineData(HpsdrBoardKind.Unknown)]
    public void NonHl2_Boards_Do_Not_Expose_Hl2OptionalToggles(HpsdrBoardKind board)
    {
        // Pin the inverse: every non-HL2 board hides the HL2 panel.
        // Issue #279.
        var caps = BoardCapabilitiesTable.For(board);
        Assert.False(caps.HasHl2OptionalToggles);
    }

    [Fact]
    public void Unknown_FallsBackTo_UnknownDefaults()
    {
        var caps = BoardCapabilitiesTable.For(HpsdrBoardKind.Unknown);
        Assert.Same(BoardCapabilities.UnknownDefaults, caps);
    }

    [Theory]
    [MemberData(nameof(EveryBoardKind))]
    public void Every_BoardKind_Returns_Sane_Fingerprint(HpsdrBoardKind board)
    {
        // Exhaustiveness pin: every enum value gets a defined fingerprint
        // (no surprise nulls), with sensible bounds for the numeric fields.
        var caps = BoardCapabilitiesTable.For(board);
        Assert.NotNull(caps);
        Assert.InRange(caps.RxAdcCount, 1, 2);
        Assert.True(caps.AdcSupplyMv == 33 || caps.AdcSupplyMv == 50,
            $"{board} has unexpected ADC supply {caps.AdcSupplyMv} mV");
        Assert.InRange(caps.MaxPowerWatts, 1, 2000);
    }

    [Theory]
    [InlineData(HpsdrBoardKind.HermesLite2, 10)]
    [InlineData(HpsdrBoardKind.Metis, 10)]
    [InlineData(HpsdrBoardKind.Hermes, 10)]
    [InlineData(HpsdrBoardKind.HermesII, 30)]
    [InlineData(HpsdrBoardKind.Angelia, 120)]
    [InlineData(HpsdrBoardKind.Orion, 120)]
    [InlineData(HpsdrBoardKind.HermesC10, 120)]
    public void MaxPowerWatts_Per_Board_Defaults(HpsdrBoardKind board, int expectedW)
    {
        // Pin the per-board axis-top so the TX power meter renders with a
        // sensible scale on first connect. HL2 = 10 W, ANAN-10 = 10 W,
        // 10E = 30 W, 100/200/G2E = 120 W (variant fan-out covered below).
        var caps = BoardCapabilitiesTable.For(board);
        Assert.Equal(expectedW, caps.MaxPowerWatts);
    }

    [Theory]
    [InlineData(OrionMkIIVariant.G2, 120)]
    [InlineData(OrionMkIIVariant.G2_1K, 1000)]
    [InlineData(OrionMkIIVariant.Anan7000DLE, 120)]
    [InlineData(OrionMkIIVariant.Anan8000DLE, 250)]
    [InlineData(OrionMkIIVariant.OrionMkII, 120)]
    [InlineData(OrionMkIIVariant.AnvelinaPro3, 120)]
    [InlineData(OrionMkIIVariant.RedPitaya, 120)]
    public void MaxPowerWatts_OrionMkII_Variant_FanOut(OrionMkIIVariant variant, int expectedW)
    {
        // 0x0A wire byte aliases — variant disambiguates 8000DLE (250 W) and
        // G2-1K (1 kW) from the rest of the Saturn family (120 W).
        var caps = BoardCapabilitiesTable.For(HpsdrBoardKind.OrionMkII, variant);
        Assert.Equal(expectedW, caps.MaxPowerWatts);
    }

    public static IEnumerable<object[]> EveryBoardKind() =>
        Enum.GetValues<HpsdrBoardKind>().Select(b => new object[] { b });
}
