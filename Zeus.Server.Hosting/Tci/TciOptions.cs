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
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

namespace Zeus.Server.Tci;

/// <summary>
/// Configuration for the TCI (Transceiver Control Interface) server.
/// TCI is an ExpertSDR3-compatible WebSocket protocol for remote control
/// and streaming, spoken by loggers (Log4OM, N1MM+), digital-mode apps
/// (JTDX, WSJT-X), and SDR display tools.
/// </summary>
public sealed class TciOptions
{
    /// <summary>
    /// Enable the TCI server. Defaults to false for security — TCI has no
    /// authentication; localhost binding is the security boundary.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Bind address for the TCI WebSocket server. Defaults to 127.0.0.1
    /// (localhost-only). Set to "0.0.0.0" to allow LAN clients, but only
    /// on trusted networks — TCI has no authentication.
    /// </summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    /// <summary>
    /// TCP port for the TCI WebSocket server. Defaults to 40001, the
    /// ExpertSDR3 standard port. (Thetis uses 50001/31001; we adopt the
    /// ecosystem default for maximum client compatibility.)
    /// </summary>
    public int Port { get; set; } = 40001;

    /// <summary>
    /// Rate-limit interval in milliseconds for coalescing high-frequency
    /// events (VFO/DDS changes during tuning). Defaults to 50 ms (20 Hz
    /// broadcast cadence). Thetis uses a 10-item queue; we time-gate instead.
    /// </summary>
    public int RateLimitMs { get; set; } = 50;

    /// <summary>
    /// Send initial radio state (VFO, mode, filter, etc.) immediately after
    /// the handshake completes. Defaults to true. Some clients expect this;
    /// others poll explicitly.
    /// </summary>
    public bool SendInitialStateOnConnect { get; set; } = true;

    /// <summary>
    /// CW mode mapping quirk. When false, CWL/CWU are sent as-is on the wire.
    /// When true, CWL becomes "CW" below 10 MHz, CWU becomes "CW" above 10 MHz
    /// (a legacy client compatibility shim). Defaults to false.
    /// </summary>
    public bool CwBecomesCwuAbove10MHz { get; set; } = false;

    /// <summary>
    /// Limit TX drive/tune_drive to safe levels for automated operation.
    /// When true, drive is clamped to 50% and tune_drive to 25%. Defaults to
    /// false. Enable if remote operators are running unattended macros.
    /// </summary>
    public bool LimitPowerLevels { get; set; } = false;
}
