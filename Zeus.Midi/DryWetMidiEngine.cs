// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;

namespace Zeus.Midi;

/// <summary>
/// Real cross-platform MIDI input engine backed by Melanchall.DryWetMidi's
/// Multimedia API (pure-managed; Win/macOS/Linux/Pi). Opens every present input
/// device, listens, and normalizes raw MIDI into <see cref="MidiInputMessage"/>.
///
/// Every CC event carries BOTH an absolute <see cref="MidiInputMessage.Value"/>
/// (0..127) AND a relative <see cref="MidiInputMessage.Delta"/> (two's-complement
/// encoder reading, 1 = +1 … 127 = -1) so the binding's control type — knob vs
/// wheel — decides which to consume; the engine cannot know a control's role.
///
/// All backend calls are guarded: if the platform has no MIDI subsystem the
/// engine reports <see cref="IsAvailable"/> = false and never throws, so the
/// server always starts.
/// </summary>
public sealed class DryWetMidiEngine : IMidiEngine
{
    private readonly ILogger<DryWetMidiEngine> _log;
    private readonly object _sync = new();
    private readonly List<InputDevice> _open = new();
    private bool _available = true;
    private bool _watcherHooked;
    private bool _disposed;

    public DryWetMidiEngine(ILogger<DryWetMidiEngine>? log = null)
        => _log = log ?? NullLogger<DryWetMidiEngine>.Instance;

    public bool IsAvailable => _available;

    public event Action<MidiInputMessage>? MessageReceived;
    public event Action? DevicesChanged;

    public IReadOnlyList<MidiDeviceDto> EnumerateDevices()
    {
        try
        {
            var list = new List<MidiDeviceDto>();
            foreach (var d in InputDevice.GetAll())
                list.Add(new MidiDeviceDto(d.Name, Connected: true));
            return list;
        }
        catch (Exception ex)
        {
            _available = false;
            _log.LogDebug(ex, "midi.enumerate failed; engine unavailable");
            return Array.Empty<MidiDeviceDto>();
        }
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_disposed) return;
            CloseAllLocked();
            try
            {
                HookWatcherLocked();
                foreach (var dev in InputDevice.GetAll())
                {
                    try
                    {
                        dev.EventReceived += OnEventReceived;
                        dev.StartEventsListening();
                        _open.Add(dev);
                        _log.LogInformation("midi.device.open name={Name}", dev.Name);
                    }
                    catch (Exception ex)
                    {
                        _log.LogDebug(ex, "midi.device.open failed name={Name}", dev.Name);
                        dev.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                _available = false;
                _log.LogDebug(ex, "midi.start failed; engine unavailable");
            }
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            CloseAllLocked();
        }
    }

    private void HookWatcherLocked()
    {
        if (_watcherHooked) return;
        try
        {
            DevicesWatcher.Instance.DeviceAdded += OnDevicesChanged;
            DevicesWatcher.Instance.DeviceRemoved += OnDevicesChanged;
            _watcherHooked = true;
        }
        catch (Exception ex)
        {
            // Hot-plug is best-effort; absence is not fatal.
            _log.LogDebug(ex, "midi.watcher hook failed (hot-plug disabled)");
        }
    }

    private void OnDevicesChanged(object? sender, DeviceAddedRemovedEventArgs e)
    {
        try { DevicesChanged?.Invoke(); }
        catch (Exception ex) { _log.LogDebug(ex, "midi.devices-changed handler threw"); }
    }

    private void CloseAllLocked()
    {
        foreach (var dev in _open)
        {
            try
            {
                dev.EventReceived -= OnEventReceived;
                dev.StopEventsListening();
                dev.Dispose();
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "midi.device.close failed name={Name}", dev.Name);
            }
        }
        _open.Clear();
    }

    private void OnEventReceived(object? sender, MidiEventReceivedEventArgs e)
    {
        var deviceName = (sender as InputDevice)?.Name ?? "MIDI";
        var msg = Normalize(deviceName, e.Event);
        if (msg is { } m)
        {
            try { MessageReceived?.Invoke(m); }
            catch (Exception ex) { _log.LogDebug(ex, "midi.message handler threw"); }
        }
    }

    /// <summary>Map a raw MIDI event to a normalized control message, or null
    /// for events we do not surface (clock, sysex, aftertouch, …). Internal so
    /// unit tests can exercise the classification without real I/O.</summary>
    internal static MidiInputMessage? Normalize(string deviceName, MidiEvent ev)
    {
        switch (ev)
        {
            case NoteOnEvent n:
            {
                int ch = n.Channel;
                int note = n.NoteNumber;
                int vel = n.Velocity;
                return new MidiInputMessage(
                    deviceName, MidiControlType.Button, $"note:{ch}:{note}", vel, 0);
            }
            case NoteOffEvent n:
            {
                int ch = n.Channel;
                int note = n.NoteNumber;
                return new MidiInputMessage(
                    deviceName, MidiControlType.Button, $"note:{ch}:{note}", 0, 0);
            }
            case ControlChangeEvent cc:
            {
                int ch = cc.Channel;
                int num = cc.ControlNumber;
                int val = cc.ControlValue;
                // Two's-complement relative reading: 1..63 = +1..+63, 65..127 = -63..-1.
                int delta = val < 64 ? val : val - 128;
                return new MidiInputMessage(
                    deviceName, MidiControlType.KnobOrSlider, $"cc:{ch}:{num}", val, delta);
            }
            case PitchBendEvent pb:
            {
                int ch = pb.Channel;
                // 14-bit pitch (0..16383) → 0..127 absolute reading.
                int val = pb.PitchValue >> 7;
                return new MidiInputMessage(
                    deviceName, MidiControlType.KnobOrSlider, $"pitch:{ch}", val, 0);
            }
            default:
                return null;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            CloseAllLocked();
            if (_watcherHooked)
            {
                try
                {
                    DevicesWatcher.Instance.DeviceAdded -= OnDevicesChanged;
                    DevicesWatcher.Instance.DeviceRemoved -= OnDevicesChanged;
                }
                catch (Exception ex) { _log.LogDebug(ex, "midi.watcher unhook failed"); }
                _watcherHooked = false;
            }
        }
    }
}
