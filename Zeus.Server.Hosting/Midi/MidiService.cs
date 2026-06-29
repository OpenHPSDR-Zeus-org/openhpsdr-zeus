// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Zeus.Contracts;
using Zeus.Midi;

namespace Zeus.Server.Midi;

/// <summary>
/// Owns the MIDI + Stream Deck input pipeline: it loads the persisted bindings,
/// opens both engines (real on a desktop, Null on headless/CI), routes every
/// inbound control event through <see cref="MidiCommandDispatcher"/> to the
/// verified radio seams, handles hot-plug, and — only while the settings panel
/// is in Learn mode — forwards raw control events to the UI as
/// <see cref="MidiLearnFrame"/> hub pushes.
///
/// Safety: MIDI is an input-only path. Buttons act on the key-DOWN edge only
/// (so a toggle command flips exactly once per press), TX keying flows through
/// <see cref="MoxSource.Midi"/>, and PureSignal arm is not in the command
/// surface at all. Engines never throw on absent hardware — the server starts
/// regardless of whether any controller is attached.
/// </summary>
public sealed class MidiService : IHostedService, IDisposable
{
    private const byte MidiLearnMsgType = (byte)MsgType.MidiLearn;

    // Learn frames are decoded by the same frontend that consumes the REST
    // endpoints, so they must match those options: camelCase properties + string
    // enums (the HTTP pipeline's JsonStringEnumConverter). The default options
    // would emit PascalCase + numeric enums and the panel would mis-read them.
    private static readonly JsonSerializerOptions LearnJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly IMidiEngine _midi;
    private readonly IStreamDeckEngine _streamDeck;
    private readonly MidiConfigStore _store;
    private readonly MidiCommandDispatcher _dispatcher;
    private readonly StreamingHub _hub;
    private readonly ILogger<MidiService> _log;

    private readonly object _sync = new();
    private Dictionary<(string Device, string ControlId), MidiMappingDto> _midiMap = new();
    private Dictionary<(string Serial, int Button), ZeusMidiCommand> _sdMap = new();
    private MidiBindingsDoc _bindings = MidiBindingsDoc.Empty;
    private volatile bool _enabled;
    private volatile bool _learning;
    private bool _started;
    private bool _disposed;

    public MidiService(
        IMidiEngine midi,
        IStreamDeckEngine streamDeck,
        MidiConfigStore store,
        RadioService radio,
        TxService tx,
        StreamingHub hub,
        ILoggerFactory loggerFactory)
    {
        _midi = midi;
        _streamDeck = streamDeck;
        _store = store;
        _hub = hub;
        _log = loggerFactory.CreateLogger<MidiService>();

        var dispatchState = new MidiDispatchState(radio.Snapshot);
        _dispatcher = new MidiCommandDispatcher(
            radio, tx, dispatchState, loggerFactory.CreateLogger<MidiCommandDispatcher>());

        _midi.MessageReceived += OnMidiMessage;
        _midi.DevicesChanged += OnMidiDevicesChanged;
        _streamDeck.InputReceived += OnStreamDeckInput;
        _streamDeck.DevicesChanged += OnStreamDeckDevicesChanged;
    }

    // ---- IHostedService -----------------------------------------------------

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _enabled = _store.GetEnabled();
            _bindings = _store.GetBindings();
            RebuildMapsLocked();
            _started = true;
            if (_enabled) StartEnginesLocked();
        }
        _log.LogInformation(
            "midi.service.start enabled={Enabled} midiAvail={M} streamDeckAvail={S} mappings={Maps} sdMappings={SdMaps}",
            _enabled, _midi.IsAvailable, _streamDeck.IsAvailable, _midiMap.Count, _sdMap.Count);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _learning = false;
            StopEnginesLocked();
            _started = false;
        }
        return Task.CompletedTask;
    }

    // ---- Public API for the REST endpoints ---------------------------------

    public MidiStatusDto GetStatus()
    {
        return new MidiStatusDto(
            Enabled: _enabled,
            MidiEngineAvailable: _midi.IsAvailable,
            StreamDeckEngineAvailable: _streamDeck.IsAvailable,
            MidiDevices: _midi.EnumerateDevices(),
            StreamDeckDevices: _streamDeck.EnumerateDevices(),
            Learning: _learning);
    }

    public MidiConfigDto GetConfig()
    {
        lock (_sync) return new MidiConfigDto(_enabled, _bindings);
    }

    public MidiStatusDto SetConfig(MidiConfigDto config)
    {
        var bindings = Normalize(config.Bindings);
        lock (_sync)
        {
            bool wasEnabled = _enabled;
            _enabled = config.Enabled;
            _bindings = bindings;
            RebuildMapsLocked();
            try { _store.Set(_enabled, _bindings); }
            catch (Exception ex) { _log.LogWarning(ex, "midi.config.persist failed"); }

            if (_started)
            {
                if (_enabled && !wasEnabled) StartEnginesLocked();
                else if (!_enabled && wasEnabled) StopEnginesLocked();
            }
        }
        _log.LogInformation("midi.config.updated enabled={Enabled} mappings={Maps} sdMappings={SdMaps}",
            _enabled, _midiMap.Count, _sdMap.Count);
        return GetStatus();
    }

    /// <summary>Enter Learn mode: inbound control events are forwarded to the UI
    /// and NOT dispatched to the radio. Starts the engines if they were idle so
    /// the operator can learn controls even with MIDI globally disabled.</summary>
    public MidiStatusDto StartLearn()
    {
        lock (_sync)
        {
            _learning = true;
            if (_started && !_enabled) StartEnginesLocked();
        }
        _log.LogInformation("midi.learn.start");
        return GetStatus();
    }

    public MidiStatusDto StopLearn()
    {
        lock (_sync)
        {
            _learning = false;
            // If MIDI is globally disabled, the engines were only running for
            // learn — idle them again.
            if (_started && !_enabled) StopEnginesLocked();
        }
        _log.LogInformation("midi.learn.stop");
        return GetStatus();
    }

    public IReadOnlyList<StreamDeckDeviceDto> GetStreamDeckDevices() => _streamDeck.EnumerateDevices();

    // ---- Engine lifecycle (under _sync) ------------------------------------

    private void StartEnginesLocked()
    {
        try { _midi.Start(); } catch (Exception ex) { _log.LogDebug(ex, "midi.engine.start threw"); }
        try { _streamDeck.Start(); } catch (Exception ex) { _log.LogDebug(ex, "streamdeck.engine.start threw"); }
    }

    private void StopEnginesLocked()
    {
        try { _midi.Stop(); } catch (Exception ex) { _log.LogDebug(ex, "midi.engine.stop threw"); }
        try { _streamDeck.Stop(); } catch (Exception ex) { _log.LogDebug(ex, "streamdeck.engine.stop threw"); }
    }

    private void RebuildMapsLocked()
    {
        var midiMap = new Dictionary<(string, string), MidiMappingDto>();
        foreach (var m in _bindings.Mappings)
            midiMap[(m.DeviceName, m.ControlId)] = m;
        _midiMap = midiMap;

        var sdMap = new Dictionary<(string, int), ZeusMidiCommand>();
        foreach (var m in _bindings.StreamDeckMappings)
            sdMap[(m.Serial, m.ButtonIndex)] = m.Command;
        _sdMap = sdMap;
    }

    private static MidiBindingsDoc Normalize(MidiBindingsDoc? doc)
    {
        if (doc is null) return MidiBindingsDoc.Empty;
        return new MidiBindingsDoc(
            MidiBindingsDoc.CurrentVersion,
            doc.Mappings ?? new List<MidiMappingDto>(),
            doc.StreamDeckMappings ?? new List<StreamDeckMappingDto>());
    }

    // ---- Event routing ------------------------------------------------------

    private void OnMidiMessage(MidiInputMessage msg)
    {
        if (_learning)
        {
            PublishLearnFrame(new MidiLearnFrame(
                msg.DeviceName, msg.ControlId, msg.ControlType, msg.Value, msg.Delta));
            return;
        }
        if (!_enabled) return;

        MidiMappingDto map;
        var local = _midiMap;
        if (!local.TryGetValue((msg.DeviceName, msg.ControlId), out map!)) return;

        switch (map.ControlType)
        {
            case MidiControlType.Button:
                // Act on key-down only — a toggle command flips exactly once per
                // press; a momentary command's own `value > 0` guard still holds.
                if (msg.Value <= 0) return;
                _dispatcher.Dispatch(map.Command, value: msg.Value, delta: 0);
                break;

            case MidiControlType.KnobOrSlider:
            {
                // Remap the control's usable [Min,Max] travel onto the full
                // 0..127 the dispatch scalers expect, so a partial-throw fader
                // still reaches both extremes.
                int v = RemapToMidi(msg.Value, map.Min, map.Max);
                _dispatcher.Dispatch(map.Command, value: v, delta: 0);
                break;
            }

            case MidiControlType.Wheel:
                if (msg.Delta == 0) return;
                _dispatcher.Dispatch(map.Command, value: 0, delta: msg.Delta);
                break;
        }
    }

    private void OnStreamDeckInput(StreamDeckInput input)
    {
        if (_learning)
        {
            PublishLearnFrame(new MidiLearnFrame(
                input.DeviceName, $"sd:{input.Serial}:{input.ButtonIndex}",
                MidiControlType.Button, input.Pressed ? 127 : 0, 0));
            return;
        }
        if (!_enabled) return;
        if (!input.Pressed) return; // act on key-down

        var local = _sdMap;
        if (local.TryGetValue((input.Serial, input.ButtonIndex), out var cmd))
            _dispatcher.Dispatch(cmd, value: 127, delta: 0);
    }

    private void OnMidiDevicesChanged()
    {
        lock (_sync)
        {
            // Re-open to pick up the (un)plugged device. Only while engines
            // should be live (enabled or learning).
            if (_started && (_enabled || _learning))
                try { _midi.Start(); } catch (Exception ex) { _log.LogDebug(ex, "midi.reopen threw"); }
        }
    }

    private void OnStreamDeckDevicesChanged()
    {
        lock (_sync)
        {
            if (_started && (_enabled || _learning))
                try { _streamDeck.Start(); } catch (Exception ex) { _log.LogDebug(ex, "streamdeck.reopen threw"); }
        }
    }

    private void PublishLearnFrame(MidiLearnFrame frame)
    {
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(frame, LearnJsonOptions);
            var payload = new byte[json.Length + 1];
            payload[0] = MidiLearnMsgType;
            Buffer.BlockCopy(json, 0, payload, 1, json.Length);
            _hub.BroadcastMidiLearn(payload);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "midi.learn.publish failed");
        }
    }

    /// <summary>Linearly remap a 0..127 reading from its calibrated [min,max]
    /// window onto the full 0..127 range. Identity when min=0,max=127.</summary>
    internal static int RemapToMidi(int value, int min, int max)
    {
        if (max <= min) return Math.Clamp(value, 0, 127);
        int v = Math.Clamp(value, min, max);
        double scaled = (v - min) * 127.0 / (max - min);
        return (int)Math.Round(Math.Clamp(scaled, 0, 127));
    }

    // ---- Dispose ------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _midi.MessageReceived -= OnMidiMessage;
        _midi.DevicesChanged -= OnMidiDevicesChanged;
        _streamDeck.InputReceived -= OnStreamDeckInput;
        _streamDeck.DevicesChanged -= OnStreamDeckDevicesChanged;
        try { _midi.Dispose(); } catch { /* ignore */ }
        try { _streamDeck.Dispose(); } catch { /* ignore */ }
    }
}
