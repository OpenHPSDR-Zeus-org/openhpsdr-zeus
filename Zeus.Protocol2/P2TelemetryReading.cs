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
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

namespace Zeus.Protocol2;

/// <summary>
/// One sample of the Protocol-2 hi-priority status packet (radio → host on
/// UDP 1025, 60-byte payload). Field offsets and meanings come from Thetis
/// <c>ChannelMaster/network.c:683-756</c> (case <c>portIdx == 0</c>):
///
/// <list type="bullet">
///   <item><c>byte 0</c> — bit 0 PTT, bit 1 Dot, bit 2 Dash, bit 4 PLL locked</item>
///   <item><c>byte 1</c> — ADC0..7 overload bits (bit i = ADC i)</item>
///   <item><c>bytes 2..3</c> — exciter power (12-bit, sign-extended to 16, BE)</item>
///   <item><c>bytes 10..11</c> — PA forward power ADC (BE u16)</item>
///   <item><c>bytes 18..19</c> — PA reverse power ADC (BE u16)</item>
///   <item><c>bytes 26..27</c> — hardware LEDs (BE u16)</item>
///   <item><c>bytes 35..38</c> — ADC0/ADC1 max magnitude (BE u16)</item>
///   <item><c>bytes 45..46</c> — supply volts (BE u16)</item>
///   <item><c>bytes 47..54</c> — user ADC3..0 (BE u16)</item>
///   <item><c>byte 55</c> — user digital input bitfield</item>
/// </list>
///
/// The forward/reverse ADC numbers feed the same watts math as the P1 alex
/// FWD/REF readings — see <see cref="Zeus.Contracts.RadioCalibration"/> for
/// the per-board bridge / refvoltage / cal-offset constants.
/// </summary>
public readonly record struct P2TelemetryReading(
    ushort FwdAdc,
    ushort RevAdc,
    ushort ExciterAdc,
    bool PttIn,
    bool PllLocked,
    bool DotIn = false,
    bool DashIn = false,
    bool SidetoneActive = false,
    byte AdcOverloadBits = 0,
    ushort Adc0MaxMagnitude = 0,
    ushort Adc1MaxMagnitude = 0,
    ushort SupplyVoltsAdc = 0,
    ushort UserAdc0 = 0,
    ushort UserAdc1 = 0,
    ushort UserAdc2 = 0,
    ushort UserAdc3 = 0,
    byte UserDigitalIn = 0,
    ushort HardwareLeds = 0);
