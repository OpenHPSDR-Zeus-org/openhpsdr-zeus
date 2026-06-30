// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Collections.Generic;

namespace Zeus.Contracts;

/// <summary>The physical control kind a mapping expects — drives how a raw
/// MIDI event is interpreted (button = note-on/off, knob/slider = absolute
/// 0..127 value, wheel = relative ±delta encoder). Append-only.</summary>
public enum MidiControlType : byte
{
    Button = 0,
    KnobOrSlider = 1,
    Wheel = 2,
}

/// <summary>A connected MIDI input device, as enumerated by the engine.</summary>
public sealed record MidiDeviceDto(
    string Name,
    bool Connected);

/// <summary>One operator-defined binding: a physical control on a named device
/// mapped to a Zeus command. <paramref name="ControlId"/> is the engine's
/// stable identifier for the control (e.g. "note:0:60" or "cc:0:7"). Min/Max
/// shape the value scaling for knobs/sliders; <paramref name="Toggle"/> marks a
/// button as a latching toggle vs momentary.</summary>
public sealed record MidiMappingDto(
    string DeviceName,
    string ControlId,
    MidiControlType ControlType,
    ZeusMidiCommand Command,
    int Min = 0,
    int Max = 127,
    bool Toggle = false);

/// <summary>A connected Elgato Stream Deck (HID), as enumerated by the engine.
/// <paramref name="ButtonCount"/> lets the UI render the right key grid.</summary>
public sealed record StreamDeckDeviceDto(
    string Name,
    string Serial,
    int ButtonCount,
    bool Connected);

/// <summary>One Stream Deck button → command binding, keyed by device serial
/// and zero-based button index.</summary>
public sealed record StreamDeckMappingDto(
    string Serial,
    int ButtonIndex,
    ZeusMidiCommand Command);

/// <summary>The full persisted controller binding document — MIDI control maps
/// plus Stream Deck key maps — versioned for forward migration. Stored as a
/// single row in <c>MidiConfigStore</c>.</summary>
public sealed record MidiBindingsDoc(
    int Version,
    IReadOnlyList<MidiMappingDto> Mappings,
    IReadOnlyList<StreamDeckMappingDto> StreamDeckMappings)
{
    public const int CurrentVersion = 1;

    public static MidiBindingsDoc Empty { get; } = new(
        CurrentVersion,
        new List<MidiMappingDto>(),
        new List<StreamDeckMappingDto>());
}

/// <summary>A live control event pushed to the UI during Learn mode (MsgType
/// 0x3B). The panel highlights the matching control so the operator can bind
/// it. <paramref name="ControlType"/> is the engine's best guess from the wire
/// shape; <paramref name="Value"/> is the absolute 0..127 reading (or button
/// velocity); <paramref name="Delta"/> the relative encoder step when known.
/// Stream Deck presses arrive with <paramref name="ControlType"/> = Button and
/// a "sd:&lt;serial&gt;:&lt;index&gt;" <paramref name="ControlId"/>.</summary>
public sealed record MidiLearnFrame(
    string DeviceName,
    string ControlId,
    MidiControlType ControlType,
    int Value,
    int Delta);

/// <summary>The MIDI subsystem's runtime config payload — the enable flag plus
/// the binding document — the body of <c>GET/PUT /api/midi/config</c>.</summary>
public sealed record MidiConfigDto(
    bool Enabled,
    MidiBindingsDoc Bindings);

/// <summary>MIDI subsystem status for the settings panel: whether each engine
/// is available on this platform, the live device lists, and the enable
/// flag.</summary>
public sealed record MidiStatusDto(
    bool Enabled,
    bool MidiEngineAvailable,
    bool StreamDeckEngineAvailable,
    IReadOnlyList<MidiDeviceDto> MidiDevices,
    IReadOnlyList<StreamDeckDeviceDto> StreamDeckDevices,
    bool Learning);
