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

using Zeus.Contracts;

namespace Zeus.Protocol2;

/// <summary>
/// Operator-driven CW keyer configuration carried into the Protocol-2
/// TxSpecific packet (port 1026, bytes 5-13/17). This is the seam that
/// activates the radio's <b>internal (FPGA) iambic keyer</b> so a physical
/// straight key / paddle plugged into the rear KEY jack actually keys the
/// transmitter — the missing piece behind issue #1032 on the G2 (and every
/// other Protocol-2 board).
///
/// <para>
/// On Protocol 2 the host does NOT key CW: once <see cref="Active"/> is set
/// (in CW mode) the radio's gateware shapes the dits/dahs, keys the PA, and
/// — with break-in — handles TX/RX switching itself. The host only has to
/// publish this config in the TxSpecific packet. This mirrors pihpsdr
/// <c>new_protocol.c</c> <c>new_protocol_tx_specific</c> (transmit_specific_buffer
/// bytes 5-17) and the OpenHPSDR Ethernet Protocol v4.4 "Transmitter
/// Specific" packet (§ pp.29-30).
/// </para>
///
/// <para>
/// Fields here are the values an operator changes (CW mode active, keyer
/// mode, speed, sidetone). The remaining gateware parameters
/// (weight / spacing / reversed / break-in / hang / RF-delay / ramp) are
/// pinned to Thetis/pihpsdr-faithful defaults inside
/// <see cref="Protocol2Client.ComposeCmdTxBuffer"/>, exactly as the
/// Protocol-1 path pins them in <c>ControlFrame</c>.
/// </para>
/// </summary>
public readonly record struct CwKeyerWireConfig
{
    /// <summary>
    /// True when the operating (TX/VFO-A) mode is CWU/CWL. Gates TxSpecific
    /// byte-5 bit-1 ("CW selected"): when false the byte stays 0 and the
    /// CmdTx tail is byte-identical to a non-CW transmit, so SSB/AM/FM/DIG
    /// wire form is unchanged.
    /// </summary>
    public bool Active { get; init; }

    /// <summary>Straight / Iambic-A / Iambic-B. Straight passes an external
    /// keyer or bug through untouched (gateware does no element timing).</summary>
    public CwKeyerMode Mode { get; init; }

    /// <summary>Keyer speed in WPM. TxSpecific byte 9. The gateware ignores it
    /// in straight-key mode but it also clamps the RF-delay (900/WPM), so a
    /// sane value is always sent.</summary>
    public int SpeedWpm { get; init; }

    /// <summary>Radio-generated sidetone frequency in Hz. TxSpecific bytes 7-8
    /// (big-endian). Shares the operator's CW pitch.</summary>
    public int SidetoneHz { get; init; }

    /// <summary>Radio-generated sidetone level, 0-127 (0 disables the sidetone
    /// bit). TxSpecific byte 6. Derived from the operator's sidetone gain.</summary>
    public byte SidetoneLevel { get; init; }

    /// <summary>A neutral, inactive config (no CW) — the default for every
    /// non-CW transmit and for callers/tests that don't drive CW.</summary>
    public static CwKeyerWireConfig Inactive => default;
}
