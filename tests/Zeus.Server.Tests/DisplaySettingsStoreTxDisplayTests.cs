// SPDX-License-Identifier: GPL-2.0-or-later
//
// DisplaySettingsStore — TX display analyzer params (live TX waterfall).
// Pins: null-on-first-run (engine falls back to defaults), round-trip of valid
// values, per-field independence (updating one knob keeps the others), and
// validation (out-of-range / NaN / non-power-of-two are dropped, not persisted).
// These are display-only params; the safety value here is "a malformed PUT
// can't poison zeus-prefs.db or push garbage into the WDSP analyzer reconfig".

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class DisplaySettingsStoreTxDisplayTests : IDisposable
{
    private readonly string _dbPath;

    public DisplaySettingsStoreTxDisplayTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zeus-prefs-txdisplay-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private DisplaySettingsStore NewStore() =>
        new DisplaySettingsStore(NullLogger<DisplaySettingsStore>.Instance, _dbPath);

    private void Save(DisplaySettingsStore store,
        double? cal = null, int? fft = null, int? win = null, double? tau = null) =>
        store.SaveMode("basic", "fill", "#FFA028",
            txDisplayCalOffsetDb: cal, txDisplayFftSize: fft,
            txDisplayWindow: win, txDisplayAvgTauMs: tau);

    [Fact]
    public void FirstRun_TxDisplayParamsAreNull()
    {
        using var store = NewStore();
        var dto = store.Get();
        Assert.Null(dto.TxDisplayCalOffsetDb);
        Assert.Null(dto.TxDisplayFftSize);
        Assert.Null(dto.TxDisplayWindow);
        Assert.Null(dto.TxDisplayAvgTauMs);
    }

    [Fact]
    public void ValidValues_RoundTrip()
    {
        using var store = NewStore();
        Save(store, cal: -12.5, fft: 32768, win: 1, tau: 250.0);
        var dto = store.Get();
        Assert.Equal(-12.5, dto.TxDisplayCalOffsetDb);
        Assert.Equal(32768, dto.TxDisplayFftSize);
        Assert.Equal(1, dto.TxDisplayWindow);
        Assert.Equal(250.0, dto.TxDisplayAvgTauMs);
    }

    [Fact]
    public void RoundTrip_SurvivesReopen()
    {
        using (var store = NewStore())
            Save(store, cal: 6.0, fft: 8192, win: 5, tau: 100.0);
        using var reopened = NewStore();
        var dto = reopened.Get();
        Assert.Equal(6.0, dto.TxDisplayCalOffsetDb);
        Assert.Equal(8192, dto.TxDisplayFftSize);
        Assert.Equal(5, dto.TxDisplayWindow);
        Assert.Equal(100.0, dto.TxDisplayAvgTauMs);
    }

    [Fact]
    public void PartialUpdate_LeavesOtherFieldsUntouched()
    {
        using var store = NewStore();
        Save(store, cal: -10.0, fft: 16384, win: 2, tau: 175.0);
        // Update only the cal offset; everything else must persist.
        Save(store, cal: 3.0);
        var dto = store.Get();
        Assert.Equal(3.0, dto.TxDisplayCalOffsetDb);
        Assert.Equal(16384, dto.TxDisplayFftSize);
        Assert.Equal(2, dto.TxDisplayWindow);
        Assert.Equal(175.0, dto.TxDisplayAvgTauMs);
    }

    [Theory]
    [InlineData(200.0)]   // beyond ±60 dB
    [InlineData(-200.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidCalOffset_IsDropped(double bad)
    {
        using var store = NewStore();
        Save(store, cal: -8.0);            // seed a good value
        Save(store, cal: bad);             // bad write must be ignored
        Assert.Equal(-8.0, store.Get().TxDisplayCalOffsetDb);
    }

    [Theory]
    [InlineData(3000)]    // not a power-of-two in the accepted set
    [InlineData(0)]
    [InlineData(1_000_000)]
    public void InvalidFftSize_IsDropped(int bad)
    {
        using var store = NewStore();
        Save(store, fft: 8192);
        Save(store, fft: bad);
        Assert.Equal(8192, store.Get().TxDisplayFftSize);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(12)]
    public void InvalidWindow_IsDropped(int bad)
    {
        using var store = NewStore();
        Save(store, win: 2);
        Save(store, win: bad);
        Assert.Equal(2, store.Get().TxDisplayWindow);
    }

    [Theory]
    [InlineData(-5.0)]
    [InlineData(5000.0)]  // beyond the 2000 ms cap
    [InlineData(double.NaN)]
    public void InvalidAvgTau_IsDropped(double bad)
    {
        using var store = NewStore();
        Save(store, tau: 150.0);
        Save(store, tau: bad);
        Assert.Equal(150.0, store.Get().TxDisplayAvgTauMs);
    }
}
