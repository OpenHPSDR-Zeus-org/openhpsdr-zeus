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

import { useEffect, useRef, useState } from 'react';
import { SMeter } from './SMeter';

// Presentational harness until real meter telemetry is wired up. Generates a
// plausible fluctuating RX level and lets the user toggle a TX state to
// preview the power-bar variant.
export function SMeterDemo() {
  const [isTx, setIsTx] = useState(false);
  const [dbm, setDbm] = useState(-100);
  const [watts, setWatts] = useState(0);
  const startRef = useRef<number>(performance.now());
  const rafRef = useRef<number | null>(null);

  useEffect(() => {
    const tick = () => {
      const t = (performance.now() - startRef.current) / 1000;
      if (isTx) {
        // Slight envelope around 25 W avg with voice-peak excursions.
        const env = 25 + 15 * Math.abs(Math.sin(t * 3.1)) + 8 * Math.sin(t * 11);
        setWatts(Math.max(0, env));
      } else {
        // Slow roaming S-level with bursts.
        const slow = -95 + 25 * Math.sin(t * 0.35);
        const fast = 6 * Math.sin(t * 2.4) + 3 * Math.sin(t * 7.2);
        setDbm(slow + fast);
      }
      rafRef.current = requestAnimationFrame(tick);
    };
    rafRef.current = requestAnimationFrame(tick);
    return () => {
      if (rafRef.current != null) cancelAnimationFrame(rafRef.current);
    };
  }, [isTx]);

  return (
    <section className="flex items-center gap-2 border-b border-neutral-800 bg-neutral-950 px-3 py-1 sm:px-4">
      <button
        type="button"
        onClick={() => setIsTx((v) => !v)}
        className={
          'rounded px-2 py-1 font-mono text-xs tracking-wide ' +
          (isTx
            ? 'bg-red-600/80 text-neutral-50 ring-1 ring-red-400/60'
            : 'bg-neutral-800 text-neutral-300 hover:bg-neutral-700')
        }
        aria-pressed={isTx}
        title="Toggle TX (demo)"
      >
        {isTx ? 'TX' : 'RX'}
      </button>
      <div className="flex-1">
        {isTx ? (
          <SMeter mode="tx" watts={watts} maxWatts={100} />
        ) : (
          <SMeter mode="rx" dbm={dbm} />
        )}
      </div>
    </section>
  );
}
