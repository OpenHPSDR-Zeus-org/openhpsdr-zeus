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

import { useTxStore } from '../state/tx-store';

// HL2 PA temperature chip for the transport bar.
//
// Source: MsgType 0x17 (PaTempFrame) at 2 Hz, already clamped server-side to
// [-40, 125] °C. The HL2 Q6 sensor drives the gateware auto-shutdown at 55 °C,
// so the operator needs a quick at-a-glance read with headroom warning:
//   < 50 °C         — normal      (default chip color)
//   50 ≤ t < 55 °C  — warning     (amber — var(--orange))
//   ≥ 55 °C         — danger      (red   — var(--tx))
//
// Format matches the task spec: one decimal below 50 °C (precision matters
// at low temps), integer at/above 50 °C (visual clarity in the warning band).
// Null reading (no sample yet / disconnected) renders em-dash.

const WARN_C = 50;
const DANGER_C = 55;

function formatTempC(c: number): string {
  return c >= WARN_C ? `${Math.round(c)}` : c.toFixed(1);
}

export function PaTempChip() {
  const paTempC = useTxStore((s) => s.paTempC);
  const hasReading = paTempC !== null && Number.isFinite(paTempC);
  const danger = hasReading && paTempC >= DANGER_C;
  const warn = hasReading && !danger && paTempC >= WARN_C;
  const valueColor = danger
    ? 'var(--tx)'
    : warn
      ? 'var(--orange)'
      : 'var(--fg-0)';
  const display = hasReading ? `${formatTempC(paTempC)} °C` : '— °C';
  return (
    <div
      className="chip hide-mobile"
      title="HL2 PA temperature (Q6 sensor). 55 °C auto-shutdown threshold."
      aria-label={hasReading ? `PA temperature ${display}` : 'PA temperature no reading'}
      style={
        danger
          ? {
              // Subtle red glow on shutdown-imminent; no new hue introduced,
              // just the existing --tx-soft token used elsewhere for TX chrome.
              boxShadow: '0 0 6px var(--tx-soft)',
            }
          : undefined
      }
    >
      <span className="k">PA TEMP</span>
      <span className="v mono" style={{ color: valueColor }}>
        {display}
      </span>
    </div>
  );
}
