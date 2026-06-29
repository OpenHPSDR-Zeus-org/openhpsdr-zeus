// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Collections.Concurrent;
using Zeus.Contracts;

namespace Zeus.Server.Midi;

/// <summary>
/// Engine-local state the <see cref="MidiCommandDispatcher"/> consults while
/// routing a control event: a live <see cref="StateDto"/> snapshot accessor,
/// per-command latching-toggle bits (for buttons mapped as on/off latches),
/// the 0..127 → range scaler for knobs/sliders, and the operator-tunable VFO
/// wheel sensitivity. No radio I/O lives here — only the dispatch's own memory,
/// so it is trivially testable with a synthetic snapshot delegate.
/// </summary>
public sealed class MidiDispatchState
{
    private readonly Func<StateDto> _snapshot;
    private readonly ConcurrentDictionary<ZeusMidiCommand, bool> _toggles = new();
    private int _messagesPerTuneStep = 1;

    public MidiDispatchState(Func<StateDto> snapshot) => _snapshot = snapshot;

    /// <summary>The authoritative live radio state.</summary>
    public StateDto Snapshot() => _snapshot();

    /// <summary>Flip and return the persisted on/off latch for a toggle command.
    /// First call returns true (turns it on).</summary>
    public bool Toggle(ZeusMidiCommand cmd)
    {
        bool next = !_toggles.GetValueOrDefault(cmd, false);
        _toggles[cmd] = next;
        return next;
    }

    /// <summary>Read the current latch without flipping it (test/inspection).</summary>
    public bool ToggleState(ZeusMidiCommand cmd) => _toggles.GetValueOrDefault(cmd, false);

    /// <summary>Map an absolute 0..127 control reading onto [min, max] linearly.
    /// Values are clamped to the 7-bit MIDI range first.</summary>
    public double Scale(int value, double min, double max)
    {
        int v = Math.Clamp(value, 0, 127);
        return min + (max - min) * (v / 127.0);
    }

    /// <summary>VFO wheel sensitivity: the number of MIDI messages required per
    /// VFO tune detent. Engine-local (no radio seam); floored at 1.</summary>
    public int MessagesPerTuneStep
    {
        get => _messagesPerTuneStep;
        set => _messagesPerTuneStep = Math.Max(1, value);
    }
}
