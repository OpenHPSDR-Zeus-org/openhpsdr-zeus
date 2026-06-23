using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

// Multi-DDC TX target (SetTxReceiver / TxFrequencyHz index resolution) and the
// per-RX listen/mute audible model (SetReceiverAudible + legacy Rx2AudioMode
// projection). See docs/designs/multi-ddc-tx-and-audio.md.
public sealed class RadioServiceMultiDdcTxAudioTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-multiddc-{Guid.NewGuid():N}.db");
    private readonly PaSettingsStore _paStore;
    private readonly DspSettingsStore _dspStore;

    public RadioServiceMultiDdcTxAudioTests()
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
    public void TxFrequency_DefaultsToRx1()
    {
        using var radio = BuildRadio();
        radio.SetVfo(14_200_000);

        var s = radio.Snapshot();
        Assert.Equal(0, s.TxReceiverIndex);
        Assert.Equal(TxVfo.A, s.TxVfo);
        Assert.Equal(14_200_000, RadioService.TxFrequencyHz(s));
    }

    [Fact]
    public void SetTxReceiver_Rx2_TransmitsOnVfoB()
    {
        using var radio = BuildRadio();
        radio.SetVfo(14_200_000);
        radio.SetRx2(new Rx2SetRequest(Enabled: true, VfoBHz: 7_100_000));

        var s = radio.SetTxReceiver(1);

        Assert.Equal(1, s.TxReceiverIndex);
        Assert.Equal(TxVfo.B, s.TxVfo); // legacy A/B projection stays consistent
        Assert.Equal(7_100_000, RadioService.TxFrequencyHz(s));
    }

    [Fact]
    public void SetTxReceiver_ExtraDdc_TransmitsOnThatReceiverVfo()
    {
        using var radio = BuildRadio();
        radio.SetVfo(14_200_000);
        radio.SetReceiver(2, enabled: true, vfoHz: 21_050_000);

        var s = radio.SetTxReceiver(2);

        Assert.Equal(2, s.TxReceiverIndex);
        Assert.Equal(TxVfo.A, s.TxVfo); // RX3+ projects to A for legacy consumers
        Assert.Equal(21_050_000, RadioService.TxFrequencyHz(s));
    }

    [Fact]
    public void SetTxReceiver_UnexposedExtra_ClampsToRx1()
    {
        using var radio = BuildRadio();
        radio.SetVfo(14_200_000);

        // RX3 not exposed → must not transmit on a receiver that isn't streaming.
        var s = radio.SetTxReceiver(2);

        Assert.Equal(0, s.TxReceiverIndex);
        Assert.Equal(14_200_000, RadioService.TxFrequencyHz(s));
    }

    [Fact]
    public void DisablingTxTargetReceiver_FallsBackToRx1()
    {
        using var radio = BuildRadio();
        radio.SetReceiver(2, enabled: true, vfoHz: 21_050_000);
        radio.SetTxReceiver(2);

        // Disabling RX3 (the TX target) must drop TX back to RX1.
        var s = radio.SetReceiver(2, enabled: false);

        Assert.Equal(0, s.TxReceiverIndex);
        Assert.Equal(TxVfo.A, s.TxVfo);
    }

    [Fact]
    public void Audible_DefaultsTrueForAllReceivers()
    {
        using var radio = BuildRadio();
        radio.SetRx2(new Rx2SetRequest(Enabled: true));
        radio.SetReceiver(2, enabled: true, vfoHz: 21_050_000);

        var s = radio.Snapshot();
        Assert.True(Rx(s, 0).Audible);
        Assert.True(Rx(s, 1).Audible);
        Assert.True(Rx(s, 2).Audible);
    }

    [Fact]
    public void SetReceiverAudible_MutingRx1_DerivesRx2OnlyLegacyMode()
    {
        using var radio = BuildRadio();
        radio.SetRx2(new Rx2SetRequest(Enabled: true));

        var s = radio.SetReceiverAudible(0, false);

        Assert.False(Rx(s, 0).Audible);
        Assert.True(Rx(s, 1).Audible);
        // RX1 muted + RX2 audible projects to the legacy Rx2-only tri-state.
        Assert.Equal(Rx2AudioMode.Rx2, s.Rx2AudioMode);
    }

    [Fact]
    public void SetReceiverAudible_ExtraDdc_DoesNotTouchLegacyTriState()
    {
        using var radio = BuildRadio();
        radio.SetRx2(new Rx2SetRequest(Enabled: true));
        radio.SetReceiver(2, enabled: true, vfoHz: 21_050_000);

        var s = radio.SetReceiverAudible(2, false);

        Assert.False(Rx(s, 2).Audible);
        Assert.True(Rx(s, 0).Audible);
        Assert.True(Rx(s, 1).Audible);
        Assert.Equal(Rx2AudioMode.Both, s.Rx2AudioMode);
    }

    [Fact]
    public void LegacyRx2AudioMode_SyncsAudibleBits()
    {
        using var radio = BuildRadio();
        radio.SetRx2(new Rx2SetRequest(Enabled: true, AudioMode: Rx2AudioMode.Rx1));

        var s = radio.Snapshot();
        Assert.True(Rx(s, 0).Audible);   // Rx1-only → RX1 audible
        Assert.False(Rx(s, 1).Audible);  // RX2 muted
    }
}
