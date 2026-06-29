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
        TxService Tx) : IDisposable
    {
        public void Dispose() => Service.Dispose();
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
        return new Harness(service, midi, sd, radio, tx);
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
    public void StatusReportsEnginesUnavailable_WithNullEngines()
    {
        using var h = Build();
        var status = h.Service.GetStatus();
        Assert.False(status.MidiEngineAvailable);
        Assert.False(status.StreamDeckEngineAvailable);
        Assert.Empty(status.MidiDevices);
        Assert.Empty(status.StreamDeckDevices);
    }
}
