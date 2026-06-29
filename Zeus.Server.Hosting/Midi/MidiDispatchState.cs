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

namespace Zeus.Server.Midi;

/// <summary>
/// Engine-local state the <see cref="MidiCommandDispatcher"/> consults while
/// routing a control event: a live <see cref="StateDto"/> snapshot accessor and
/// the 0..127 → range scaler for knobs/sliders. Every on/off command derives
/// its next value from the live snapshot (never a shadow latch), so a control
/// changed from the web UI / CAT / PTT can't desync the next MIDI press. No
/// radio I/O lives here — only the dispatch's own memory, so it is trivially
/// testable with a synthetic snapshot delegate.
/// </summary>
public sealed class MidiDispatchState
{
    private readonly Func<StateDto> _snapshot;

    public MidiDispatchState(Func<StateDto> snapshot) => _snapshot = snapshot;

    /// <summary>The authoritative live radio state.</summary>
    public StateDto Snapshot() => _snapshot();

    /// <summary>Map an absolute 0..127 control reading onto [min, max] linearly.
    /// Values are clamped to the 7-bit MIDI range first.</summary>
    public double Scale(int value, double min, double max)
    {
        int v = Math.Clamp(value, 0, 127);
        return min + (max - min) * (v / 127.0);
    }
}
