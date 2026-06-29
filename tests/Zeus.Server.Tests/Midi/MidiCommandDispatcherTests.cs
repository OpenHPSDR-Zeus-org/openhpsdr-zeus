// SPDX-License-Identifier: GPL-2.0-or-later
//
// Dispatch-level tests for the MIDI command surface (issue #18), driving the
// real RadioService / TxService through MidiCommandDispatcher. Focus: a sample
// of supported commands hit the right seam, knob/wheel scaling is correct, MOX
// keys via MoxSource.Midi only on a press, and parity-only commands are safe
// no-ops. No real MIDI/HID I/O — the dispatcher takes already-resolved commands.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;
using Zeus.Server.Midi;

namespace Zeus.Server.Tests.Midi;

public sealed class MidiCommandDispatcherTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-midi-disp-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (File.Exists(_dbPath + ".pa")) File.Delete(_dbPath + ".pa"); } catch { }
    }

    private (MidiCommandDispatcher D, RadioService Radio, TxService Tx) Build()
    {
        var lf = NullLoggerFactory.Instance;
        var dspStore = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _dbPath);
        var paStore = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath + ".pa");
        var radio = new RadioService(lf, dspStore, paStore);
        radio.MarkProtocol2Connected("127.0.0.1:1024", 48_000);
        var hub = new StreamingHub(new NullLogger<StreamingHub>());
        var pipeline = new DspPipelineService(radio, hub, Array.Empty<IRxAudioSink>(), lf);
        var tx = new TxService(radio, pipeline, hub, NullBandPlanService.Instance, new NullLogger<TxService>());
        var state = new MidiDispatchState(radio.Snapshot);
        var disp = new MidiCommandDispatcher(radio, tx, state, NullLogger<MidiCommandDispatcher>.Instance);
        return (disp, radio, tx);
    }

    [Fact]
    public void ModeUsb_SetsRx1Mode()
    {
        var (d, radio, _) = Build();
        radio.SetMode(RxMode.LSB);
        d.Dispatch(ZeusMidiCommand.ModeUSB, value: 127, delta: 0);
        Assert.Equal(RxMode.USB, radio.Snapshot().Mode);
    }

    [Fact]
    public void BandUp_StepsToNextHigherBand()
    {
        var (d, radio, _) = Build();
        radio.SetVfo(14_175_000); // 20m
        d.Dispatch(ZeusMidiCommand.BandUp, value: 127, delta: 0);
        Assert.Equal("17m", BandUtils.FreqToBand(radio.Snapshot().VfoHz));
    }

    [Fact]
    public void Band40m_TunesToBandCenter()
    {
        var (d, radio, _) = Build();
        d.Dispatch(ZeusMidiCommand.Band40m, value: 127, delta: 0);
        Assert.Equal(7_175_000, radio.Snapshot().VfoHz);
    }

    [Fact]
    public void DriveLevel_KnobScalesToFullPercent()
    {
        var (d, radio, _) = Build();
        d.Dispatch(ZeusMidiCommand.DriveLevel, value: 127, delta: 0);
        Assert.Equal(100, radio.Snapshot().DrivePct);
        d.Dispatch(ZeusMidiCommand.DriveLevel, value: 0, delta: 0);
        Assert.Equal(0, radio.Snapshot().DrivePct);
    }

    [Fact]
    public void SetAfGain_KnobReachesUpperClamp()
    {
        var (d, radio, _) = Build();
        d.Dispatch(ZeusMidiCommand.SetAfGain, value: 127, delta: 0);
        Assert.Equal(20.0, radio.Snapshot().RxAfGainDb, 3);
    }

    [Fact]
    public void SquelchLevel_KnobScalesToZeroHundred()
    {
        var (d, radio, _) = Build();
        d.Dispatch(ZeusMidiCommand.SquelchLevel, value: 127, delta: 0);
        Assert.Equal(100, radio.Snapshot().Squelch?.Level);
    }

    [Fact]
    public void ChangeFreqVfoA_WheelMovesByTenHzPerDetent()
    {
        var (d, radio, _) = Build();
        radio.SetVfo(14_200_000);
        d.Dispatch(ZeusMidiCommand.ChangeFreqVfoA, value: 0, delta: 5);
        Assert.Equal(14_200_050, radio.Snapshot().VfoHz);
        d.Dispatch(ZeusMidiCommand.ChangeFreqVfoA, value: 0, delta: -10);
        Assert.Equal(14_199_950, radio.Snapshot().VfoHz);
    }

    [Fact]
    public void MoxOnOff_KeysOnPressAndUnkeysOnNextPress()
    {
        var (d, _, tx) = Build();
        Assert.False(tx.IsMoxOn);
        d.Dispatch(ZeusMidiCommand.MoxOnOff, value: 127, delta: 0);
        Assert.True(tx.IsMoxOn);
        d.Dispatch(ZeusMidiCommand.MoxOnOff, value: 127, delta: 0);
        Assert.False(tx.IsMoxOn);
    }

    [Fact]
    public void RitOnOff_TogglesFromSnapshot()
    {
        var (d, radio, _) = Build();
        Assert.False(radio.Snapshot().RitEnabled);
        d.Dispatch(ZeusMidiCommand.RitOnOff, value: 127, delta: 0);
        Assert.True(radio.Snapshot().RitEnabled);
    }

    [Fact]
    public void UnsupportedCommands_AreSafeNoOps()
    {
        var (d, radio, _) = Build();
        radio.SetVfo(14_200_000);
        var before = radio.Snapshot().VfoHz;
        // Parity-only commands with empty case bodies must not throw or mutate.
        d.Dispatch(ZeusMidiCommand.Band2m, value: 127, delta: 0);
        d.Dispatch(ZeusMidiCommand.TuningStepUp, value: 127, delta: 0);
        d.Dispatch(ZeusMidiCommand.VacOnOff, value: 127, delta: 0);
        d.Dispatch(ZeusMidiCommand.ApfOnOff, value: 127, delta: 0);
        Assert.Equal(before, radio.Snapshot().VfoHz);
    }

    [Fact]
    public void WheelSensitivityCommands_AreParityNoOps()
    {
        // VFO wheel sensitivity is not wired to any tune path (catalogued
        // Supported=false), so these must be safe no-ops — no throw, no tuning.
        var (d, radio, _) = Build();
        radio.SetVfo(14_200_000);
        var before = radio.Snapshot().VfoHz;
        d.Dispatch(ZeusMidiCommand.MidiMessagesPerTuneStepUp, value: 127, delta: 0);
        d.Dispatch(ZeusMidiCommand.MidiMessagesPerTuneStepDown, value: 127, delta: 0);
        d.Dispatch(ZeusMidiCommand.MidiMessagesPerTuneStepToggle, value: 127, delta: 0);
        Assert.Equal(before, radio.Snapshot().VfoHz);
    }

    [Fact]
    public void MuteOnOff_TogglesFromLiveSnapshotState()
    {
        // A non-MIDI mute change (here: direct seam call) must not desync the
        // MIDI toggle — the next press reads live state and flips it correctly.
        var (d, radio, _) = Build();
        Assert.False(radio.Snapshot().Rx1Muted);
        d.Dispatch(ZeusMidiCommand.MuteOnOff, value: 127, delta: 0);
        Assert.True(radio.Snapshot().Rx1Muted);

        // External unmute (web UI / CAT), then a MIDI press must mute again —
        // not no-op (which a shadow latch would have done).
        radio.SetReceiverMuted(0, false);
        Assert.False(radio.Snapshot().Rx1Muted);
        d.Dispatch(ZeusMidiCommand.MuteOnOff, value: 127, delta: 0);
        Assert.True(radio.Snapshot().Rx1Muted);
    }

    [Fact]
    public void MoxOnOff_DoesNotKeyOnButtonRelease()
    {
        var (d, _, tx) = Build();
        Assert.False(tx.IsMoxOn);
        // A release (value <= 0) must never key TX.
        d.Dispatch(ZeusMidiCommand.MoxOnOff, value: 0, delta: 0);
        Assert.False(tx.IsMoxOn);
    }
}
