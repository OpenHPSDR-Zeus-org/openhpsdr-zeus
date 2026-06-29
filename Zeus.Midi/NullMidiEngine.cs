// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using Zeus.Contracts;

namespace Zeus.Midi;

/// <summary>A no-device MIDI engine used on headless/CI hosts and in unit
/// tests. It opens nothing (no real MIDI I/O — important: a real device open in
/// a test can crash the Windows CI host), reports no devices, and lets tests
/// drive the pipeline by calling <see cref="Inject"/> with synthetic events.</summary>
public sealed class NullMidiEngine : IMidiEngine
{
    public bool IsAvailable => false;

    public IReadOnlyList<MidiDeviceDto> EnumerateDevices() => Array.Empty<MidiDeviceDto>();

    public void Start() { }
    public void Stop() { }

    public event Action<MidiInputMessage>? MessageReceived;
    public event Action? DevicesChanged;

    /// <summary>Test hook: publish a synthetic inbound control event exactly as
    /// a real device would, with no I/O.</summary>
    public void Inject(MidiInputMessage message) => MessageReceived?.Invoke(message);

    /// <summary>Test hook: simulate a hot-plug add/remove notification.</summary>
    public void RaiseDevicesChanged() => DevicesChanged?.Invoke();

    public void Dispose() { }
}
