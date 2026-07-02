// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class RfFilterSettingsStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(),
        $"zeus-prefs-rf-filters-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public void Defaults_Are_Stock_Auto_And_BoardAware()
    {
        using var store = NewStore();
        var dto = store.GetDto(HpsdrBoardKind.OrionMkII, State(), txActive: false, psEnabled: false);

        Assert.False(dto.CustomMatrixEnabled);
        Assert.Equal("anan-7000", dto.ActiveProfileKey);
        Assert.Equal("20 / 15 m", dto.Active.Rx1Label);
        Assert.Equal("30 / 20 m LPF", dto.Active.TxLabel);
        Assert.Empty(dto.Warnings);
    }

    [Fact]
    public void Defaults_Match_Thetis_Alex_Filter_Windows()
    {
        using var store = NewStore();
        var dto = store.GetDto(HpsdrBoardKind.OrionMkII, State(), txActive: false, psEnabled: false);

        var anan = dto.Profiles.First(p => p.Key == "anan-7000");
        AssertRange(anan.RxFilters, "160", 1_500_000, 2_099_999);
        AssertRange(anan.RxFilters, "80_60", 2_100_000, 5_499_999);
        AssertRange(anan.RxFilters, "40_30", 5_500_000, 10_999_999);
        AssertRange(anan.RxFilters, "20_15", 11_000_000, 21_999_999);
        AssertRange(anan.RxFilters, "12_10", 22_000_000, 34_999_999);
        AssertRange(anan.RxFilters, "6_pre", 35_000_000, 61_440_000);

        var classic = dto.Profiles.First(p => p.Key == "classic-alex");
        AssertRange(classic.RxFilters, "1_5", 1_800_000, 6_499_999);
        AssertRange(classic.RxFilters, "6_5", 6_500_000, 9_499_999);
        AssertRange(classic.RxFilters, "9_5", 9_500_000, 12_999_999);
        AssertRange(classic.RxFilters, "13", 13_000_000, 19_999_999);
        AssertRange(classic.RxFilters, "20", 20_000_000, 49_999_999);
        AssertRange(classic.RxFilters, "6_pre", 50_000_000, 61_440_000);

        AssertRange(anan.TxFilters, "160", 0, 2_500_000);
        AssertRange(anan.TxFilters, "80", 2_500_001, 5_000_000);
        AssertRange(anan.TxFilters, "60_40", 5_000_001, 8_000_000);
        AssertRange(anan.TxFilters, "30_20", 8_000_001, 16_500_000);
        AssertRange(anan.TxFilters, "17_15", 16_500_001, 24_000_000);
        AssertRange(anan.TxFilters, "12_10", 24_000_001, 35_600_000);
        AssertRange(anan.TxFilters, "6_bypass", 35_600_001, 61_440_000);
    }

    [Fact]
    public void Manual_Row_RoundTrips_And_Normalizes()
    {
        using (var store = NewStore())
        {
            var dto = store.GetDto(HpsdrBoardKind.OrionMkII, State(), txActive: false, psEnabled: false);
            var anan = dto.Profiles.First(p => p.Key == "anan-7000");
            var patched = anan with
            {
                RxFilters = anan.RxFilters
                    .Select(r => r.Key == "40_30"
                        ? r with { StartHz = 6_000_000, EndHz = 12_000_000, ForceBypass = true }
                        : r)
                    .ToArray(),
            };
            store.Set(new RfFilterSettingsSetRequest(
                    CustomMatrixEnabled: true,
                    RxBypassAll: false,
                    RxBypassOnTx: false,
                    RxBypassOnPureSignal: false,
                    Profiles: dto.Profiles.Select(p => p.Key == patched.Key ? patched : p).ToArray()),
                HpsdrBoardKind.OrionMkII,
                State(),
                txActive: false,
                psEnabled: false);
        }

        using var reopened = NewStore();
        var runtime = reopened.GetRuntime(HpsdrBoardKind.OrionMkII);
        var row = runtime.Anan7000RxFilters.First(r => r.Key == "40_30");

        Assert.True(runtime.CustomMatrixEnabled);
        Assert.Equal(6_000_000, row.StartHz);
        Assert.Equal(12_000_000, row.EndHz);
        Assert.True(row.ForceBypass);
    }

    [Fact]
    public void Reset_Restores_Stock_Auto()
    {
        using var store = NewStore();
        var dto = store.GetDto(HpsdrBoardKind.OrionMkII, State(), txActive: false, psEnabled: false);
        store.Set(new RfFilterSettingsSetRequest(
                CustomMatrixEnabled: true,
                RxBypassAll: true,
                RxBypassOnTx: false,
                RxBypassOnPureSignal: false,
                Profiles: dto.Profiles),
            HpsdrBoardKind.OrionMkII,
            State(),
            txActive: false,
            psEnabled: false);

        var reset = store.Reset(HpsdrBoardKind.OrionMkII, State(), txActive: false, psEnabled: false);

        Assert.False(reset.CustomMatrixEnabled);
        Assert.False(reset.RxBypassAll);
    }

    private RfFilterSettingsStore NewStore() =>
        new(NullLogger<RfFilterSettingsStore>.Instance, _dbPath);

    private static void AssertRange(
        IReadOnlyList<RfFilterRangeDto> rows,
        string key,
        long startHz,
        long endHz)
    {
        var row = rows.First(r => r.Key == key);
        Assert.Equal(startHz, row.StartHz);
        Assert.Equal(endHz, row.EndHz);
    }

    private static StateDto State() => new(
        Status: ConnectionStatus.Disconnected,
        Endpoint: null,
        VfoHz: 14_200_000,
        Mode: RxMode.USB,
        FilterLowHz: 100,
        FilterHighHz: 2850,
        SampleRate: 192_000);
}
