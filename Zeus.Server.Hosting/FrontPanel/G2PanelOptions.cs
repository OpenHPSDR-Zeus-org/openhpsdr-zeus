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

namespace Zeus.Server.FrontPanel;

/// <summary>
/// Configuration for the ANAN G2 / G2-Ultra hardware front panel bridge.
/// Bound from the <c>G2FrontPanel</c> section of configuration (appsettings
/// or environment, e.g. <c>G2FrontPanel__Baud=115200</c>).
///
/// <para>The panel is a serial device wired to the host running Zeus — on a
/// stock G2 that is the radio's internal Raspberry Pi, where the udev rule
/// <c>61-g2-serial.rules</c> publishes it as <c>/dev/serial/by-id/g2-front-*</c>.
/// When <see cref="DevicePath"/> is left empty the bridge auto-detects that
/// symlink, so on the G2 Pi it "just works" and on any other host it stays a
/// silent no-op (no device → idle, periodic re-probe).</para>
/// </summary>
public sealed class G2PanelOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "G2FrontPanel";

    /// <summary>Master switch. When false the bridge never opens a port.
    /// Default true: it is gated by device presence, not by this flag, so a
    /// machine with no panel costs nothing.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Explicit serial device (e.g. <c>/dev/ttyACM0</c> or, on
    /// Windows with a USB panel, <c>COM5</c>). Empty = auto-detect the
    /// <c>g2-front-*</c> by-id symlink on Linux.</summary>
    public string? DevicePath { get; set; }

    /// <summary>Baud rate. The 8" Mk2 control front and the CM4/CM5 UART
    /// variants run 9600; the RP2040-Zero "Front V1" runs 115200. Auto-detect
    /// reads the rate from the symlink name when this is left at 0.</summary>
    public int Baud { get; set; }

    /// <summary>Well-known by-id symlinks the udev rule creates, newest first.
    /// The suffix encodes the baud so auto-detect needs no extra probing.</summary>
    public static readonly (string Path, int Baud)[] KnownSymlinks =
    {
        ("/dev/serial/by-id/g2-front-9600", 9600),
        ("/dev/serial/by-id/g2-front-115200", 115200),
    };
}
