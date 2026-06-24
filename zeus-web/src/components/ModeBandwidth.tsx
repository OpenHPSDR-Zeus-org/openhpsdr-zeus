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

import { useCallback } from 'react';
import { type RxMode } from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import {
  getReceiverMode,
  optimisticSetReceiverMode,
  postReceiverMode,
} from '../state/receiver-state';
import { saveReceiverBandModeMemory } from '../util/band-memory';
import { toolbarFavDragMime } from './toolbar/toolbarFavoriteDrag';

type ModeEntry = { value: RxMode; label: string };

const MODES: readonly ModeEntry[] = [
  { value: 'LSB', label: 'LSB' },
  { value: 'USB', label: 'USB' },
  { value: 'CWL', label: 'CWL' },
  { value: 'CWU', label: 'CWU' },
  { value: 'AM', label: 'AM' },
  { value: 'SAM', label: 'SAM' },
  { value: 'DSB', label: 'DSB' },
  { value: 'FM', label: 'FM' },
  { value: 'DIGL', label: 'DIGL' },
  { value: 'DIGU', label: 'DIGU' },
  { value: 'FREEDV', label: 'FreeDV' },
];

export function ModeBandwidth() {
  // Follow the focused receiver across all DDCs (0=RX1, 1=RX2, >=2=RX3+), the
  // same model VFO B used via rxFocus — generalised to any numeric receiver so
  // the mode row drives whichever receiver the operator is working in.
  const focusedRxIndex = useConnectionStore((s) => s.focusedRxIndex);
  const applyState = useConnectionStore((s) => s.applyState);
  const activeMode = useConnectionStore((s) => getReceiverMode(s, focusedRxIndex));

  const selectMode = useCallback(
    (m: RxMode) => {
      if (m === activeMode) return;
      optimisticSetReceiverMode(focusedRxIndex, m);
      // Per-band last-mode memory is an RX1/RX2 (A/B) concept; skip for RX3+.
      if (focusedRxIndex <= 1) {
        saveReceiverBandModeMemory(focusedRxIndex === 1 ? 'B' : 'A', m);
      }
      postReceiverMode(focusedRxIndex, m)
        .then((state) => {
          applyState(state);
          if (focusedRxIndex <= 1) {
            saveReceiverBandModeMemory(focusedRxIndex === 1 ? 'B' : 'A', undefined, state);
          }
        })
        .catch(() => {
          /* next state poll will reconcile */
        });
    },
    [focusedRxIndex, activeMode, applyState],
  );

  return (
    <>
      {/* Desktop: horizontal row of mode buttons. width:100% so the row
          fills its container — single line at typical tile widths, wraps
          to the next line as the tile narrows (flex-wrap from .btn-row.wrap). */}
      <div className="ctrl-group hide-mobile" style={{ width: '100%' }}>
        <div className="btn-row wrap" style={{ width: '100%' }}>
          {MODES.map((m) => (
            <button
              key={m.value}
              type="button"
              draggable
              onDragStart={(e) => {
                e.dataTransfer.setData(toolbarFavDragMime('mode'), m.value);
                e.dataTransfer.effectAllowed = 'move';
              }}
              onClick={() => selectMode(m.value)}
              className={`btn sm ${activeMode === m.value ? 'active' : ''}`}
              title={`${m.label} — drag onto a toolbar favorite slot to pin`}
            >
              {m.label}
            </button>
          ))}
        </div>
      </div>

      {/* Mobile: dropdown for mode selection */}
      <div className="ctrl-group show-mobile" style={{ display: 'none' }}>
        <select
          value={activeMode}
          onChange={(e) => selectMode(e.target.value as RxMode)}
          className="mode-select"
          style={{
            background: 'var(--btn-top)',
            color: 'var(--fg-0)',
            border: '1px solid var(--line)',
            borderRadius: 'var(--r-sm)',
            padding: '4px 8px',
            fontSize: '11px',
            fontWeight: 600,
            cursor: 'pointer',
          }}
        >
          {MODES.map((m) => (
            <option key={m.value} value={m.value}>
              {m.label}
            </option>
          ))}
        </select>
      </div>
    </>
  );
}
