// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server;

namespace Zeus.Server.Tests;

// Per-radio frequency calibration (issue #325) write-through + clamp
// behaviour. Pins:
//
//   1. RadioService.SetFrequencyCorrectionFactor persists through the
//      live store and is visible via Get on the next call.
//   2. The factor survives a "process restart" — a second RadioService
//      built on the same on-disk store sees the same value.
//   3. Out-of-range values are clamped to ±100 ppm (factor ∈
//      [0.9999, 1.0001]) instead of being persisted verbatim. Anything
//      wider than this is almost certainly an operator mistake, not a
//      real crystal-drift correction.
//   4. NaN / Infinity throws (defensive — REST validators should reject
//      these first, but a malformed call must not corrupt the store).
public class RadioServiceFrequencyCorrectionTests : IDisposable
{
    private readonly string _dbPath;

    public RadioServiceFrequencyCorrectionTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zeus-prefs-freqcal-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (File.Exists(_dbPath + ".pa")) File.Delete(_dbPath + ".pa"); } catch { }
        try { if (File.Exists(_dbPath + ".dsp")) File.Delete(_dbPath + ".dsp"); } catch { }
    }

    private PaSettingsStore NewPaStore() =>
        new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath + ".pa");

    private DspSettingsStore NewDspStore() =>
        new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _dbPath + ".dsp");

    private RadioService BuildRadio(PreferredRadioStore prefs) =>
        new RadioService(
            NullLoggerFactory.Instance,
            NewDspStore(),
            NewPaStore(),
            preferredRadioStore: prefs);

    [Fact]
    public void Default_Is_One()
    {
        using var prefs = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath);
        using var radio = BuildRadio(prefs);
        Assert.Equal(1.0, radio.GetFrequencyCorrectionFactor());
    }

    [Fact]
    public void Set_Round_Trips_Through_Service()
    {
        using var prefs = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath);
        using var radio = BuildRadio(prefs);

        var applied = radio.SetFrequencyCorrectionFactor(1.000001);
        Assert.Equal(1.000001, applied, precision: 9);
        Assert.Equal(1.000001, radio.GetFrequencyCorrectionFactor(), precision: 9);
        Assert.Equal(1.000001, prefs.GetFrequencyCorrectionFactor(), precision: 9);
    }

    [Fact]
    public void Persists_Across_Simulated_Reconnect()
    {
        using (var prefs = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath))
        using (var radio = BuildRadio(prefs))
        {
            radio.SetFrequencyCorrectionFactor(0.999995);
        }

        using var prefs2 = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath);
        using var radio2 = BuildRadio(prefs2);
        Assert.Equal(0.999995, radio2.GetFrequencyCorrectionFactor(), precision: 9);
    }

    [Theory]
    [InlineData(1.5, 1.0001)]    // way too high → clamp to +100 ppm
    [InlineData(0.5, 0.9999)]    // way too low  → clamp to -100 ppm
    [InlineData(2.0, 1.0001)]
    [InlineData(-1.0, 0.9999)]
    public void Out_Of_Range_Factor_Is_Clamped(double input, double expected)
    {
        using var prefs = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath);
        using var radio = BuildRadio(prefs);

        var applied = radio.SetFrequencyCorrectionFactor(input);
        Assert.Equal(expected, applied, precision: 9);
        Assert.Equal(expected, radio.GetFrequencyCorrectionFactor(), precision: 9);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Non_Finite_Factor_Throws(double input)
    {
        using var prefs = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath);
        using var radio = BuildRadio(prefs);

        Assert.Throws<ArgumentException>(() => radio.SetFrequencyCorrectionFactor(input));
    }

    [Fact]
    public void In_Range_Factors_Are_Preserved_Verbatim()
    {
        using var prefs = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath);
        using var radio = BuildRadio(prefs);

        // Boundary: exactly ±100 ppm should pass through unchanged.
        Assert.Equal(1.0001, radio.SetFrequencyCorrectionFactor(1.0001), precision: 9);
        Assert.Equal(0.9999, radio.SetFrequencyCorrectionFactor(0.9999), precision: 9);

        // Realistic crystal drift: ~5 ppm.
        Assert.Equal(1.000005, radio.SetFrequencyCorrectionFactor(1.000005), precision: 9);
    }
}
