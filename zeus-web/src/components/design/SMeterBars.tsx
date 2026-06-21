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

// Vertical S-unit bar array matching the reference image: 12 bars with S-labels below.
// Driven by a simulated receive level in dBm so the visual matches the design even
// when the real backend isn't pushing an S-meter value. Real dBm readings can be
// dropped in as the `dbm` prop when SMeterLive is rewired into this panel later.

const LABELS = ['1', '3', '5', '7', '9', '+10', '+20', '+30', '+40', '+50', '+60'];

export function SMeterBars({ dbm }: { dbm: number }) {
  // Map -127 dBm → S0 .. -73 dBm → S9 → +60 over (-13 dBm).
  // 6 dB per S-unit below S9, 10 dB per step above S9.
  const sUnits = (() => {
    if (dbm <= -127) return 0;
    if (dbm <= -73) return (dbm + 127) / 6; // 0..9
    return 9 + (dbm + 73) / 10; // 9..~15
  })();

  return (
    <div className="smeter">
      <div className="smeter-scale">
        {Array.from({ length: 12 }, (_, i) => {
          const lit = sUnits >= i + 0.5;
          const over = i >= 9;
          return (
            <div key={i} className={`smeter-s ${lit ? 'lit' : ''} ${over ? 'over' : ''}`}>
              <div className="smeter-bar" />
              <div className="smeter-lbl">{LABELS[i] ?? ''}</div>
            </div>
          );
        })}
      </div>
      <div className="smeter-foot">
        <span className="label-xs">S-METER</span>
        <span className="smeter-val mono">
          {dbm.toFixed(0)}
          <span className="unit"> dBm</span>
        </span>
      </div>
    </div>
  );
}
