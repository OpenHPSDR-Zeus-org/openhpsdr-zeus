// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using Xunit;
using Zeus.Contracts;
using Zeus.Protocol1.Discovery;
using Zeus.Server;

namespace Zeus.Server.Tests;

/// <summary>
/// <see cref="RadioCalibrations.For"/> must dispatch each connected board
/// kind to the right calibration record. Constants come from Thetis
/// <c>console.cs:25053-25118</c> (computeAlexFwdPower); regressing the
/// dispatch silently mis-scales the operator's TX power meter.
///
/// Issue #174 — added when P2 hi-pri telemetry was wired into the meter
/// pipeline; before that point the meter ignored everything except HL2.
/// </summary>
public class RadioCalibrationsDispatchTests
{
    [Fact]
    public void HermesLite2_GetsHl2Bridge()
    {
        var cal = RadioCalibrations.For(HpsdrBoardKind.HermesLite2);
        Assert.Same(RadioCalibration.HermesLite2, cal);
        Assert.Equal(1.5, cal.BridgeVolt);
    }

    [Fact]
    public void Hermes_Metis_Griffin_GetsHermesBridge()
    {
        Assert.Same(RadioCalibration.Hermes, RadioCalibrations.For(HpsdrBoardKind.Hermes));
        Assert.Same(RadioCalibration.Hermes, RadioCalibrations.For(HpsdrBoardKind.Metis));
        Assert.Same(RadioCalibration.Hermes, RadioCalibrations.For(HpsdrBoardKind.HermesII));
    }

    [Fact]
    public void Angelia_GetsAnan100Bridge()
    {
        var cal = RadioCalibrations.For(HpsdrBoardKind.Angelia);
        Assert.Same(RadioCalibration.Anan100, cal);
        Assert.Equal(0.095, cal.BridgeVolt);
        Assert.Equal(3.3, cal.RefVoltage);
        Assert.Equal(6, cal.AdcCalOffset);
    }

    [Fact]
    public void Orion_GetsAnan200Bridge()
    {
        var cal = RadioCalibrations.For(HpsdrBoardKind.Orion);
        Assert.Same(RadioCalibration.Anan200, cal);
        Assert.Equal(0.108, cal.BridgeVolt);
        Assert.Equal(5.0, cal.RefVoltage);
        Assert.Equal(4, cal.AdcCalOffset);
    }

    [Fact]
    public void OrionMkII_GetsG2Bridge_NotAnan8000Bridge()
    {
        // Board id 0x0A aliases ANAN-7000 / G1 / G2 / G2-1K / RedPitaya
        // (Thetis ANAN_G2 — bridge 0.12 / ref 5.0 / offset 32) and
        // ANAN-8000D (Thetis ORIONMKII — bridge 0.08 / ref 5.0 / offset 18).
        // The default dispatch picks the G2 bucket because that is what
        // KB2UKA's test rig reports. ANAN-8000D operators may see a
        // ~30 % low FWD reading — see RadioCalibration.OrionMkIIAnan8000.
        var cal = RadioCalibrations.For(HpsdrBoardKind.OrionMkII);
        Assert.Same(RadioCalibration.OrionMkII, cal);
        Assert.Equal(0.12, cal.BridgeVolt);
        Assert.Equal(5.0, cal.RefVoltage);
        Assert.Equal(32, cal.AdcCalOffset);
    }

    [Fact]
    public void HermesC10_GetsG2Bridge()
    {
        // ANAN-G2E (Thetis HPSDRHW.HermesC10) shares the OrionMkII / G2
        // forward-power calibration constants per Thetis console.cs:25079-25088
        // (computeAlexFwdPower groups ANAN_G2E with ANAN_G2 / ANAN_G2_1K /
        // ANAN7000D / ANVELINAPRO3 / REDPITAYA at bridge 0.12 / ref 5.0 /
        // offset 32).
        var cal = RadioCalibrations.For(HpsdrBoardKind.HermesC10);
        Assert.Same(RadioCalibration.OrionMkII, cal);
        Assert.Equal(0.12, cal.BridgeVolt);
        Assert.Equal(5.0, cal.RefVoltage);
        Assert.Equal(32, cal.AdcCalOffset);
    }

    [Fact]
    public void Unknown_FallsBackToHl2_NotZero()
    {
        // A divide-by-zero in ComputeMeters would surface as Infinity / NaN
        // on the meter — fall back to a sane bucket instead.
        var cal = RadioCalibrations.For(HpsdrBoardKind.Unknown);
        Assert.True(cal.BridgeVolt > 0);
        Assert.True(cal.RefVoltage > 0);
    }

    [Fact]
    public void OrionMkII_Variant_G2_Default_Matches_PreIssue218_Behaviour()
    {
        // Default variant (G2) must dispatch identically to the no-variant
        // overload — operators who never touch the setting see no change.
        var defaultCal = RadioCalibrations.For(HpsdrBoardKind.OrionMkII);
        var explicitG2 = RadioCalibrations.For(HpsdrBoardKind.OrionMkII, OrionMkIIVariant.G2);
        Assert.Same(defaultCal, explicitG2);
        Assert.Same(RadioCalibration.OrionMkII, explicitG2);
    }

    [Fact]
    public void OrionMkII_Variant_Anan8000DLE_Picks_AltBridge()
    {
        // Issue #218: 8000DLE has bridge 0.08 / ref 5.0 / offset 18 per
        // Thetis console.cs:25089-25093. Selecting the variant must route
        // through the OrionMkIIAnan8000 bucket so 8000D meters read
        // correctly (was ~30 % low under G2 dispatch).
        var cal = RadioCalibrations.For(HpsdrBoardKind.OrionMkII, OrionMkIIVariant.Anan8000DLE);
        Assert.Same(RadioCalibration.OrionMkIIAnan8000, cal);
        Assert.Equal(0.08, cal.BridgeVolt);
        Assert.Equal(18, cal.AdcCalOffset);
        Assert.Equal(200.0, cal.MaxWatts);
    }

    [Fact]
    public void OrionMkII_Variant_OrionMkIIOriginal_Picks_AltBridge_100W()
    {
        // Apache OrionMkII (original, not the umbrella term) shares 8000D's
        // bridge but is 100 W rated.
        var cal = RadioCalibrations.For(HpsdrBoardKind.OrionMkII, OrionMkIIVariant.OrionMkII);
        Assert.Same(RadioCalibration.OrionMkIIOriginal, cal);
        Assert.Equal(0.08, cal.BridgeVolt);
        Assert.Equal(100.0, cal.MaxWatts);
    }

    [Fact]
    public void OrionMkII_Variant_G2_1K_Same_Bridge_DifferentMaxWatts()
    {
        // G2-1K shares G2's bridge constants (G8NJJ noted 1K may need
        // different scaling but Thetis ships G2 numbers). MaxWatts = 1000.
        var cal = RadioCalibrations.For(HpsdrBoardKind.OrionMkII, OrionMkIIVariant.G2_1K);
        Assert.Same(RadioCalibration.AnanG21K, cal);
        Assert.Equal(0.12, cal.BridgeVolt);
        Assert.Equal(1000.0, cal.MaxWatts);
    }

    [Theory]
    [InlineData(OrionMkIIVariant.Anan7000DLE)]
    [InlineData(OrionMkIIVariant.AnvelinaPro3)]
    [InlineData(OrionMkIIVariant.RedPitaya)]
    public void OrionMkII_OtherVariants_Inherit_G2_Bridge(OrionMkIIVariant variant)
    {
        // 7000DLE / ANVELINA-PRO3 / Red Pitaya all share G2's bridge
        // constants per Thetis console.cs:25079-25088.
        var cal = RadioCalibrations.For(HpsdrBoardKind.OrionMkII, variant);
        Assert.Same(RadioCalibration.OrionMkII, cal);
    }

    [Theory]
    [MemberData(nameof(EveryBoardKindWithEveryVariant))]
    public void Variant_Ignored_For_NonOrionMkII_Boards(HpsdrBoardKind board, OrionMkIIVariant variant)
    {
        // Variant only matters for the 0x0A wire-byte alias family. Every
        // other board must dispatch identically regardless of variant.
        if (board == HpsdrBoardKind.OrionMkII) return;
        var defaultCal = RadioCalibrations.For(board);
        var withVariant = RadioCalibrations.For(board, variant);
        Assert.Same(defaultCal, withVariant);
    }

    public static IEnumerable<object[]> EveryBoardKindWithEveryVariant() =>
        from board in Enum.GetValues<HpsdrBoardKind>()
        from variant in Enum.GetValues<OrionMkIIVariant>()
        select new object[] { board, variant };

    [Theory]
    [MemberData(nameof(EveryBoardKind))]
    public void Every_BoardKind_Dispatches_To_NonDegenerate_Calibration(HpsdrBoardKind board)
    {
        // Exhaustiveness pin: every enum value must dispatch to a bucket
        // whose constants cannot drive ComputeMeters into Infinity / NaN.
        // Adding a new HpsdrBoardKind value without extending For() will
        // fall through to HermesLite2 (sane fallback), so this test
        // verifies the safety net rather than enforcing per-board correctness
        // (the named tests above do that).
        var cal = RadioCalibrations.For(board);
        Assert.True(cal.BridgeVolt > 0, $"{board} dispatched to a zero-bridge calibration");
        Assert.True(cal.RefVoltage > 0, $"{board} dispatched to a zero-ref calibration");
        Assert.True(cal.MaxWatts > 0, $"{board} dispatched to a zero-max-watts calibration");
    }

    public static IEnumerable<object[]> EveryBoardKind() =>
        Enum.GetValues<HpsdrBoardKind>().Select(b => new object[] { b });

    [Theory]
    [InlineData(HpsdrBoardKind.HermesLite2, OrionMkIIVariant.G2, 0.98)]
    [InlineData(HpsdrBoardKind.Hermes, OrionMkIIVariant.G2, 0.98)]
    [InlineData(HpsdrBoardKind.Metis, OrionMkIIVariant.G2, 0.98)]
    [InlineData(HpsdrBoardKind.HermesII, OrionMkIIVariant.G2, 0.98)]
    [InlineData(HpsdrBoardKind.Angelia, OrionMkIIVariant.G2, 0.98)]
    [InlineData(HpsdrBoardKind.Orion, OrionMkIIVariant.G2, 0.98)]
    [InlineData(HpsdrBoardKind.HermesC10, OrionMkIIVariant.G2, 0.98)]
    [InlineData(HpsdrBoardKind.Unknown, OrionMkIIVariant.G2, 0.98)]
    [InlineData(HpsdrBoardKind.OrionMkII, OrionMkIIVariant.G2, -4.476)]
    [InlineData(HpsdrBoardKind.OrionMkII, OrionMkIIVariant.G2_1K, -4.476)]
    [InlineData(HpsdrBoardKind.OrionMkII, OrionMkIIVariant.Anan7000DLE, 4.841644)]
    [InlineData(HpsdrBoardKind.OrionMkII, OrionMkIIVariant.Anan8000DLE, 4.841644)]
    [InlineData(HpsdrBoardKind.OrionMkII, OrionMkIIVariant.OrionMkII, 4.841644)]
    [InlineData(HpsdrBoardKind.OrionMkII, OrionMkIIVariant.AnvelinaPro3, 4.841644)]
    [InlineData(HpsdrBoardKind.OrionMkII, OrionMkIIVariant.RedPitaya, 4.841644)]
    public void RxMeterOffsetDb_Dispatches_Per_Thetis_Defaults(
        HpsdrBoardKind board,
        OrionMkIIVariant variant,
        double expectedOffsetDb)
    {
        Assert.Equal(expectedOffsetDb, RadioCalibrations.RxMeterOffsetDb(board, variant), precision: 6);
    }

    [Fact]
    public void RxMeterOffsetDb_Default_OrionMkII_Uses_G2_Default()
    {
        Assert.Equal(
            RadioCalibrations.RxMeterOffsetDb(HpsdrBoardKind.OrionMkII, OrionMkIIVariant.G2),
            RadioCalibrations.RxMeterOffsetDb(HpsdrBoardKind.OrionMkII));
    }
}
