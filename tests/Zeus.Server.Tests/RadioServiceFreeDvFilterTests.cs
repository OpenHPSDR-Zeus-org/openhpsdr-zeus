using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

/// <summary>
/// FreeDV is a linear digital mode spec-locked to a tight bandpass (300..2700 Hz,
/// USB) around the 1500 Hz-centred modem. It must NOT inherit the shared SSB
/// filter slot — entering FreeDV applies the spec passband and leaving it restores
/// the operator's SSB widths via the same mode-family memory every other mode uses.
/// </summary>
public sealed class RadioServiceFreeDvFilterTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-freedv-filter-{Guid.NewGuid():N}.db");
    private readonly PaSettingsStore _paStore;
    private readonly DspSettingsStore _dspStore;

    public RadioServiceFreeDvFilterTests()
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
    public void SetMode_FreeDv_AppliesSpecBandpassToRxAndTx()
    {
        using var radio = BuildRadio();
        radio.SetMode(RxMode.USB);

        var after = radio.SetMode(RxMode.FreeDv);

        Assert.Equal(RxMode.FreeDv, after.Mode);
        // USB-signed positive spec passband on both RX and TX.
        Assert.Equal(300, after.FilterLowHz);
        Assert.Equal(2700, after.FilterHighHz);
        Assert.Equal(300, after.TxFilterLowHz);
        Assert.Equal(2700, after.TxFilterHighHz);
    }

    [Fact]
    public void SetMode_LeavingFreeDv_RestoresOperatorSsbWidths()
    {
        using var radio = BuildRadio();
        radio.SetMode(RxMode.USB);
        // Operator picks a non-default SSB RX + TX width.
        radio.SetFilter(100, 3000, "VAR1");
        radio.SetTxFilter(120, 2900);

        radio.SetMode(RxMode.FreeDv);
        var back = radio.SetMode(RxMode.USB);

        Assert.Equal(RxMode.USB, back.Mode);
        Assert.Equal(100, back.FilterLowHz);
        Assert.Equal(3000, back.FilterHighHz);
        Assert.Equal(120, back.TxFilterLowHz);
        Assert.Equal(2900, back.TxFilterHighHz);
    }

    [Fact]
    public void SetMode_FreeDv_DoesNotStompSharedSsbSlot()
    {
        using var radio = BuildRadio();
        radio.SetMode(RxMode.USB);
        radio.SetFilter(150, 2850, "VAR1");

        // Round-trip USB -> FreeDV -> DIGU. DIGU shares the SSB slot, so if FreeDV
        // had written its 300/2700 into the SSB slot, DIGU would inherit it.
        radio.SetMode(RxMode.FreeDv);
        var digu = radio.SetMode(RxMode.DIGU);

        // DIGU is USB-family; SignedFilterForMode zeroes the low edge for DIGU.
        Assert.Equal(RxMode.DIGU, digu.Mode);
        Assert.Equal(2850, digu.FilterHighHz);
    }
}
