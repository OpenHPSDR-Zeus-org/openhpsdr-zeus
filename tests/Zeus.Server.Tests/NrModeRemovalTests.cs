// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class NrModeRemovalTests : IDisposable
{
    private readonly string _basePath = Path.Combine(
        Path.GetTempPath(),
        $"zeus-nr-unsupported-{Guid.NewGuid():N}");

    public void Dispose()
    {
        foreach (var path in Directory.EnumerateFiles(Path.GetTempPath(), Path.GetFileName(_basePath) + "*"))
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void SetNr_NormalizesUnsupportedModeToOff()
    {
        using var dspStore = NewDspStore();
        using var radio = NewRadio(dspStore);

        // 5 is one past the last defined mode (Rnnr = 4) — a corrupt/legacy
        // DB value that must normalize to Off rather than be honored.
        var snapshot = radio.SetNr(new NrConfig(NrMode: (NrMode)5));

        Assert.Equal(NrMode.Off, snapshot.Nr?.NrMode);
        Assert.Equal(NrMode.Off, dspStore.Get()?.NrMode);
    }

    [Fact]
    public void Constructor_NormalizesPersistedUnsupportedModeToOff()
    {
        using var dspStore = NewDspStore();
        // 5 is one past the last defined mode (Rnnr = 4): a stale persisted
        // value from an older schema must be clamped to Off on load.
        dspStore.Upsert(new NrConfig(NrMode: (NrMode)5));

        using var radio = NewRadio(dspStore);

        Assert.Equal(NrMode.Off, radio.Snapshot().Nr?.NrMode);
    }

    // Regression: the store's NormalizeNrMode must accept every mode
    // RadioService.IsSupportedNrMode accepts. NR3 (Rnnr) was added to the
    // RadioService allow-list but not the store's, so SetNr(Rnnr) was silently
    // persisted as Off and the operator's NR3 selection never survived a
    // restart. Keep the two allow-lists in lock-step.
    [Fact]
    public void SetNr_PersistsRnnrModeAcrossReload()
    {
        using var dspStore = NewDspStore();
        using (var radio = NewRadio(dspStore))
        {
            var snapshot = radio.SetNr(new NrConfig(NrMode: NrMode.Rnnr));
            Assert.Equal(NrMode.Rnnr, snapshot.Nr?.NrMode);
        }

        Assert.Equal(NrMode.Rnnr, dspStore.Get()?.NrMode);

        using var reloaded = NewRadio(dspStore);
        Assert.Equal(NrMode.Rnnr, reloaded.Snapshot().Nr?.NrMode);
    }

    private RadioService NewRadio(DspSettingsStore dspStore)
    {
        var paStore = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _basePath + ".pa");
        return new RadioService(NullLoggerFactory.Instance, dspStore, paStore);
    }

    private DspSettingsStore NewDspStore() =>
        new(NullLogger<DspSettingsStore>.Instance, _basePath + ".dsp");
}
