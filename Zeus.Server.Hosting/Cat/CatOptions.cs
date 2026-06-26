// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

namespace Zeus.Server.Cat;

/// <summary>
/// Configuration for the CAT (Computer Aided Transceiver) server. CAT is a
/// Kenwood TS-2000 ASCII command protocol carried over a raw TCP socket,
/// spoken by loggers (N1MM+, Log4OM), digital-mode apps (WSJT-X, JTDX,
/// fldigi), and the Hamlib <c>rigctl</c>/<c>net rigctl</c> bridge. It mirrors
/// the TCI server (<see cref="Tci.TciOptions"/>); the only structural
/// difference is transport (raw TCP vs. WebSocket-over-Kestrel).
/// </summary>
public sealed class CatOptions
{
    /// <summary>
    /// Enable the CAT server. Defaults to false for security — CAT has no
    /// authentication; localhost binding is the security boundary.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Bind address for the CAT TCP server. Defaults to 127.0.0.1
    /// (localhost-only). Set to "0.0.0.0" to allow LAN clients, but only on
    /// trusted networks — CAT grants full TX control with no authentication.
    /// </summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    /// <summary>
    /// TCP port for the CAT server. Defaults to 19090 (the piHPSDR rigctl
    /// default — recognizable to SDR operators; avoids 6060/5173/40001). There
    /// is no universal TCP-CAT port standard, so this MUST be set to whatever
    /// the client (WSJT-X/N1MM/fldigi/Hamlib) is pointed at.
    /// </summary>
    public int Port { get; set; } = 19090;

    /// <summary>
    /// Rate-limit interval in milliseconds for coalescing high-frequency
    /// Auto-Information (AI) pushes (VFO changes during tuning). Defaults to
    /// 50 ms (20 Hz). Without this, an AI2 client spinning the VFO floods the
    /// link. Mirrors <see cref="Tci.TciOptions.RateLimitMs"/>.
    /// </summary>
    public int RateLimitMs { get; set; } = 50;

    /// <summary>
    /// Send the current radio state to a client immediately after it enables
    /// Auto-Information (AI1/AI2). Defaults to true. Poll-only (AI0) clients
    /// never receive unsolicited frames regardless.
    /// </summary>
    public bool SendInitialStateOnConnect { get; set; } = true;

    /// <summary>
    /// Limit TX drive (PC command) to a safe level for unattended automated
    /// operation. When true, drive is clamped to 50%. Defaults to false.
    /// Mirrors <see cref="Tci.TciOptions.LimitPowerLevels"/>.
    /// </summary>
    public bool LimitPowerLevels { get; set; } = false;
}
