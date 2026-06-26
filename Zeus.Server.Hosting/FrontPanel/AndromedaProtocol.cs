// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// The ANDROMEDA CAT framing decoded here (ZZZS/ZZZP/ZZZE/ZZZU/ZZZD and the
// ZZZI LED reports) and the G2-Ultra (type-5) button/encoder map this module
// feeds were derived by studying DeskHPSDR (https://github.com/dl1bz/deskhpsdr,
// maintained by Heiko DL1BZ) and pihpsdr (https://github.com/dl1ycf/pihpsdr,
// maintained by Christoph Wüllen DL1YCF), both GPL-2.0-or-later. See
// ATTRIBUTIONS.md at the repository root for the full provenance statement.

namespace Zeus.Server.FrontPanel;

/// <summary>
/// A single decoded message from an ANDROMEDA-protocol front panel
/// (the ANAN G2 / G2-Ultra control head). The panel speaks a CAT dialect
/// over a serial line; every message is a <c>;</c>-terminated ASCII token.
/// </summary>
public abstract record PanelEvent
{
    /// <summary>Push-button transition. <see cref="V"/> is 0 (released),
    /// 1 (pressed) or 2 (held long). Short vs long press is disambiguated
    /// by the v=0→1→2→0 transition sequence, tracked by the router.</summary>
    public sealed record Button(int Id, int V) : PanelEvent;

    /// <summary>Encoder detents. <see cref="Ticks"/> is signed: positive =
    /// clockwise, negative = counter-clockwise, magnitude = number of ticks
    /// since the last report (1..9).</summary>
    public sealed record Encoder(int Id, int Ticks) : PanelEvent;

    /// <summary>Main VFO dial. <see cref="Steps"/> is signed accelerated
    /// encoder ticks. The action router divides these into logical VFO steps
    /// before applying the operator's selected Hz step.</summary>
    public sealed record Vfo(int Steps) : PanelEvent;

    /// <summary>The panel announced its identity (response to <c>ZZZS;</c>).
    /// <see cref="Type"/> 5 = G2 Ultra (Mk2), 4 = upgraded G2 Mk1, 1 = the
    /// original ANDROMEDA console.</summary>
    public sealed record Version(int Type, string Raw) : PanelEvent;
}

/// <summary>
/// Streaming decoder for the ANDROMEDA serial dialect. Bytes arrive in
/// arbitrary chunks; <see cref="Feed"/> accumulates them and yields one
/// <see cref="PanelEvent"/> per complete <c>;</c>-terminated command it can
/// interpret. Unknown / unhandled commands are silently dropped (the panel
/// also sends ordinary CAT queries we don't need here).
/// </summary>
public sealed class AndromedaParser
{
    // Bounded accumulator: a runaway peer can't grow this without bound.
    private readonly System.Text.StringBuilder _buf = new(64);
    private const int MaxCommandLen = 64;

    /// <summary>
    /// ANDROMEDA VFO acceleration curve (deskhpsdr <c>andromeda_vfo_speedup</c>).
    /// Index = raw step count reported by the panel (0..30); value = the
    /// accelerated step count so faster spinning tunes over-proportionally.
    /// Index 31 holds the multiplier applied for raw counts &gt; 30.
    /// </summary>
    private static readonly int[] VfoSpeedup =
    {
          0,   1,   2,   3,   4,   5,   6,   7,
          8,   9,  11,  12,  14,  17,  19,  22,
         25,  29,  33,  38,  43,  48,  54,  61,
         69,  77,  85,  95, 105, 116, 128,   4,
    };

    /// <summary>Feed a chunk of received characters; invokes <paramref name="emit"/>
    /// once per fully-decoded command.</summary>
    public void Feed(ReadOnlySpan<char> chunk, Action<PanelEvent> emit)
    {
        foreach (var c in chunk)
        {
            if (c == ';')
            {
                var cmd = _buf.ToString();
                _buf.Clear();
                var ev = Parse(cmd);
                if (ev is not null) emit(ev);
            }
            else if (c is not ('\r' or '\n'))
            {
                if (_buf.Length >= MaxCommandLen) _buf.Clear(); // resync on junk
                _buf.Append(c);
            }
        }
    }

    // Parse one command body (without the trailing ';').
    private static PanelEvent? Parse(string s)
    {
        // We only care about the ANDROMEDA "ZZZ" extension namespace plus the
        // VFO up/down shortcuts.
        if (s.Length < 4 || s[0] != 'Z' || s[1] != 'Z' || s[2] != 'Z')
            return null;

        char kind = s[3];
        switch (kind)
        {
            case 'P': // ZZZPxxy  — push-button: xx=id, y=0/1/2
                if (s.Length == 7 && TryTwo(s, 4, out int pb) && TryDigit(s[6], out int v))
                    return new PanelEvent.Button(pb, v);
                return null;

            case 'E': // ZZZExxy  — encoder: xx=id(+50 = CCW), y=ticks
                if (s.Length == 7 && TryTwo(s, 4, out int enc) && TryDigit(s[6], out int ticks))
                {
                    if (enc > 50) { enc -= 50; ticks = -ticks; }
                    return ticks == 0 ? null : new PanelEvent.Encoder(enc, ticks);
                }
                return null;

            case 'U': // ZZZUxx — VFO up (active receiver)
            case 'D': // ZZZDxx — VFO down
                if (s.Length == 6 && TryTwo(s, 4, out int raw))
                {
                    int steps = raw <= 30 ? VfoSpeedup[raw] : raw * VfoSpeedup[31];
                    return new PanelEvent.Vfo(kind == 'U' ? steps : -steps);
                }
                return null;

            case 'S': // ZZZSxxyyzzz — version announcement, xx = ANDROMEDA type
                if (s.Length >= 6 && TryTwo(s, 4, out int type))
                    return new PanelEvent.Version(type, s);
                return null;

            default:
                return null;
        }
    }

    private static bool TryDigit(char c, out int d)
    {
        if (c is >= '0' and <= '9') { d = c - '0'; return true; }
        d = 0; return false;
    }

    private static bool TryTwo(string s, int at, out int n)
    {
        if (TryDigit(s[at], out int hi) && TryDigit(s[at + 1], out int lo))
        {
            n = hi * 10 + lo; return true;
        }
        n = 0; return false;
    }

    /// <summary>Build a <c>ZZZIxxy;</c> LED report to send back to the panel
    /// (xx = LED number, y = 0/1 off/on).</summary>
    public static string LedCommand(int led, bool on) =>
        $"ZZZI{led:D2}{(on ? 1 : 0)};";
}
