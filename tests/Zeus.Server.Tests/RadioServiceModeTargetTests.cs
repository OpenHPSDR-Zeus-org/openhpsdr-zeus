using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class RadioServiceModeTargetTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-mode-target-{Guid.NewGuid():N}.db");
    private readonly PaSettingsStore _paStore;
    private readonly DspSettingsStore _dspStore;

    public RadioServiceModeTargetTests()
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

    private RadioService BuildRadio() =>
        new(NullLoggerFactory.Instance, _dspStore, _paStore);

    [Fact]
    public void SetMode_DefaultTarget_BumpsVfoA()
    {
        using var radio = BuildRadio();
        radio.SetMode(RxMode.USB);
        radio.SetVfo(14_200_000);
        radio.SetRx2(new Rx2SetRequest(Enabled: true, VfoBHz: 7_100_000));

        var after = radio.SetMode(RxMode.CWU);

        Assert.Equal(RxMode.CWU, after.Mode);
        Assert.Equal(14_200_600, after.VfoHz);
        Assert.Equal(7_100_000, after.Rx2().VfoHz);
    }

    [Fact]
    public void SetMode_TargetB_BumpsVfoBWithoutMovingVfoA()
    {
        using var radio = BuildRadio();
        radio.SetMode(RxMode.USB);
        radio.SetVfo(14_200_000);
        radio.SetRx2(new Rx2SetRequest(Enabled: true, VfoBHz: 7_100_000));

        var after = radio.SetMode(RxMode.CWU, TxVfo.B);

        Assert.Equal(RxMode.USB, after.Mode);
        Assert.Equal(RxMode.CWU, after.Rx2().Mode);
        Assert.Equal(14_200_000, after.VfoHz);
        Assert.Equal(7_100_600, after.Rx2().VfoHz);
    }

    [Fact]
    public void SetFilter_TargetB_LeavesPrimaryFilterUntouched()
    {
        using var radio = BuildRadio();
        radio.SetMode(RxMode.USB);
        radio.SetFilter(150, 2850, "VAR1");
        radio.SetRx2(new Rx2SetRequest(Enabled: true, VfoBHz: 7_100_000));

        var after = radio.SetFilter(300, 2400, "F6", TxVfo.B);

        Assert.Equal(150, after.FilterLowHz);
        Assert.Equal(2850, after.FilterHighHz);
        Assert.Equal("VAR1", after.FilterPresetName);
        Assert.Equal(300, after.Rx2().FilterLowHz);
        Assert.Equal(2400, after.Rx2().FilterHighHz);
        Assert.Equal("F6", after.Rx2().FilterPresetName);
    }

    [Fact]
    public void SetFilter_TargetB_DoesNotReplacePrimaryModeMemory()
    {
        using var radio = BuildRadio();
        radio.SetMode(RxMode.USB);
        radio.SetFilter(150, 2850, "VAR1");
        radio.SetRx2(new Rx2SetRequest(Enabled: true, VfoBHz: 7_100_000));
        radio.SetMode(RxMode.AM);

        radio.SetFilter(300, 2400, "F6", TxVfo.B);
        var after = radio.SetMode(RxMode.USB);

        Assert.Equal(RxMode.USB, after.Mode);
        Assert.Equal(150, after.FilterLowHz);
        Assert.Equal(2850, after.FilterHighHz);
        Assert.Equal(300, after.Rx2().FilterLowHz);
        Assert.Equal(2400, after.Rx2().FilterHighHz);
    }

    [Fact]
    public void SetMode_TargetB_RestoresTargetBFilterMemory()
    {
        using var radio = BuildRadio();
        radio.SetMode(RxMode.USB);
        radio.SetFilter(150, 2850, "VAR1");
        radio.SetRx2(new Rx2SetRequest(Enabled: true, VfoBHz: 7_100_000));
        radio.SetFilter(300, 2400, "F6", TxVfo.B);

        radio.SetMode(RxMode.AM, TxVfo.B);
        var after = radio.SetMode(RxMode.USB, TxVfo.B);

        Assert.Equal(RxMode.USB, after.Mode);
        Assert.Equal(RxMode.USB, after.Rx2().Mode);
        Assert.Equal(150, after.FilterLowHz);
        Assert.Equal(2850, after.FilterHighHz);
        Assert.Equal(300, after.Rx2().FilterLowHz);
        Assert.Equal(2400, after.Rx2().FilterHighHz);
    }
}
