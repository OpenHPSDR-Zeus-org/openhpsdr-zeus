// SPDX-License-Identifier: GPL-2.0-or-later
//
// MidiService routing + learn tests (issue #18). Driven entirely by a
// NullMidiEngine / NullStreamDeckEngine with synthetic injected events — NO
// real MIDI/HID I/O (a real device open in a test can crash the Windows CI
// host). Asserts: a mapped button dispatches on key-down only; learn mode
// diverts events away from the radio; key-up does not fire.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Midi;
using Zeus.Server;
using Zeus.Server.Midi;

namespace Zeus.Server.Tests.Midi;

public sealed class MidiServiceTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-midi-svc-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (File.Exists(_dbPath + ".pa")) File.Delete(_dbPath + ".pa"); } catch { }
        try { if (File.Exists(_dbPath + ".midi")) File.Delete(_dbPath + ".midi"); } catch { }
    }

    private sealed record Harness(
        MidiService Service,
        NullMidiEngine Midi,
        NullStreamDeckEngine StreamDeck,
        RadioService Radio,
        TxService Tx,
        MidiConfigStore Store) : IDisposable
    {
        // MidiService does not own the injected store, so the harness must
        // release the SharedLiteDatabase lease — otherwise the open handle
        // blocks the temp .midi db delete on Windows (the lock class the store
        // header warns about). Mirrors CatSerialServiceIntegrationTests.
        public void Dispose()
        {
            Service.Dispose();
            Store.Dispose();
        }
    }

    private Harness Build()
    {
        var lf = NullLoggerFactory.Instance;
        var dspStore = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _dbPath);
        var paStore = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath + ".pa");
        var radio = new RadioService(lf, dspStore, paStore);
        radio.MarkProtocol2Connected("127.0.0.1:1024", 48_000);
        var hub = new StreamingHub(new NullLogger<StreamingHub>());
        var pipeline = new DspPipelineService(radio, hub, Array.Empty<IRxAudioSink>(), lf);
        var tx = new TxService(radio, pipeline, hub, NullBandPlanService.Instance, new NullLogger<TxService>());
        var store = new MidiConfigStore(NullLogger<MidiConfigStore>.Instance, _dbPath + ".midi");
        var midi = new NullMidiEngine();
        var sd = new NullStreamDeckEngine();
        var service = new MidiService(midi, sd, store, radio, tx, hub, lf);
        service.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        return new Harness(service, midi, sd, radio, tx, store);
    }

    private static MidiConfigDto ConfigWith(params MidiMappingDto[] mappings) =>
        new(Enabled: true, new MidiBindingsDoc(
            MidiBindingsDoc.CurrentVersion, mappings, Array.Empty<StreamDeckMappingDto>()));

    [Fact]
    public void MappedButton_DispatchesOnKeyDownOnly()
    {
        using var h = Build();
        h.Service.SetConfig(ConfigWith(
            new MidiMappingDto("DJ", "note:0:62", MidiControlType.Button, ZeusMidiCommand.Band40m)));

        // key-down (velocity > 0) tunes; key-up (velocity 0) is ignored.
        h.Radio.SetVfo(14_200_000);
        h.Midi.Inject(new MidiInputMessage("DJ", MidiControlType.Button, "note:0:62", 127, 0));
        Assert.Equal(7_175_000, h.Radio.Snapshot().VfoHz);

        h.Radio.SetVfo(14_200_000);
        h.Midi.Inject(new MidiInputMessage("DJ", MidiControlType.Button, "note:0:62", 0, 0));
        Assert.Equal(14_200_000, h.Radio.Snapshot().VfoHz);
    }

    [Fact]
    public void UnmappedControl_DoesNothing()
    {
        using var h = Build();
        h.Service.SetConfig(ConfigWith(
            new MidiMappingDto("DJ", "note:0:62", MidiControlType.Button, ZeusMidiCommand.Band40m)));
        h.Radio.SetVfo(14_200_000);
        h.Midi.Inject(new MidiInputMessage("DJ", MidiControlType.Button, "note:0:99", 127, 0));
        Assert.Equal(14_200_000, h.Radio.Snapshot().VfoHz);
    }

    [Fact]
    public void KnobMapping_ScalesAfGain()
    {
        using var h = Build();
        h.Service.SetConfig(ConfigWith(
            new MidiMappingDto("DJ", "cc:0:7", MidiControlType.KnobOrSlider, ZeusMidiCommand.SetAfGain)));
        h.Midi.Inject(new MidiInputMessage("DJ", MidiControlType.KnobOrSlider, "cc:0:7", 127, 63));
        Assert.Equal(20.0, h.Radio.Snapshot().RxAfGainDb, 3);
    }

    [Fact]
    public void WheelMapping_TunesByDelta()
    {
        using var h = Build();
        h.Service.SetConfig(ConfigWith(
            new MidiMappingDto("DJ", "cc:0:20", MidiControlType.Wheel, ZeusMidiCommand.ChangeFreqVfoA)));
        h.Radio.SetVfo(14_200_000);
        h.Midi.Inject(new MidiInputMessage("DJ", MidiControlType.Wheel, "cc:0:20", 0, 4));
        Assert.Equal(14_200_040, h.Radio.Snapshot().VfoHz);
    }

    [Fact]
    public void Disabled_DoesNotDispatch()
    {
        using var h = Build();
        // enabled=false config
        h.Service.SetConfig(new MidiConfigDto(false, new MidiBindingsDoc(
            MidiBindingsDoc.CurrentVersion,
            new[] { new MidiMappingDto("DJ", "note:0:62", MidiControlType.Button, ZeusMidiCommand.Band40m) },
            Array.Empty<StreamDeckMappingDto>())));
        h.Radio.SetVfo(14_200_000);
        h.Midi.Inject(new MidiInputMessage("DJ", MidiControlType.Button, "note:0:62", 127, 0));
        Assert.Equal(14_200_000, h.Radio.Snapshot().VfoHz);
    }

    [Fact]
    public void LearnMode_DivertsEventAwayFromRadio()
    {
        using var h = Build();
        h.Service.SetConfig(ConfigWith(
            new MidiMappingDto("DJ", "note:0:62", MidiControlType.Button, ZeusMidiCommand.Band40m)));
        h.Service.StartLearn();
        Assert.True(h.Service.GetStatus().Learning);

        h.Radio.SetVfo(14_200_000);
        h.Midi.Inject(new MidiInputMessage("DJ", MidiControlType.Button, "note:0:62", 127, 0));
        // While learning, the mapped command must NOT fire.
        Assert.Equal(14_200_000, h.Radio.Snapshot().VfoHz);

        h.Service.StopLearn();
        Assert.False(h.Service.GetStatus().Learning);
        h.Midi.Inject(new MidiInputMessage("DJ", MidiControlType.Button, "note:0:62", 127, 0));
        Assert.Equal(7_175_000, h.Radio.Snapshot().VfoHz);
    }

    [Fact]
    public void StreamDeckMapping_FiresOnPress()
    {
        using var h = Build();
        h.Service.SetConfig(new MidiConfigDto(true, new MidiBindingsDoc(
            MidiBindingsDoc.CurrentVersion,
            Array.Empty<MidiMappingDto>(),
            new[] { new StreamDeckMappingDto("SD1", 3, ZeusMidiCommand.Band20m) })));
        h.Radio.SetVfo(7_100_000);
        h.StreamDeck.Inject(new StreamDeckInput("Stream Deck", "SD1", 3, Pressed: true));
        Assert.Equal(14_175_000, h.Radio.Snapshot().VfoHz);
        // release does nothing
        h.Radio.SetVfo(7_100_000);
        h.StreamDeck.Inject(new StreamDeckInput("Stream Deck", "SD1", 3, Pressed: false));
        Assert.Equal(7_100_000, h.Radio.Snapshot().VfoHz);
    }

    [Fact]
    public void KnobMappedToMox_DoesNotKeyTransmitter()
    {
        using var h = Build();
        // A misguided Learn-mode binding of a fader to MOX must never key TX:
        // the routing layer refuses continuous controls for TX-keying commands.
        h.Service.SetConfig(ConfigWith(
            new MidiMappingDto("DJ", "cc:0:7", MidiControlType.KnobOrSlider, ZeusMidiCommand.MoxOnOff)));
        Assert.False(h.Tx.IsMoxOn);
        h.Midi.Inject(new MidiInputMessage("DJ", MidiControlType.KnobOrSlider, "cc:0:7", 100, 0));
        h.Midi.Inject(new MidiInputMessage("DJ", MidiControlType.KnobOrSlider, "cc:0:7", 64, 0));
        Assert.False(h.Tx.IsMoxOn);
    }

    [Fact]
    public void StatusReportsEnginesUnavailable_WithNullEngines()
    {
        using var h = Build();
        var status = h.Service.GetStatus();
        Assert.False(status.MidiEngineAvailable);
        Assert.False(status.StreamDeckEngineAvailable);
        Assert.Empty(status.MidiDevices);
        Assert.Empty(status.StreamDeckDevices);
    }

    [Fact]
    public void SetConfig_RepairsWheelCommandStoredAsKnobOrSlider()
    {
        // Issue #1231: DryWetMidiEngine classifies every CC event (fader OR
        // jog encoder) as KnobOrSlider, so before the panel's bug-2 fix a jog
        // wheel bound to ChangeFreqVfoA persisted with ControlType=KnobOrSlider
        // and routed through the delta==0 no-op branch. The reconciliation
        // pass in Normalize snaps the stored type to the command's catalogued
        // type so a subsequent inbound CC carrying a real delta actually tunes.
        using var h = Build();
        h.Service.SetConfig(ConfigWith(
            new MidiMappingDto("DJ", "cc:0:20", MidiControlType.KnobOrSlider, ZeusMidiCommand.ChangeFreqVfoA)));

        var stored = h.Service.GetConfig().Bindings.Mappings;
        Assert.Single(stored);
        Assert.Equal(MidiControlType.Wheel, stored[0].ControlType);

        h.Radio.SetVfo(14_200_000);
        h.Midi.Inject(new MidiInputMessage("DJ", MidiControlType.KnobOrSlider, "cc:0:20", 1, 1));
        Assert.Equal(14_200_010, h.Radio.Snapshot().VfoHz);
    }

    [Fact]
    public void SetConfig_LeavesButtonMappingUntouched()
    {
        // The reconciliation must never rewrite a Button binding, since Notes
        // are the only wire shape the router treats as a discrete press.
        using var h = Build();
        h.Service.SetConfig(ConfigWith(
            new MidiMappingDto("DJ", "note:0:62", MidiControlType.Button, ZeusMidiCommand.Band40m)));
        var stored = h.Service.GetConfig().Bindings.Mappings;
        Assert.Equal(MidiControlType.Button, stored[0].ControlType);
    }

    [Fact]
    public void FilterShift_IsCatalogedAsWheelSoSliderBindingRepairsToDelta()
    {
        // Prior to issue #1231 FilterShift was catalogued KnobOrSlider but
        // dispatched from delta, so any slider bound to it was silent. The
        // catalog now agrees with the dispatcher (Wheel), and Normalize
        // repairs a legacy slider binding into a Wheel binding that routes
        // its delta through the dispatcher.
        using var h = Build();
        h.Service.SetConfig(ConfigWith(
            new MidiMappingDto("DJ", "cc:0:30", MidiControlType.KnobOrSlider, ZeusMidiCommand.FilterShift)));
        var stored = h.Service.GetConfig().Bindings.Mappings;
        Assert.Equal(MidiControlType.Wheel, stored[0].ControlType);

        var beforeLow = h.Radio.Snapshot().FilterLowHz;
        var beforeHigh = h.Radio.Snapshot().FilterHighHz;
        h.Midi.Inject(new MidiInputMessage("DJ", MidiControlType.KnobOrSlider, "cc:0:30", 5, 5));
        var afterLow = h.Radio.Snapshot().FilterLowHz;
        var afterHigh = h.Radio.Snapshot().FilterHighHz;
        Assert.Equal(beforeLow + 50, afterLow);
        Assert.Equal(beforeHigh + 50, afterHigh);
    }
}
