// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;
using Xunit;

namespace Zeus.Server.Tests;

/// <summary>
/// PureSignal settings persistence — guards the round-3 fix where SetPs and
/// SetTwoTone silently failed to call _psStore.Upsert. After the fix, every
/// Set* path that mutates a persisted PS field must drop a doc into the
/// LiteDB-backed store. PsEnabled is a persisted standing preference;
/// TwoToneEnabled remains session-only because it can key the transmitter.
/// </summary>
public class PsPersistenceTests : IDisposable
{
    private readonly string _dbPath;

    public PsPersistenceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zeus-prefs-pstest-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private (RadioService radio, PsSettingsStore store) BuildRadioWithStore()
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var dspStore = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance);
        var paStore = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath);
        var psStore = new PsSettingsStore(NullLogger<PsSettingsStore>.Instance, _dbPath);
        var radio = new RadioService(
            loggerFactory, dspStore, paStore,
            filterPresetStore: null, txIqSource: null,
            preferredRadioStore: null, psStore: psStore);
        return (radio, psStore);
    }

    [Fact]
    public void SetPs_PersistsEnabledAndAutoFlag()
    {
        var (radio, store) = BuildRadioWithStore();

        // Operator arms PS and picks Single mode — persisted fields reflect
        // both the standing arm preference and the cal-mode preference.
        radio.SetPs(new PsControlSetRequest(Enabled: true, Auto: false, Single: true));

        var entry = store.Get();
        Assert.NotNull(entry);
        Assert.True(entry!.Enabled);
        Assert.False(entry!.Auto);
    }

    [Fact]
    public void SetTwoTone_PersistsFreq1Freq2Mag()
    {
        var (radio, store) = BuildRadioWithStore();

        radio.SetTwoTone(new TwoToneSetRequest(
            Enabled: false, Freq1: 750.0, Freq2: 2000.0, Mag: 0.4));

        var entry = store.Get();
        Assert.NotNull(entry);
        Assert.Equal(750.0, entry!.TwoToneFreq1);
        Assert.Equal(2000.0, entry.TwoToneFreq2);
        Assert.Equal(0.4, entry.TwoToneMag);
    }

    [Fact]
    public void SetTwoTone_PartialFields_PreservesUnsetTunings()
    {
        // Operator changes only freq1 — freq2 and mag should keep their
        // existing StateDto / persisted values, not flip to defaults.
        var (radio, store) = BuildRadioWithStore();

        // First seed: full set.
        radio.SetTwoTone(new TwoToneSetRequest(
            Enabled: false, Freq1: 800.0, Freq2: 2100.0, Mag: 0.45));
        // Second call: only freq1 supplied.
        radio.SetTwoTone(new TwoToneSetRequest(
            Enabled: false, Freq1: 850.0));

        var entry = store.Get();
        Assert.NotNull(entry);
        Assert.Equal(850.0, entry!.TwoToneFreq1);
        Assert.Equal(2100.0, entry.TwoToneFreq2);
        Assert.Equal(0.45, entry.TwoToneMag);
    }

    [Fact]
    public void SetPsAdvanced_PersistsTunings()
    {
        var (radio, store) = BuildRadioWithStore();

        radio.SetPsAdvanced(new PsAdvancedSetRequest(
            Ptol: true,
            AutoAttenuate: false,
            MoxDelaySec: 0.5,
            LoopDelaySec: 0.1,
            AmpDelayNs: 200.0,
            HwPeak: null,
            IntsSpiPreset: "8/512"));

        var entry = store.Get();
        Assert.NotNull(entry);
        Assert.True(entry!.Ptol);
        Assert.False(entry.AutoAttenuate);
        Assert.Equal(0.5, entry.MoxDelaySec);
        Assert.Equal(0.1, entry.LoopDelaySec);
        Assert.Equal(200.0, entry.AmpDelayNs);
        Assert.Equal("8/512", entry.IntsSpiPreset);
    }

    [Fact]
    public void SetPsFeedbackSource_PersistsSource()
    {
        var (radio, store) = BuildRadioWithStore();

        radio.SetPsFeedbackSource(new PsFeedbackSourceSetRequest(PsFeedbackSource.External));

        var entry = store.Get();
        Assert.NotNull(entry);
        Assert.Equal(PsFeedbackSource.External, entry!.Source);
    }

    [Fact]
    public void NewRadioService_RehydratesPersistedFields()
    {
        // Round-trip: write via one RadioService, restart, read via second.
        var (radio1, _) = BuildRadioWithStore();
        radio1.SetPs(new PsControlSetRequest(Enabled: true, Auto: false, Single: false));
        radio1.SetTwoTone(new TwoToneSetRequest(
            Enabled: false, Freq1: 900.0, Freq2: 2200.0, Mag: 0.42));
        radio1.SetPsAdvanced(new PsAdvancedSetRequest(
            MoxDelaySec: 0.35,
            IntsSpiPreset: "16/512"));
        radio1.SetPsFeedbackSource(new PsFeedbackSourceSetRequest(PsFeedbackSource.External));

        // Build a fresh RadioService against the same on-disk DB (same _dbPath).
        var (radio2, _) = BuildRadioWithStore();
        var snap = radio2.Snapshot();

        Assert.True(snap.PsEnabled);
        Assert.False(snap.PsAuto);
        Assert.Equal(900.0, snap.TwoToneFreq1);
        Assert.Equal(2200.0, snap.TwoToneFreq2);
        Assert.Equal(0.42, snap.TwoToneMag);
        Assert.Equal(0.35, snap.PsMoxDelaySec);
        Assert.Equal("16/512", snap.PsIntsSpiPreset);
        Assert.Equal(PsFeedbackSource.External, snap.PsFeedbackSource);
        // Transmit/keying arm should NOT survive — TwoToneEnabled stays false
        // on every fresh session even if Enabled=true had been set previously.
        Assert.False(snap.TwoToneEnabled);
    }

    [Fact]
    public void PsHwPeak_PersistsPerBoard_AndSurvivesRestart()
    {
        // KB2UKA's symptom: he hand-calibrated HW Peak to 0.655 for the G2 +
        // RF2K-S external sample-tap chain (factory default 0.6121 is too low
        // for that physical chain — Observed bar pegs red). Backend restart
        // clobbered it back to 0.6121 because the old code re-derived per
        // connect without consulting persisted state. This guards that the
        // operator-calibrated value survives a restart and only applies to
        // the matching board key.
        var (radio1, store) = BuildRadioWithStore();
        // Simulate a P2 G2 connect so RadioService caches the right board key.
        // Variant default is G2 — that's what EffectiveOrionMkIIVariant returns
        // without an explicit override, matching KB2UKA's bench.
        radio1.ApplyPsHwPeakForConnection(isProtocol2: true, board: HpsdrBoardKind.OrionMkII);
        var pristine = radio1.Snapshot();
        Assert.Equal(0.6121, pristine.PsHwPeak);          // factory default
        Assert.Equal(0.6121, pristine.PsHwPeakDefault);

        // Operator dials in 0.655 — write should land in the per-board slot.
        radio1.SetPsAdvanced(new PsAdvancedSetRequest(HwPeak: 0.655));
        Assert.Equal(0.655, radio1.Snapshot().PsHwPeak);
        var entry = store.Get();
        Assert.NotNull(entry);
        string g2Key = RadioService.GetPsBoardKey(true, HpsdrBoardKind.OrionMkII, OrionMkIIVariant.G2);
        Assert.True(entry!.HwPeakByBoard.ContainsKey(g2Key));
        Assert.Equal(0.655, entry.HwPeakByBoard[g2Key]);

        // Fresh RadioService against the same on-disk DB — re-connect to the
        // G2 should restore 0.655, NOT the factory 0.6121.
        var (radio2, _) = BuildRadioWithStore();
        radio2.ApplyPsHwPeakForConnection(isProtocol2: true, board: HpsdrBoardKind.OrionMkII);
        var restored = radio2.Snapshot();
        Assert.Equal(0.655, restored.PsHwPeak);            // operator value wins
        Assert.Equal(0.6121, restored.PsHwPeakDefault);    // factory default still surfaced for the UI hint
    }

    [Fact]
    public void PsHwPeak_PerBoardSlots_DontLeakAcrossBoards()
    {
        // Operator who switches between an HL2 and a G2 (KB2UKA does) should
        // get the right calibrated value per radio. Setting on one board
        // must not stomp the other's entry.
        var (radio1, store) = BuildRadioWithStore();
        // Calibrate the G2 first.
        radio1.ApplyPsHwPeakForConnection(isProtocol2: true, board: HpsdrBoardKind.OrionMkII);
        radio1.SetPsAdvanced(new PsAdvancedSetRequest(HwPeak: 0.655));

        // Switch to HL2 (P1) — connect should pull the HL2 factory default
        // (0.233, mi0bot clsHardwareSpecific.cs:312), NOT the G2's
        // operator-tuned 0.655.
        radio1.ApplyPsHwPeakForConnection(isProtocol2: false, board: HpsdrBoardKind.HermesLite2);
        Assert.Equal(0.233, radio1.Snapshot().PsHwPeak);

        // Operator calibrates the HL2 to 0.21 — write should land in the HL2
        // slot, leaving the G2 slot untouched.
        radio1.SetPsAdvanced(new PsAdvancedSetRequest(HwPeak: 0.21));
        Assert.Equal(0.21, radio1.Snapshot().PsHwPeak);

        // Fresh RadioService — verify both per-board values survived and
        // route to the correct board.
        var (radio2, _) = BuildRadioWithStore();
        radio2.ApplyPsHwPeakForConnection(isProtocol2: true, board: HpsdrBoardKind.OrionMkII);
        Assert.Equal(0.655, radio2.Snapshot().PsHwPeak);
        radio2.ApplyPsHwPeakForConnection(isProtocol2: false, board: HpsdrBoardKind.HermesLite2);
        Assert.Equal(0.21, radio2.Snapshot().PsHwPeak);

        // And the on-disk record contains both keys.
        var entry = store.Get();
        Assert.NotNull(entry);
        Assert.Equal(2, entry!.HwPeakByBoard.Count);
        Assert.Equal(0.655, entry.HwPeakByBoard[RadioService.GetPsBoardKey(true, HpsdrBoardKind.OrionMkII, OrionMkIIVariant.G2)]);
        Assert.Equal(0.21, entry.HwPeakByBoard[RadioService.GetPsBoardKey(false, HpsdrBoardKind.HermesLite2, OrionMkIIVariant.G2)]);
    }

    [Fact]
    public void PsHwPeak_DisconnectedSetAdvanced_DoesNotPollutePreviousBoardSlot()
    {
        // After disconnect, _currentPsBoardKey is cleared. A SetPsAdvanced
        // call in that state should leave the previous radio's slot intact
        // (operator dialling in the panel while disconnected must NOT
        // overwrite a different radio's calibrated value).
        var (radio, store) = BuildRadioWithStore();
        radio.ApplyPsHwPeakForConnection(isProtocol2: true, board: HpsdrBoardKind.OrionMkII);
        radio.SetPsAdvanced(new PsAdvancedSetRequest(HwPeak: 0.655));
        // Pseudo-disconnect — DisconnectAsync would also tear down a P1
        // client, but we only need the state-clearing portion for this test.
        radio.MarkProtocol2Disconnected();
        // Operator drags the slider while disconnected — value lands in the
        // live state but should NOT touch the per-board store entry.
        radio.SetPsAdvanced(new PsAdvancedSetRequest(HwPeak: 0.999));

        var entry = store.Get();
        Assert.NotNull(entry);
        string g2Key = RadioService.GetPsBoardKey(true, HpsdrBoardKind.OrionMkII, OrionMkIIVariant.G2);
        Assert.Equal(0.655, entry!.HwPeakByBoard[g2Key]);  // G2 slot untouched
    }
}
