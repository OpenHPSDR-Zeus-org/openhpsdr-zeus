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

    private static StateDto State() => new(
        Status: ConnectionStatus.Disconnected,
        Endpoint: null,
        VfoHz: 14_200_000,
        Mode: RxMode.USB,
        FilterLowHz: 100,
        FilterHighHz: 2850,
        SampleRate: 192_000);
}
