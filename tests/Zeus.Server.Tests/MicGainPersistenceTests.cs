// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;
using Xunit;

namespace Zeus.Server.Tests;

/// <summary>
/// Full TX mic-gain + Leveler max-gain persistence round-trip — guards the
/// desktop-relaunch bug where the slider reverted to 0 dB on every launch.
///
/// The existing <c>MicGainEndpointTests</c> assert the HTTP handler updates
/// the live <see cref="StateDto"/>; <c>RadioStateStoreTests</c> assert the
/// LiteDB row round-trips. Neither covered the *seam between them*: that
/// <see cref="RadioService.SetTxMicGain"/> rides the debounce flush into the
/// store AND that a freshly-reconstructed <see cref="RadioService"/> (the
/// relaunch path) hydrates the operator's value back into its first
/// <see cref="StateDto"/> rather than the 0 dB / 8 dB seeds.
///
/// Disposing the first RadioService forces its final flush (same hook the
/// desktop window-close StopAsync / host-dispose drives), so this exercises
/// exactly the save→restart→restore cycle the operator hits.
/// </summary>
public class MicGainPersistenceTests : IDisposable
{
    private readonly string _dbPath;

    public MicGainPersistenceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zeus-prefs-micgaintest-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private (RadioService radio, RadioStateStore store) BuildRadioWithStore()
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var dspStore = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance);
        var paStore = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath);
        var stateStore = new RadioStateStore(NullLogger<RadioStateStore>.Instance, _dbPath);
        var radio = new RadioService(
            loggerFactory, dspStore, paStore,
            filterPresetStore: null, txIqSource: null,
            preferredRadioStore: null, psStore: null,
            radioStateStore: stateStore);
        return (radio, stateStore);
    }

    [Fact]
    public void SetTxMicGain_FlushesToStore()
    {
        var (radio, store) = BuildRadioWithStore();

        radio.SetTxMicGain(-12);
        // Force the debounce flush deterministically — same write the 1 s
        // timer / Dispose hook performs, without waiting on wall-clock.
        radio.Dispose();

        var entry = store.Get();
        Assert.NotNull(entry);
        Assert.Equal(-12, entry!.MicGainDb);
    }

    [Fact]
    public void SetTxLevelerMaxGain_FlushesToStore()
    {
        var (radio, store) = BuildRadioWithStore();

        radio.SetTxLevelerMaxGain(15.0);
        radio.Dispose();

        var entry = store.Get();
        Assert.NotNull(entry);
        Assert.Equal(15.0, entry!.LevelerMaxGainDb);
    }

    [Fact]
    public void MicGain_SurvivesRadioServiceReconstruction()
    {
        // Session 1: operator drops mic gain to -18 dB, then the app closes.
        var (radio1, _) = BuildRadioWithStore();
        radio1.SetTxMicGain(-18);
        radio1.Dispose();   // final flush — mirrors desktop window-close shutdown

        // Session 2: a fresh RadioService reads the same prefs DB on relaunch.
        var (radio2, _) = BuildRadioWithStore();
        try
        {
            // The very first StateDto the frontend hydrates from must already
            // carry the operator's value, NOT the 0 dB seed.
            Assert.Equal(-18, radio2.Snapshot().MicGainDb);
        }
        finally
        {
            radio2.Dispose();
        }
    }

    [Fact]
    public void LevelerMaxGain_SurvivesRadioServiceReconstruction()
    {
        var (radio1, _) = BuildRadioWithStore();
        radio1.SetTxLevelerMaxGain(3.5);
        radio1.Dispose();

        var (radio2, _) = BuildRadioWithStore();
        try
        {
            Assert.Equal(3.5, radio2.Snapshot().LevelerMaxGainDb);
        }
        finally
        {
            radio2.Dispose();
        }
    }

    [Fact]
    public void MicGainAndLeveler_PersistTogether_WithDriveAndVolume()
    {
        // The control fields share one radio_state row. This guards against a
        // future Save() that drops MicGainDb while still writing DrivePct /
        // RxAfGainDb — the exact asymmetry seen in the field (drive + leveler
        // persisted, mic reverted to 0) would resurface as a failing assert.
        var (radio1, _) = BuildRadioWithStore();
        radio1.SetDrive(33);
        radio1.SetRxAfGain(-9.0);
        radio1.SetTxLevelerMaxGain(11.0);
        radio1.SetTxMicGain(-7);
        radio1.Dispose();

        var (radio2, _) = BuildRadioWithStore();
        try
        {
            var snap = radio2.Snapshot();
            Assert.Equal(33, snap.DrivePct);
            Assert.Equal(-9.0, snap.RxAfGainDb);
            Assert.Equal(11.0, snap.LevelerMaxGainDb);
            Assert.Equal(-7, snap.MicGainDb);
        }
        finally
        {
            radio2.Dispose();
        }
    }
}
