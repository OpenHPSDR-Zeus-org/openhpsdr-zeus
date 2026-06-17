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
        Assert.Equal(7_100_000, after.VfoBHz);
    }

    [Fact]
    public void SetMode_TargetB_BumpsVfoBWithoutMovingVfoA()
    {
        using var radio = BuildRadio();
        radio.SetMode(RxMode.USB);
        radio.SetVfo(14_200_000);
        radio.SetRx2(new Rx2SetRequest(Enabled: true, VfoBHz: 7_100_000));

        var after = radio.SetMode(RxMode.CWU, TxVfo.B);

        Assert.Equal(RxMode.CWU, after.Mode);
        Assert.Equal(14_200_000, after.VfoHz);
        Assert.Equal(7_100_600, after.VfoBHz);
    }
}
