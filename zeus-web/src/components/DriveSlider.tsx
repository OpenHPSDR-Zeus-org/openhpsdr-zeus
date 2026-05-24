// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
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

import { useCallback, useRef } from 'react';
import { setDrive } from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { useTxStore } from '../state/tx-store';
import { usePaStore } from '../state/pa-store';
import { useLiveSlider } from '../hooks/useLiveSlider';

// PRD FR-4 drive range: 0..100 percent. Stream during drag via rAF coalesce
// (one POST per paint at most) so the radio's drive byte tracks the thumb in
// real time during MOX — previously a 100 ms trailing-edge debounce caused
// audible lag on a fast sweep.
const MIN = 0;
const MAX = 100;

export function DriveSlider() {
  const connected = useConnectionStore((s) => s.status === 'Connected');
  const drivePercent = useTxStore((s) => s.drivePercent);
  const setDrivePercent = useTxStore((s) => s.setDrivePercent);
  // Live "target watts" preview so the operator can read "35% of 100 W = 35 W"
  // at a glance — the slider itself is target-watts-%, not drive-byte-%, and
  // this field makes the muscle-memory bridge from Thetis explicit. Hidden
  // when Rated PA Output is 0 (raw drive-byte mode — watts have no meaning).
  const paMaxWatts = usePaStore((s) => s.settings.global.paMaxPowerWatts);
  const targetWatts = paMaxWatts > 0 ? Math.round((paMaxWatts * drivePercent) / 100) : null;

  // Tracks the most recently acknowledged value so we can roll back on error.
  const previousOnError = useRef<number>(drivePercent);

  const liveSlider = useLiveSlider<number>({
    send: useCallback(
      (v: number, signal: AbortSignal) => {
        const prevValue = previousOnError.current;
        return setDrive(v, signal)
          .then((r) => {
            if (signal.aborted) return;
            previousOnError.current = r.drivePercent;
            if (r.drivePercent !== v) setDrivePercent(r.drivePercent);
          })
          .catch((err) => {
            if (signal.aborted) return;
            if (err instanceof DOMException && err.name === 'AbortError') return;
            // Roll back the optimistic update so the user sees the real state.
            setDrivePercent(prevValue);
          });
      },
      [setDrivePercent],
    ),
  });

  // Server is now authoritative for drivePercent (StateDto.DrivePct, persisted
  // via RadioStateStore, hydrated into tx-store by tx-store.hydrateFromState
  // on every fresh RadioStateDto). The previous push-on-connect that ran here
  // would clobber the server's hydrated value with the localStorage mirror
  // every time the operator (re)connected. Removed deliberately.

  const onChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const v = Number(e.currentTarget.value);
    setDrivePercent(v);
    liveSlider.push(v);
  };

  return (
    <label className="knob-group">
      <span className="label-xs">DRV</span>
      <input
        type="range"
        min={MIN}
        max={MAX}
        step={1}
        value={drivePercent}
        disabled={!connected}
        onChange={onChange}
        onMouseUp={() => liveSlider.flush()}
        onTouchEnd={() => liveSlider.flush()}
        onKeyUp={() => liveSlider.flush()}
        style={{ flex: 1, cursor: 'pointer', accentColor: 'var(--accent)' }}
      />
      <span className="mono" style={{ width: 40, textAlign: 'right', color: 'var(--power)', fontSize: 11, fontWeight: 700 }}>
        {drivePercent}%
      </span>
      {targetWatts !== null && (
        <span
          className="mono"
          style={{ width: 44, textAlign: 'right', color: 'var(--neutral-400, #888)', fontSize: 10 }}
          title="Target output watts = Rated PA Output × Drive %"
        >
          ~{targetWatts} W
        </span>
      )}
    </label>
  );
}
