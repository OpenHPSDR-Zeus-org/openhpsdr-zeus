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

/// <summary>One normalized inbound MIDI control event. The engine classifies
/// the raw wire shape into a Zeus <see cref="MidiControlType"/> and assigns a
/// stable <see cref="ControlId"/> ("note:&lt;ch&gt;:&lt;num&gt;",
/// "cc:&lt;ch&gt;:&lt;num&gt;", "pitch:&lt;ch&gt;") so a binding can be matched
/// without re-deriving it from raw bytes. <see cref="Value"/> is the absolute
/// 0..127 reading (note velocity / CC value / scaled pitch); <see cref="Delta"/>
/// is the relative encoder step (computed by the engine for two's-complement /
/// 7-bit relative CCs, else 0). Note-off arrives as <see cref="Value"/> = 0.</summary>
public readonly record struct MidiInputMessage(
    string DeviceName,
    MidiControlType ControlType,
    string ControlId,
    int Value,
    int Delta);

/// <summary>Abstraction over a platform MIDI-input backend so the host can be
/// driven by a real engine in production and a synthetic one in tests / on
/// headless CI. Implementations MUST never throw on construction or
/// enumeration when no backend/device is present — they report
/// <see cref="IsAvailable"/> = false and an empty device list instead, so the
/// server always starts.</summary>
public interface IMidiEngine : IDisposable
{
    /// <summary>True when a usable MIDI backend exists on this platform.</summary>
    bool IsAvailable { get; }

    /// <summary>Currently-visible MIDI input devices (never throws).</summary>
    IReadOnlyList<MidiDeviceDto> EnumerateDevices();

    /// <summary>Open every present input device and begin listening. Idempotent.</summary>
    void Start();

    /// <summary>Stop listening and close all devices. Idempotent.</summary>
    void Stop();

    /// <summary>Raised on the engine's thread for each inbound control event.</summary>
    event Action<MidiInputMessage>? MessageReceived;

    /// <summary>Raised when devices are added/removed (hot-plug) so the owner
    /// can re-open and refresh its device list.</summary>
    event Action? DevicesChanged;
}
