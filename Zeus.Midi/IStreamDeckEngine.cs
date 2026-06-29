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

/// <summary>One Elgato Stream Deck key edge. <see cref="Pressed"/> is the new
/// physical state (true = down). The owner maps key-down (Pressed=true) to the
/// bound command, mirroring a MIDI note-on.</summary>
public readonly record struct StreamDeckInput(
    string DeviceName,
    string Serial,
    int ButtonIndex,
    bool Pressed);

/// <summary>Abstraction over a Stream Deck HID backend. Like
/// <see cref="IMidiEngine"/> it MUST degrade gracefully: when HID is
/// unavailable (no device, or Linux/Pi without a udev permission rule) it
/// reports <see cref="IsAvailable"/> = false and an empty device list rather
/// than throwing, so the server always starts.</summary>
public interface IStreamDeckEngine : IDisposable
{
    /// <summary>True when HID enumeration succeeded on this platform.</summary>
    bool IsAvailable { get; }

    /// <summary>Currently-visible Stream Deck devices (never throws).</summary>
    IReadOnlyList<StreamDeckDeviceDto> EnumerateDevices();

    /// <summary>Open present devices and begin reading key reports. Idempotent.</summary>
    void Start();

    /// <summary>Stop reading and close all devices. Idempotent.</summary>
    void Stop();

    /// <summary>Raised on a reader thread for each key edge.</summary>
    event Action<StreamDeckInput>? InputReceived;

    /// <summary>Raised on hot-plug add/remove so the owner can re-open.</summary>
    event Action? DevicesChanged;
}
