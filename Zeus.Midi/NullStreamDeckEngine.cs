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

/// <summary>No-device Stream Deck engine for headless/CI hosts and unit tests.
/// Opens no HID device (no real I/O — a real HID open in a test can crash the
/// Windows CI host) and lets tests inject synthetic key edges.</summary>
public sealed class NullStreamDeckEngine : IStreamDeckEngine
{
    public bool IsAvailable => false;

    public IReadOnlyList<StreamDeckDeviceDto> EnumerateDevices() => Array.Empty<StreamDeckDeviceDto>();

    public void Start() { }
    public void Stop() { }

    public event Action<StreamDeckInput>? InputReceived;
    public event Action? DevicesChanged;

    /// <summary>Test hook: publish a synthetic key edge with no I/O.</summary>
    public void Inject(StreamDeckInput input) => InputReceived?.Invoke(input);

    /// <summary>Test hook: simulate a hot-plug notification.</summary>
    public void RaiseDevicesChanged() => DevicesChanged?.Invoke();

    public void Dispose() { }
}
