// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

/// <summary>
/// CTUN (click-tune / centred-tuning) invariants in <see cref="RadioService"/>.
/// CTUN re-introduces the frozen-NCO model behind a toggle, with the TX fix the
/// #470 revert lacked: on key-down the hardware NCO retunes to the dial so the
/// shared P1/P2 VFO register transmits on frequency, then restores the frozen
/// RX centre on un-key. We pin:
///
///   1. Off (default) → classic: SetVfo retunes the NCO to the dial.
///   2. On → SetVfo within the IQ window moves only VfoHz; RadioLoHz frozen.
///   3. On → SetVfo past the IF capacity (±0.45×sample_rate) recenters so the
///      radio keeps demodulating.
///   4. External sources (fromExternal=true) always recenter, even under CTUN.
///   5. Disabling CTUN snaps the NCO back to the dial.
///   6. Key-down (SetMox true) aligns the NCO to the dial; key-up restores the
///      frozen centre — the TX-on-the-dial guarantee.
///   7. CtunEnabled persists across a restart.
/// </summary>
public sealed class RadioServiceCtunTests : IDisposable
{
    private readonly string _dbPath;
    private readonly PaSettingsStore _paStore;
    private readonly DspSettingsStore _dspStore;

    public RadioServiceCtunTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zeus-prefs-ctun-{Guid.NewGuid():N}.db");
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
        new RadioService(NullLoggerFactory.Instance, _dspStore, _paStore);

    [Fact]
    public void CtunOff_SetVfo_RetunesRadioLoToDial()
    {
        using var radio = BuildRadio();
        radio.SetMode(RxMode.USB);
        radio.SetRadioLo(14_074_000);

        var after = radio.SetVfo(14_080_000);

        // CTUN off (default) → classic recenter.
        Assert.Equal(14_080_000, after.VfoHz);
        Assert.Equal(14_080_000, after.RadioLoHz);
        Assert.False(after.CtunEnabled);
    }

    [Fact]
    public void CtunOn_SetVfoWithinWindow_FreezesRadioLo()
    {
        using var radio = BuildRadio();
        radio.SetMode(RxMode.USB);
        radio.SetVfo(14_074_000);          // classic — LO == dial == 14_074_000
        radio.SetCtunEnabled(true);        // freeze the NCO here

        // +6 kHz is well inside the 192 ksps IQ window (±~86 kHz IF capacity).
        var after = radio.SetVfo(14_080_000);

        Assert.True(after.CtunEnabled);
        Assert.Equal(14_080_000, after.VfoHz);     // dial moved
        Assert.Equal(14_074_000, after.RadioLoHz); // NCO frozen — off-centre dial
    }

    [Fact]
    public void CtunOn_SetVfoPastIfCapacity_Recenters()
    {
        using var radio = BuildRadio();
        radio.SetMode(RxMode.USB);
        radio.SetVfo(14_074_000);
        radio.SetCtunEnabled(true);

        // +1 MHz blows past ±0.45×192_000 ≈ 86 kHz → fall through to recenter so
        // the signal stays inside the captured spectrum.
        var after = radio.SetVfo(15_074_000);

        Assert.Equal(15_074_000, after.VfoHz);
        Assert.Equal(15_074_000, after.RadioLoHz); // recentred on the dial
    }

    [Fact]
    public void CtunOn_ExternalSource_AlwaysRecenters()
    {
        using var radio = BuildRadio();
        radio.SetMode(RxMode.USB);
        radio.SetVfo(14_074_000);
        radio.SetCtunEnabled(true);

        // fromExternal=true (CAT/TCI) bypasses the frozen-NCO path even inside
        // the window — mirrors Thetis CATChangesCenterFreq=true.
        var after = radio.SetVfo(14_080_000, fromExternal: true);

        Assert.Equal(14_080_000, after.VfoHz);
        Assert.Equal(14_080_000, after.RadioLoHz);
    }

    [Fact]
    public void DisablingCtun_SnapsRadioLoBackToDial()
    {
        using var radio = BuildRadio();
        radio.SetMode(RxMode.USB);
        radio.SetVfo(14_074_000);
        radio.SetCtunEnabled(true);
        radio.SetVfo(14_080_000);          // dial off-centre, NCO frozen at 14_074_000

        var after = radio.SetCtunEnabled(false);

        Assert.False(after.CtunEnabled);
        Assert.Equal(14_080_000, after.VfoHz);
        Assert.Equal(14_080_000, after.RadioLoHz); // recentred on the dial
    }

    [Fact]
    public void CtunOn_KeyDown_AlignsNcoToDial_KeyUp_RestoresFrozenCentre()
    {
        using var radio = BuildRadio();
        radio.SetMode(RxMode.USB);
        radio.SetVfo(14_074_000);
        radio.SetCtunEnabled(true);
        radio.SetVfo(14_080_000);          // off-centre dial; NCO frozen at 14_074_000
        Assert.Equal(14_074_000, radio.Snapshot().RadioLoHz);

        // Key down — the carrier must land on the dial, not the frozen centre.
        radio.SetMox(true);
        Assert.Equal(14_080_000, radio.Snapshot().RadioLoHz);

        // Key up — the off-centre RX view comes back.
        radio.SetMox(false);
        Assert.Equal(14_074_000, radio.Snapshot().RadioLoHz);
        Assert.Equal(14_080_000, radio.Snapshot().VfoHz);
    }

    [Fact]
    public void CtunOff_KeyDown_DoesNotTouchRadioLo()
    {
        using var radio = BuildRadio();
        radio.SetMode(RxMode.USB);
        radio.SetVfo(14_074_000);
        long loBefore = radio.Snapshot().RadioLoHz;

        radio.SetMox(true);
        Assert.Equal(loBefore, radio.Snapshot().RadioLoHz);
        radio.SetMox(false);
        Assert.Equal(loBefore, radio.Snapshot().RadioLoHz);
    }

    [Fact]
    public void CtunEnabled_PersistsAcrossRestart()
    {
        var storePath = _dbPath + ".rs";
        // Two separate RadioStateStore instances over the same file mirror a
        // real restart: the first flushes on dispose, the second hydrates.
        var store1 = new RadioStateStore(NullLogger<RadioStateStore>.Instance, storePath);
        using (var radio = new RadioService(
            NullLoggerFactory.Instance, _dspStore, _paStore, radioStateStore: store1))
        {
            radio.SetCtunEnabled(true);
        } // RadioService.Dispose → FlushState → store1.Save (CtunEnabled=true).
        store1.Dispose(); // release the LiteDB file before reopening it.

        var store2 = new RadioStateStore(NullLogger<RadioStateStore>.Instance, storePath);
        using (var reborn = new RadioService(
            NullLoggerFactory.Instance, _dspStore, _paStore, radioStateStore: store2))
        {
            Assert.True(reborn.Snapshot().CtunEnabled);
        }
        store2.Dispose();
        try { if (File.Exists(storePath)) File.Delete(storePath); } catch { }
    }

    [Fact]
    public void CtunOff_PersistedOffCenterRadioLo_RehydratesAtDial()
    {
        const long dialHz = 14_275_000;
        const long staleLoHz = 14_258_065;
        var storePath = _dbPath + $".rs-{Guid.NewGuid():N}";

        var store1 = new RadioStateStore(NullLogger<RadioStateStore>.Instance, storePath);
        store1.Save(new RadioStateEntry
        {
            VfoHz = dialHz,
            Mode = RxMode.USB,
            RadioLoHz = staleLoHz,
            CtunEnabled = false,
            UpdatedUtc = DateTime.UtcNow,
        });
        store1.Dispose();

        var store2 = new RadioStateStore(NullLogger<RadioStateStore>.Instance, storePath);
        using (var reborn = new RadioService(
            NullLoggerFactory.Instance, _dspStore, _paStore, radioStateStore: store2))
        {
            var snap = reborn.Snapshot();
            Assert.False(snap.CtunEnabled);
            Assert.Equal(dialHz, snap.VfoHz);
            Assert.Equal(dialHz, snap.RadioLoHz);
        }
        store2.Dispose();
        try { if (File.Exists(storePath)) File.Delete(storePath); } catch { }
    }

    [Fact]
    public void CtunOff_CwPersistedOffCenterRadioLo_RehydratesAtEffectiveDial()
    {
        const long dialHz = 14_046_000;
        const long staleLoHz = 14_258_065;
        var storePath = _dbPath + $".rs-{Guid.NewGuid():N}";

        var store1 = new RadioStateStore(NullLogger<RadioStateStore>.Instance, storePath);
        store1.Save(new RadioStateEntry
        {
            VfoHz = dialHz,
            Mode = RxMode.CWU,
            RadioLoHz = staleLoHz,
            CtunEnabled = false,
            UpdatedUtc = DateTime.UtcNow,
        });
        store1.Dispose();

        var store2 = new RadioStateStore(NullLogger<RadioStateStore>.Instance, storePath);
        using (var reborn = new RadioService(
            NullLoggerFactory.Instance, _dspStore, _paStore, radioStateStore: store2))
        {
            var snap = reborn.Snapshot();
            Assert.False(snap.CtunEnabled);
            Assert.Equal(dialHz, snap.VfoHz);
            Assert.Equal(dialHz - CwOffset.CwPitchHz, snap.RadioLoHz);
        }
        store2.Dispose();
        try { if (File.Exists(storePath)) File.Delete(storePath); } catch { }
    }

    [Fact]
    public void CtunOn_PersistedOffCenterRadioLo_RehydratesFrozenCenter()
    {
        const long dialHz = 14_275_000;
        const long frozenLoHz = 14_258_065;
        var storePath = _dbPath + $".rs-{Guid.NewGuid():N}";

        var store1 = new RadioStateStore(NullLogger<RadioStateStore>.Instance, storePath);
        store1.Save(new RadioStateEntry
        {
            VfoHz = dialHz,
            Mode = RxMode.USB,
            RadioLoHz = frozenLoHz,
            CtunEnabled = true,
            UpdatedUtc = DateTime.UtcNow,
        });
        store1.Dispose();

        var store2 = new RadioStateStore(NullLogger<RadioStateStore>.Instance, storePath);
        using (var reborn = new RadioService(
            NullLoggerFactory.Instance, _dspStore, _paStore, radioStateStore: store2))
        {
            var snap = reborn.Snapshot();
            Assert.True(snap.CtunEnabled);
            Assert.Equal(dialHz, snap.VfoHz);
            Assert.Equal(frozenLoHz, snap.RadioLoHz);
        }
        store2.Dispose();
        try { if (File.Exists(storePath)) File.Delete(storePath); } catch { }
    }
}
