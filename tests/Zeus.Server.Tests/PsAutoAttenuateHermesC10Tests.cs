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

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Dsp;
using Zeus.Protocol1;
using Zeus.Server;

namespace Zeus.Server.Tests;

/// <summary>
/// HermesC10 (ANAN-G2E) P1 auto-attenuate — the <c>Tick1HermesC10P1</c>
/// dance driven through the REAL <see cref="PsAutoAttenuateService.Tick1"/>
/// gate + dispatch chain, against a real (constructed, never connected — no
/// socket I/O) <see cref="Protocol1Client"/> so the wire value lands in the
/// same atten_on_Tx plumbing the radio reads. Covers: board dispatch (C10
/// dances, every other P1 board still skips), the plain mi0bot walk math
/// (tooHot / tooQuiet, full-ddB step), the 0..31 clamps, the
/// AutoAttenuate-off skip, the fb-zero skip (deliberately NO
/// stall-acquisition walk — fb-zero recovery is the operator's manual
/// control), the one-disable/one-restore SetPsControl bracket, the
/// no-new-calc gate, and the arm-edge ground-truth baseline (silicon 31,
/// never a phantom 0).
///
/// Also pins the GH #426 engine-arm guard
/// (<see cref="DspPipelineService.P1PsEngineArmSupported"/>) board-by-board:
/// HermesC10 now arms the engine; every other P1 board still skips.
/// </summary>
public class PsAutoAttenuateHermesC10Tests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-psattc10-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var suffix in new[] { "", ".pa" })
        {
            try { if (File.Exists(_dbPath + suffix)) File.Delete(_dbPath + suffix); } catch { }
        }
    }

    // Hot / in-window / quiet feedback levels around the mi0bot window
    // [128, 181] with ideal 152.293 (PsAutoAttenuateService thresholds).
    private static PsStageMeters Meters(float feedback, int calibrationAttempts) => new(
        FeedbackLevel: feedback,
        CalState: 8,               // STAYON — outside the wedge-watchdog 4..7 window
        Correcting: true,
        CorrectionDb: 0f,
        MaxTxEnvelope: 0.5f,
        CalibrationAttempts: calibrationAttempts);

    private sealed class Harness : IDisposable
    {
        public RadioService Radio = null!;
        public TxService Tx = null!;
        public Protocol1Client Client = null!;
        public FakePsEngine Engine = null!;
        public PsAutoAttenuateService Svc = null!;

        public void Dispose()
        {
            Radio.SetActiveClientForTest(null);
            Client.Dispose();
            Radio.Dispose();
        }
    }

    private Harness Build(HpsdrBoardKind board)
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var dspStore = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _dbPath);
        var paStore = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath + ".pa");
        var radio = new RadioService(loggerFactory, dspStore, paStore);

        // Constructed but never connected — RadioService then reports the
        // client's board kind and IsConnected without any socket I/O.
        var client = new Protocol1Client();
        client.SetBoardKind(board);
        radio.SetActiveClientForTest(client);

        var hub = new StreamingHub(new NullLogger<StreamingHub>());
        var engine = new FakePsEngine();
        var pipe = new TestPipeline(radio, hub, loggerFactory, engine);
        var tx = new TxService(radio, pipe, hub, NullBandPlanService.Instance, new NullLogger<TxService>());
        var svc = new PsAutoAttenuateService(radio, tx, pipe, NullLogger<PsAutoAttenuateService>.Instance);

        return new Harness { Radio = radio, Tx = tx, Client = client, Engine = engine, Svc = svc };
    }

    // Arm PS (auto-cal mode, the operator default) and key MOX, then clear
    // any engine calls the arm path recorded so the assertions see the dance
    // bracket alone.
    private static void ArmAndKey(Harness h)
    {
        h.Radio.SetPs(new PsControlSetRequest(Enabled: true, Auto: true, Single: false));
        Assert.True(h.Tx.TrySetMox(true, out var err), $"TrySetMox failed: {err}");
        h.Engine.PsControlCalls.Clear();
    }

    // ---- Dispatch -----------------------------------------------------------

    [Fact]
    public void C10Board_HotFeedback_DancesAndWiresAttenOnTx()
    {
        using var h = Build(HpsdrBoardKind.HermesC10);
        h.Client.SetPsTxAttenOnTxDb(10);
        ArmAndKey(h);
        // fb=250 → tooHot; ddB = 20·log10(250/152.293) = 4.31 → step +4.
        h.Engine.Meters = Meters(250, calibrationAttempts: 1);

        h.Svc.Tick1();   // Monitor: disable PS, compute delta
        h.Svc.Tick1();   // SetNewValues: 10 + 4 → 14 on the wire + persisted
        h.Svc.Tick1();   // RestoreOperation: re-arm with saved cal-mode

        Assert.Equal(14, h.Client.PsTxAttenOnTxDb);
        Assert.Equal(14, h.Radio.Snapshot().PsTxFeedbackAttenuationDb);
        // Exactly one disable/restore bracket, restoring the operator's
        // (auto=true, single=false) — mi0bot PSForm.cs:763/:790-815.
        Assert.Equal(new[] { (false, false), (true, false) }, h.Engine.PsControlCalls);
    }

    [Theory]
    [InlineData(HpsdrBoardKind.Metis)]
    [InlineData(HpsdrBoardKind.Hermes)]
    [InlineData(HpsdrBoardKind.HermesII)]
    [InlineData(HpsdrBoardKind.Angelia)]
    [InlineData(HpsdrBoardKind.Orion)]
    public void NonC10P1Board_HotFeedback_StillSkips(HpsdrBoardKind board)
    {
        // Every other P1 board has no PS feedback attenuator: the dispatch
        // must fall through to the P2 branch and land on skip=p2-null (no
        // P2 client here) — no engine bracket, no wire write, no persist.
        using var h = Build(board);
        h.Client.SetPsTxAttenOnTxDb(10);   // stale value must stay untouched
        ArmAndKey(h);
        int attnBefore = h.Radio.Snapshot().PsTxFeedbackAttenuationDb;
        h.Engine.Meters = Meters(250, calibrationAttempts: 1);

        h.Svc.Tick1();
        h.Svc.Tick1();
        h.Svc.Tick1();

        Assert.Equal(10, h.Client.PsTxAttenOnTxDb);
        Assert.Equal(attnBefore, h.Radio.Snapshot().PsTxFeedbackAttenuationDb);
        Assert.Empty(h.Engine.PsControlCalls);
    }

    // ---- Gates --------------------------------------------------------------

    [Fact]
    public void AutoAttenuateOff_SkipsDance()
    {
        using var h = Build(HpsdrBoardKind.HermesC10);
        h.Client.SetPsTxAttenOnTxDb(10);
        ArmAndKey(h);
        h.Radio.SetPsAdvanced(new PsAdvancedSetRequest(AutoAttenuate: false));
        h.Engine.Meters = Meters(250, calibrationAttempts: 1);

        h.Svc.Tick1();
        h.Svc.Tick1();

        Assert.Equal(10, h.Client.PsTxAttenOnTxDb);
        Assert.Empty(h.Engine.PsControlCalls);
    }

    [Fact]
    public void FeedbackZero_NeverWalks()
    {
        // SESSION-LEAD AMENDMENT 1: no stall-acquisition heuristics. fb=0
        // (e.g. a heavily over-attenuated external sampler tap) must NOT
        // trigger an automatic walk in either direction — recovery is the
        // operator's manual attenuation control. CalibrationAttempts keeps
        // incrementing so it is specifically the fb>0 gate being pinned.
        using var h = Build(HpsdrBoardKind.HermesC10);
        ArmAndKey(h);

        for (int cal = 1; cal <= 4; cal++)
        {
            h.Engine.Meters = Meters(0, calibrationAttempts: cal);
            h.Svc.Tick1();
        }

        Assert.Equal(31, h.Client.PsTxAttenOnTxDb);   // silicon default, untouched
        Assert.Empty(h.Engine.PsControlCalls);
    }

    [Fact]
    public void FeedbackInWindow_NoDance()
    {
        using var h = Build(HpsdrBoardKind.HermesC10);
        h.Client.SetPsTxAttenOnTxDb(10);
        ArmAndKey(h);
        h.Engine.Meters = Meters(150, calibrationAttempts: 1);   // 128 ≤ fb ≤ 181

        h.Svc.Tick1();
        h.Svc.Tick1();

        Assert.Equal(10, h.Client.PsTxAttenOnTxDb);
        Assert.Empty(h.Engine.PsControlCalls);
    }

    [Fact]
    public void NoNewCalc_GatesTheNextDance()
    {
        // mi0bot PSForm.cs:1097-1099 — step only after calcc completes a NEW
        // fit. With CalibrationAttempts frozen, a still-hot feedback must not
        // start a second dance (stepping mid-fit is the binfo[6] wedge).
        using var h = Build(HpsdrBoardKind.HermesC10);
        h.Client.SetPsTxAttenOnTxDb(10);
        ArmAndKey(h);
        h.Engine.Meters = Meters(250, calibrationAttempts: 1);

        for (int i = 0; i < 6; i++) h.Svc.Tick1();   // dance (3 ticks) + 3 gated ticks

        Assert.Equal(14, h.Client.PsTxAttenOnTxDb);            // stepped exactly once
        Assert.Equal(2, h.Engine.PsControlCalls.Count);        // one bracket only
    }

    // ---- Walk math + clamps --------------------------------------------------

    [Fact]
    public void TooQuiet_WalksDown_ClampsAtZero()
    {
        using var h = Build(HpsdrBoardKind.HermesC10);
        h.Client.SetPsTxAttenOnTxDb(3);
        ArmAndKey(h);
        // fb=100 < 128 with attn > 0 → tooQuiet; ddB = 20·log10(100/152.293)
        // = -3.65 → step -4 → clamp(3 - 4) = 0.
        h.Engine.Meters = Meters(100, calibrationAttempts: 1);

        h.Svc.Tick1();
        h.Svc.Tick1();
        h.Svc.Tick1();

        Assert.Equal(0, h.Client.PsTxAttenOnTxDb);
        Assert.Equal(0, h.Radio.Snapshot().PsTxFeedbackAttenuationDb);
    }

    [Fact]
    public void TooHot_WalksUp_ClampsAt31()
    {
        using var h = Build(HpsdrBoardKind.HermesC10);
        h.Client.SetPsTxAttenOnTxDb(30);
        ArmAndKey(h);
        // fb=2000 → ddB = 22.4 → step +22 → clamp(30 + 22) = 31.
        h.Engine.Meters = Meters(2000, calibrationAttempts: 1);

        h.Svc.Tick1();
        h.Svc.Tick1();
        h.Svc.Tick1();

        Assert.Equal(31, h.Client.PsTxAttenOnTxDb);
    }

    [Fact]
    public void ArmEdge_BaselinesToRadioGroundTruth_Not_PhantomZero()
    {
        // The operator never set a value → the radio is holding the silicon
        // reset 31 (the sentinel read-back). The arm-edge baseline must read
        // that ground truth: a hot dance from 31 computes 31 + 4 → clamps to
        // 31 → NO wire write. A phantom-0 baseline would compute 0 + 4 = 4
        // and slam the wire — the exact desync bug the arm-edge sync fixed.
        using var h = Build(HpsdrBoardKind.HermesC10);
        ArmAndKey(h);
        int attnBefore = h.Radio.Snapshot().PsTxFeedbackAttenuationDb;
        h.Engine.Meters = Meters(250, calibrationAttempts: 1);

        h.Svc.Tick1();
        h.Svc.Tick1();
        h.Svc.Tick1();

        Assert.Equal(31, h.Client.PsTxAttenOnTxDb);
        Assert.Equal(attnBefore, h.Radio.Snapshot().PsTxFeedbackAttenuationDb);
        // The bracket still completes so PS is never left disabled.
        Assert.Equal(new[] { (false, false), (true, false) }, h.Engine.PsControlCalls);
    }

    // ---- Mid-dance recovery ---------------------------------------------------

    [Fact]
    public void UnkeyMidDance_RestoresOperatorCalMode()
    {
        // If MOX drops between the disable and the restore, the not-keyed
        // gate must re-issue SetPsControl with the operator's saved cal-mode
        // — otherwise PS sits disabled in WDSP for the rest of the session.
        using var h = Build(HpsdrBoardKind.HermesC10);
        h.Client.SetPsTxAttenOnTxDb(10);
        ArmAndKey(h);
        h.Engine.Meters = Meters(250, calibrationAttempts: 1);

        h.Svc.Tick1();   // Monitor → disable PS, dance in flight
        Assert.Equal(new[] { (false, false) }, h.Engine.PsControlCalls);

        Assert.True(h.Tx.TrySetMox(false, out _));
        h.Svc.Tick1();   // not-keyed recover path

        Assert.Equal(new[] { (false, false), (true, false) }, h.Engine.PsControlCalls);
        Assert.Equal(10, h.Client.PsTxAttenOnTxDb);   // aborted before the write
    }

    // ---- GH #426 engine-arm guard ---------------------------------------------

    [Theory]
    [InlineData(HpsdrBoardKind.HermesLite2, true)]
    [InlineData(HpsdrBoardKind.HermesC10, true)]    // the G2E carve-out under test
    [InlineData(HpsdrBoardKind.Metis, false)]
    [InlineData(HpsdrBoardKind.Hermes, false)]
    [InlineData(HpsdrBoardKind.HermesII, false)]
    [InlineData(HpsdrBoardKind.Angelia, false)]
    [InlineData(HpsdrBoardKind.Orion, false)]
    [InlineData(HpsdrBoardKind.OrionMkII, false)]
    public void P1PsEngineArmSupported_CarvesOutOnlyHl2AndC10(HpsdrBoardKind board, bool expected)
    {
        // GH #426: arming WDSP PS on a P1 board with no feedback path parks
        // calcc in COLLECT and freezes RX audio + waterfall. Only HL2 and
        // HermesC10 deliver the 4-DDC paired layout; everyone else skips.
        Assert.Equal(expected, DspPipelineService.P1PsEngineArmSupported(p1Connected: true, board));
    }

    [Fact]
    public void P1PsEngineArmSupported_Protocol2_AlwaysArms()
    {
        // No P1 client (Protocol 2 session) → the ANAN-class feedback DDC
        // path exists; board kind is irrelevant to the guard.
        Assert.True(DspPipelineService.P1PsEngineArmSupported(
            p1Connected: false, HpsdrBoardKind.OrionMkII));
        Assert.True(DspPipelineService.P1PsEngineArmSupported(
            p1Connected: false, HpsdrBoardKind.Hermes));
    }

    // ---- Test doubles ----------------------------------------------------------

    // Minimal IDspEngine: controllable PS stage meters + a SetPsControl
    // recording. Everything else is a safe no-op — the auto-attenuate tick
    // never calls the RX/TX processing surface.
    private sealed class FakePsEngine : IDspEngine
    {
        public PsStageMeters Meters = PsStageMeters.Silent;
        public List<(bool Auto, bool Single)> PsControlCalls { get; } = new();

        public PsStageMeters GetPsStageMeters() => Meters;
        public void SetPsControl(bool autoCal, bool singleCal) =>
            PsControlCalls.Add((autoCal, singleCal));

        public int TxBlockSamples => 1024;
        public int TxOutputSamples => 1024;
        public int OpenChannel(int sampleRateHz, int pixelWidth) => 0;
        public void CloseChannel(int channelId) { }
        public void FeedIq(int channelId, ReadOnlySpan<double> interleavedIqSamples) { }
        public void SetMode(int channelId, RxMode mode) { }
        public void SetFilter(int channelId, int lowHz, int highHz) { }
        public void SetVfoHz(int channelId, long vfoHz) { }
        public void SetCtunShift(int channelId, int shiftHz) { }
        public void SetAgcTop(int channelId, double topDb) { }
        public void SetAgcThresh(int channelId, double threshDbm) { }
        public double GetAgcTop(int channelId) => 0.0;
        public double GetAgcThresh(int channelId) => 0.0;
        public void SetAgc(int channelId, AgcConfig cfg) { }
        public void SetSquelch(int channelId, SquelchConfig cfg) { }
        public void SetTxLeveling(int channelId, TxLevelingConfig cfg) { }
        public void SetTxPhaseRotator(int channelId, TxPhaseRotatorConfig cfg) { }
        public void SetRxDisplayFastAttack(int channelId, bool fast) { }
        public void SetRxAfGainDb(int channelId, double db) { }
        public void SetNoiseReduction(int channelId, NrConfig cfg) { }
        public Zeus.Dsp.Nr3ModelLoadResult LoadNr3Model(string? modelFilePath) => Zeus.Dsp.Nr3ModelLoadResult.Unavailable;
        public void SetNotches(IReadOnlyList<NotchDto> notches) { }
        public void SetNotchTuneFrequencyHz(double loHz) { }
        public void SetZoom(int channelId, int level) { }
        public int ReadAudio(int channelId, Span<float> output) => 0;
        public bool TryGetDisplayPixels(int channelId, DisplayPixout which, Span<float> dbOut) => false;
        public bool TryGetTxDisplayPixels(DisplayPixout which, Span<float> dbOut) => false;
        public void ConfigureTxDisplayAnalyzer(int fftSize, int windowType, double avgTauSec) { }
        public bool TryGetPsFeedbackDisplayPixels(DisplayPixout which, Span<float> dbOut) => false;
        public int OpenTxChannel(int outputRateHz = 48_000) => 0;
        public void SetMox(bool moxOn) { }
        public double GetRxaSignalDbm(int channelId) => -140.0;
        public RxStageMeters GetRxStageMeters(int channelId) => RxStageMeters.Silent;
        public void SetTxMode(RxMode mode) { }
        public void SetTxFilter(int lowHz, int highHz) { }
        public void SetRxBandpassWindow(int channelId, BandpassWindow window) { }
        public void SetTxBandpassWindow(BandpassWindow window) { }
        public int ProcessTxBlock(ReadOnlySpan<float> micMono, Span<float> iqInterleaved) => 0;
        public void SetTxTune(bool on) { }
        public TxStageMeters GetTxStageMeters() => TxStageMeters.Silent;
        public void SetTwoTone(bool on, double freq1, double freq2, double mag) { }
        public void SetPsEnabled(bool enabled) { }
        public void SetPsHold(bool hold) { }
        public void SetPsAdvanced(bool ptol, double moxDelaySec, double loopDelaySec,
                                  double ampDelayNs, double hwPeak, int ints, int spi) { }
        public void SetPsHwPeak(double hwPeak) { }
        public void FeedPsFeedbackBlock(ReadOnlySpan<float> txI, ReadOnlySpan<float> txQ,
                                        ReadOnlySpan<float> rxI, ReadOnlySpan<float> rxQ) { }
        public void ResetPs() { }
        public void SavePsCorrection(string path) { }
        public void RestorePsCorrection(string path) { }
        public void SetCfcConfig(CfcConfig cfg) { }
        public void SetTxPanelGain(double linearGain) { }
        public void SetTxLevelerMaxGain(double maxGainDb) { }
        public void SetTxMonitorEnabled(bool enabled) { }
        public int ReadTxMonitorAudio(Span<float> output) => 0;
        public bool IsTxMonitorOn => false;
        public void Dispose() { }
    }

    // Non-hosted subclass: CurrentEngine returns the fake so the tick sees a
    // live engine without the Synthetic/WDSP lifecycle (same pattern as
    // MicGainEndpointTests.TestPipeline).
    private sealed class TestPipeline(
        RadioService radio,
        StreamingHub hub,
        ILoggerFactory logs,
        FakePsEngine engine) : DspPipelineService(radio, hub, Array.Empty<IRxAudioSink>(), logs)
    {
        public override IDspEngine CurrentEngine => engine;
    }
}
