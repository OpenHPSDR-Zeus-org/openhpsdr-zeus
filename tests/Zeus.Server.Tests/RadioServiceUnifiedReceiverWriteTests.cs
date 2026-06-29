using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

// Uniform numeric-receiver write path: /api/receivers/{index} (RadioService
// .SetReceiver) must drive EVERY receiver, including RX1 (0) and RX2 (1) which
// live on the flat StateDto fields. Each supplied field routes to the canonical
// RX1/RX2 setter so the legacy A/B endpoints and the unified endpoint stay in
// lock-step. Part of the A/B -> numeric receiver migration.
public sealed class RadioServiceUnifiedReceiverWriteTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-unifiedrx-{Guid.NewGuid():N}.db");
    private readonly PaSettingsStore _paStore;
    private readonly DspSettingsStore _dspStore;

    public RadioServiceUnifiedReceiverWriteTests()
    {
        _paStore = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath + ".pa");
        _dspStore = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _dbPath + ".dsp");
    }

    public void Dispose()
    {
        _paStore.Dispose();
        _dspStore.Dispose();
        foreach (var suffix in new[] { "", ".pa", ".dsp" })
        {
            try { if (File.Exists(_dbPath + suffix)) File.Delete(_dbPath + suffix); } catch { }
        }
    }

    private RadioService BuildRadio() => new(NullLoggerFactory.Instance, _dspStore, _paStore);

    private static ReceiverDto Rx(StateDto s, int index) =>
        s.Receivers!.Single(r => r.Index == index);

    [Fact]
    public void SetReceiver_Rx1_RoutesVfoModeFilterAndAf()
    {
        using var radio = BuildRadio();

        radio.SetReceiver(0, vfoHz: 14_074_000);
        radio.SetReceiver(0, mode: RxMode.DIGU);
        radio.SetReceiver(0, filterLowHz: 200, filterHighHz: 2900, filterPresetName: "VAR2");
        var s = radio.SetReceiver(0, afGainDb: -6.0);

        // Flat RX1 fields and the projected receivers[0] entry agree.
        Assert.Equal(14_074_000, s.VfoHz);
        Assert.Equal(RxMode.DIGU, s.Mode);
        Assert.Equal(200, s.FilterLowHz);
        Assert.Equal(2900, s.FilterHighHz);
        Assert.Equal("VAR2", s.FilterPresetName);
        Assert.Equal(-6.0, s.RxAfGainDb);

        var rx1 = Rx(s, 0);
        Assert.Equal(14_074_000, rx1.VfoHz);
        Assert.Equal(RxMode.DIGU, rx1.Mode);
        Assert.Equal(200, rx1.FilterLowHz);
        Assert.Equal(2900, rx1.FilterHighHz);
        Assert.Equal("VAR2", rx1.FilterPresetName);
    }

    [Fact]
    public void SetReceiver_Rx2_RoutesEnableVfoModeFilterAndAf()
    {
        using var radio = BuildRadio();

        radio.SetReceiver(1, enabled: true, vfoHz: 7_074_000);
        radio.SetReceiver(1, mode: RxMode.LSB);
        radio.SetReceiver(1, filterLowHz: -2850, filterHighHz: -100, filterPresetName: "VAR1");
        var s = radio.SetReceiver(1, afGainDb: -3.0);

        // RX2's tuning is authoritative in the projected receivers[1] entry
        // (the flat VFO-B fields were retired in the A/B wire collapse).
        Assert.True(s.Rx2Enabled);

        var rx2 = Rx(s, 1);
        Assert.True(rx2.Enabled);
        Assert.Equal(7_074_000, rx2.VfoHz);
        Assert.Equal(RxMode.LSB, rx2.Mode);
        Assert.Equal(-2850, rx2.FilterLowHz);
        Assert.Equal(-100, rx2.FilterHighHz);
    }

    [Fact]
    public void SetReceiver_Rx1Mode_MatchesLegacySetMode()
    {
        using var viaUnified = BuildRadio();
        using var viaLegacy = BuildRadio();

        var a = viaUnified.SetReceiver(0, mode: RxMode.CWU);
        var b = viaLegacy.SetMode(RxMode.CWU, TxVfo.A);

        Assert.Equal(b.Mode, a.Mode);
        Assert.Equal(b.FilterLowHz, a.FilterLowHz);
        Assert.Equal(b.FilterHighHz, a.FilterHighHz);
    }

    [Fact]
    public void SetReceiver_PartialFilter_FillsMissingEdgeFromCurrent()
    {
        using var radio = BuildRadio();
        radio.SetReceiver(0, filterLowHz: 100, filterHighHz: 2800, filterPresetName: "VAR1");

        // Supply only the high edge — the low edge must persist.
        var s = radio.SetReceiver(0, filterHighHz: 3200);

        Assert.Equal(100, s.FilterLowHz);
        Assert.Equal(3200, s.FilterHighHz);
    }
}
