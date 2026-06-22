// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

// Backend coverage for the control-surface features added alongside the
// piHPSDR keypad mapping: VFO lock, RIT, XIT, per-RX mute, and diversity.
public sealed class ControlSurfaceBackendTests : IDisposable
{
    private readonly string _basePath = Path.Combine(
        Path.GetTempPath(),
        $"zeus-ctrlsurface-{Guid.NewGuid():N}");

    public void Dispose()
    {
        foreach (var path in Directory.EnumerateFiles(Path.GetTempPath(), Path.GetFileName(_basePath) + "*"))
        {
            try { File.Delete(path); } catch { }
        }
    }

    private RadioService NewRadio()
    {
        var dspStore = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _basePath + ".dsp");
        var paStore = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _basePath + ".pa");
        return new RadioService(NullLoggerFactory.Instance, dspStore, paStore);
    }

    // ---- VFO lock ----

    [Fact]
    public void VfoLock_BlocksOperatorTuning_AllowsExternal()
    {
        using var radio = NewRadio();
        radio.SetVfo(14_100_000);
        radio.SetVfoLock(true);

        // Operator dial tune is rejected while locked.
        var afterOperator = radio.SetVfo(14_200_000);
        Assert.Equal(14_100_000, afterOperator.VfoHz);
        Assert.True(afterOperator.VfoLocked);

        // External (CAT/TCI/calibration) tuning still goes through.
        var afterExternal = radio.SetVfo(14_250_000, fromExternal: true);
        Assert.Equal(14_250_000, afterExternal.VfoHz);

        // Unlock restores operator tuning.
        radio.SetVfoLock(false);
        var unlocked = radio.SetVfo(14_300_000);
        Assert.Equal(14_300_000, unlocked.VfoHz);
    }

    [Fact]
    public void VfoLock_BlocksVfoB()
    {
        using var radio = NewRadio();
        radio.SetVfoB(7_100_000);
        radio.SetVfoLock(true);
        var after = radio.SetVfoB(7_200_000);
        Assert.Equal(7_100_000, after.VfoBHz);
    }

    [Fact]
    public void VfoLock_DefaultsUnlocked()
    {
        using var radio = NewRadio();
        Assert.False(radio.Snapshot().VfoLocked);
    }

    // ---- RIT ----

    [Fact]
    public void SetRit_ClampsToRange_AndKeepsVfoStill()
    {
        using var radio = NewRadio();
        radio.SetVfo(14_200_000);

        var s = radio.SetRit(enabled: true, hz: 500);
        Assert.True(s.RitEnabled);
        Assert.Equal(500, s.RitHz);
        // RIT must NOT move the displayed dial.
        Assert.Equal(14_200_000, s.VfoHz);

        // Clamp to ±99999.
        Assert.Equal(99_999, radio.SetRit(null, 250_000).RitHz);
        Assert.Equal(-99_999, radio.SetRit(null, -250_000).RitHz);

        // Partial update leaves the other field untouched.
        var disabled = radio.SetRit(enabled: false, hz: null);
        Assert.False(disabled.RitEnabled);
        Assert.Equal(-99_999, disabled.RitHz);
    }

    // ---- XIT ----

    [Fact]
    public void Xit_OffsetsTxCarrierOnly()
    {
        using var radio = NewRadio();
        radio.SetVfo(14_200_000);
        var baseline = radio.Snapshot();
        // No XIT → carrier == dial.
        Assert.Equal(14_200_000, RadioService.TxCarrierHz(baseline));

        var s = radio.SetXit(enabled: true, hz: 1_000);
        Assert.True(s.XitEnabled);
        Assert.Equal(1_000, s.XitHz);
        // Displayed VFO unchanged; carrier shifted by XIT.
        Assert.Equal(14_200_000, s.VfoHz);
        Assert.Equal(14_201_000, RadioService.TxCarrierHz(s));

        // Disable → carrier returns to dial even with a stored offset.
        var off = radio.SetXit(enabled: false, hz: null);
        Assert.Equal(14_200_000, RadioService.TxCarrierHz(off));
    }

    [Fact]
    public void Xit_ClampsToRange()
    {
        using var radio = NewRadio();
        Assert.Equal(99_999, radio.SetXit(null, 250_000).XitHz);
        Assert.Equal(-99_999, radio.SetXit(null, -250_000).XitHz);
    }

    // ---- Per-RX mute ----

    [Fact]
    public void Mute_ProjectsOntoReceiver_PerIndex()
    {
        using var radio = NewRadio();
        var s = radio.SetReceiverMuted(0, true);
        Assert.True(s.Rx1Muted);
        Assert.True(s.Receivers![0].Muted);
        Assert.False(s.Receivers[1].Muted);

        s = radio.SetReceiverMuted(1, true);
        Assert.True(s.Rx2Muted);
        Assert.True(s.Receivers![1].Muted);

        s = radio.SetReceiverMuted(0, false);
        Assert.False(s.Rx1Muted);
        Assert.False(s.Receivers![0].Muted);
        // RX2 still muted.
        Assert.True(s.Receivers[1].Muted);
    }

    [Fact]
    public void Mute_RejectsOutOfRangeIndex()
    {
        using var radio = NewRadio();
        Assert.Throws<ArgumentOutOfRangeException>(() => radio.SetReceiverMuted(99, true));
    }

    // ---- Diversity ----

    [Fact]
    public void SetDiversity_ClampsAndMerges()
    {
        using var radio = NewRadio();
        var s = radio.SetDiversity(enabled: true, gain: 5.0, phaseDeg: 400.0, sourceRx: 1);
        Assert.NotNull(s.Diversity);
        Assert.True(s.Diversity!.Enabled);
        Assert.Equal(2.0, s.Diversity.Gain);        // clamped 0..2
        Assert.Equal(180.0, s.Diversity.PhaseDeg);  // clamped -180..180

        // Partial update keeps prior fields.
        var s2 = radio.SetDiversity(enabled: false, gain: null, phaseDeg: null, sourceRx: null);
        Assert.False(s2.Diversity!.Enabled);
        Assert.Equal(2.0, s2.Diversity.Gain);
        Assert.Equal(180.0, s2.Diversity.PhaseDeg);
    }

    [Fact]
    public void Diversity_DefaultsOff()
    {
        using var radio = NewRadio();
        Assert.Null(radio.Snapshot().Diversity);
    }

    // ---- Diversity combine math (DspPipelineService.DiversityCombine) ----

    [Fact]
    public void DiversityCombine_UnityWeight_AddsSourceToReference()
    {
        // rx0 = (1, 2), src = (3, 4); weight = 1 + 0j → dest = (1+3, 2+4).
        double[] rx0 = [1, 2];
        double[] src = [3, 4];
        double[] dest = new double[2];
        DspPipelineService.DiversityCombine(rx0, src, wI: 1.0, wQ: 0.0, dest);
        Assert.Equal(4.0, dest[0], 9);
        Assert.Equal(6.0, dest[1], 9);
    }

    [Fact]
    public void DiversityCombine_NinetyDegreePhase_RotatesSource()
    {
        // weight = 0 + 1j (90°). source (si, sq) rotates to (-sq, si).
        // src = (3, 4) → rotated (-4, 3); rx0 = (0,0) → dest = (-4, 3).
        double[] rx0 = [0, 0];
        double[] src = [3, 4];
        double[] dest = new double[2];
        DspPipelineService.DiversityCombine(rx0, src, wI: 0.0, wQ: 1.0, dest);
        Assert.Equal(-4.0, dest[0], 9);
        Assert.Equal(3.0, dest[1], 9);
    }

    [Fact]
    public void DiversityCombine_ZeroGain_PassesReferenceThrough()
    {
        // gain 0 → weight (0,0) → dest == rx0 (source contributes nothing).
        double[] rx0 = [5, -7, 2, 9];
        double[] src = [100, 100, 100, 100];
        double[] dest = new double[4];
        DspPipelineService.DiversityCombine(rx0, src, wI: 0.0, wQ: 0.0, dest);
        Assert.Equal(rx0, dest);
    }

    [Fact]
    public void DiversityCombine_ShorterSource_TailPassesThrough()
    {
        // src covers only the first IQ pair; the rest of rx0 is copied verbatim.
        double[] rx0 = [1, 1, 2, 2];
        double[] src = [10, 0];
        double[] dest = new double[4];
        DspPipelineService.DiversityCombine(rx0, src, wI: 1.0, wQ: 0.0, dest);
        Assert.Equal(11.0, dest[0], 9); // 1 + 10
        Assert.Equal(1.0, dest[1], 9);  // 1 + 0
        Assert.Equal(2.0, dest[2], 9);  // untouched
        Assert.Equal(2.0, dest[3], 9);  // untouched
    }
}
