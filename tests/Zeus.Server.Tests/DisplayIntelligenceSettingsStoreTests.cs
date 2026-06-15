// SPDX-License-Identifier: GPL-2.0-or-later
//
// Persistence coverage for Signal Intelligence weak-signal display policy.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class DisplayIntelligenceSettingsStoreTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-display-intel-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private DisplayIntelligenceSettingsStore NewStore() =>
        new(NullLogger<DisplayIntelligenceSettingsStore>.Instance, _dbPath);

    [Fact]
    public void FirstRun_ReturnsBalancedWeakSignalDefaults()
    {
        using var store = NewStore();

        var settings = store.Get();

        Assert.Equal("balanced", settings.ProfileId);
        Assert.False(settings.PopEnabled);
        Assert.False(settings.SnapEnabled);
        Assert.True(settings.VisualAgcEnabled);
        Assert.True(settings.ImpulseRejectEnabled);
        Assert.Equal(30.0, settings.PopSpanDb);
        Assert.Equal(4000, settings.SnapRadiusHz);
    }

    [Fact]
    public void Save_NormalizesAndPersists()
    {
        using (var first = NewStore())
        {
            var saved = first.Save(new DisplayIntelligenceSettingsDto(
                ProfileId: " DX ",
                PopEnabled: true,
                SnapEnabled: true,
                AutoProfileEnabled: true,
                VisualAgcEnabled: false,
                ImpulseRejectEnabled: false,
                PopFloorDb: 2.0,
                PopSpanDb: 24.0,
                PopGamma: 0.42,
                PopRenderIntensity: 92,
                CoherenceHoldGate: 0.38,
                CoherenceBoostDb: 5.5,
                RidgeBoost: 0.5,
                RidgeMaxBoostDb: 10.0,
                VisualAgcStrength: 70,
                ImpulseRejectDb: 16,
                SnapRadiusHz: 5000,
                SnapMinSnrDb: 5.0,
                PeakMinSnrDb: 7.0));

            Assert.Equal("dx", saved.ProfileId);
            Assert.True(saved.PopEnabled);
            Assert.Equal(92, saved.PopRenderIntensity);
        }

        using var second = NewStore();
        var settings = second.Get();

        Assert.Equal("dx", settings.ProfileId);
        Assert.True(settings.PopEnabled);
        Assert.True(settings.SnapEnabled);
        Assert.False(settings.VisualAgcEnabled);
        Assert.Equal(24.0, settings.PopSpanDb);
        Assert.Equal(5000, settings.SnapRadiusHz);
    }
}
