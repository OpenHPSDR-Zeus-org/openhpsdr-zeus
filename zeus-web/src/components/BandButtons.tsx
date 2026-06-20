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

import { useCallback, useEffect, useRef, useState } from 'react';
import {
  fetchBandMemory,
  saveBandMemory,
  setMode,
  setVfo,
  type BandMemoryEntry,
  type RadioStateDto,
  type RxMode,
} from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { BANDS, bandOf } from './design/data';
import { toolbarFavDragMime } from './toolbar/toolbarFavoriteDrag';

type BandEntry = {
  name: string;
  centerHz: number;
  rangeStart: number;
  rangeEnd: number;
};

// HF bands only (160m-10m) for Hermes Lite 2 coverage
const HF_BANDS: readonly BandEntry[] = BANDS.slice(0, 10).map((b) => ({
  name: b.n + 'm',
  centerHz: b.center,
  rangeStart: b.range[0],
  rangeEnd: b.range[1],
}));

// Debounce the "save current (hz, mode) for the current band" write so tuning
// the VFO doesn't hammer the server on every pixel of knob travel.
const SAVE_DEBOUNCE_MS = 500;

function withRestoredMode(state: RadioStateDto, mode: RxMode): RadioStateDto {
  return { ...state, mode };
}

export function BandButtons() {
  const vfoHz = useConnectionStore((s) => s.vfoHz);
  const mode = useConnectionStore((s) => s.mode);
  const applyState = useConnectionStore((s) => s.applyState);

  const [currentBand, setCurrentBand] = useState<string>(() => bandOf(vfoHz));

  // In-memory mirror of the server's band memory. Populated from the
  // /api/bands/memory GET on mount and kept in sync with our own PUTs so a
  // band click can apply the saved (hz, mode) without an extra round-trip.
  const memoryRef = useRef<Map<string, BandMemoryEntry>>(new Map());
  const saveTimerRef = useRef<number | null>(null);
  const pendingSaveRef = useRef<BandMemoryEntry | null>(null);
  const lastBandRef = useRef<string>(currentBand);

  // Initial load of server-persisted band memory
  useEffect(() => {
    const ac = new AbortController();
    fetchBandMemory(ac.signal)
      .then((entries) => {
        const m = new Map<string, BandMemoryEntry>();
        for (const e of entries) m.set(e.band, e);
        memoryRef.current = m;
      })
      .catch(() => {
        /* offline / older server — band click will just use center defaults */
      });
    return () => ac.abort();
  }, []);

  const clearSaveTimer = useCallback(() => {
    if (saveTimerRef.current !== null) {
      window.clearTimeout(saveTimerRef.current);
      saveTimerRef.current = null;
    }
  }, []);

  const flushPendingSave = useCallback(() => {
    const pending = pendingSaveRef.current;
    if (!pending) return;

    pendingSaveRef.current = null;
    clearSaveTimer();
    memoryRef.current.set(pending.band, pending);
    saveBandMemory(pending.band, pending.hz, pending.mode).catch(() => {
      /* best-effort — next tune will retry */
    });
  }, [clearSaveTimer]);

  useEffect(() => {
    return () => {
      clearSaveTimer();
      pendingSaveRef.current = null;
    };
  }, [clearSaveTimer]);

  // Track current band + debounced save of (hz, mode) for that band.
  // Crossing bands flushes the previous band's pending row so a quick
  // mode-change-then-band-click does not drop the old band's mode memory.
  useEffect(() => {
    const band = bandOf(vfoHz);
    setCurrentBand(band);
    if (lastBandRef.current !== band) {
      flushPendingSave();
      lastBandRef.current = band;
    }
    if (band === '—') return;

    pendingSaveRef.current = { band, hz: vfoHz, mode };
    clearSaveTimer();
    saveTimerRef.current = window.setTimeout(flushPendingSave, SAVE_DEBOUNCE_MS);
  }, [clearSaveTimer, flushPendingSave, vfoHz, mode]);

  const selectBand = useCallback(
    (band: BandEntry) => {
      if (band.name !== currentBand) flushPendingSave();
      const stored = memoryRef.current.get(band.name);
      const targetHz = stored?.hz ?? band.centerHz;
      const targetMode: RxMode | null = stored?.mode ?? null;

      useConnectionStore.setState(
        targetMode && targetMode !== mode
          ? { vfoHz: targetHz, mode: targetMode }
          : { vfoHz: targetHz },
      );

      void (async () => {
        let modeRestored = !targetMode || targetMode === mode;
        if (targetMode && targetMode !== mode) {
          try {
            applyState(withRestoredMode(await setMode(targetMode), targetMode));
            modeRestored = true;
          } catch {
            /* next state poll will reconcile */
          }
        }
        try {
          const next = await setVfo(targetHz);
          applyState(
            targetMode && modeRestored
              ? withRestoredMode(next, targetMode)
              : next,
          );
        } catch {
          /* next state poll will reconcile */
        }
      })();
    },
    [applyState, currentBand, flushPendingSave, mode],
  );

  return (
    <>
      {/* Desktop: horizontal row of buttons. The "BAND" label was dropped —
          tile-chrome and panel-head already say "Band" above this control.
          width:100% so the row fills its container and wraps as the tile
          narrows. */}
      <div className="ctrl-group hide-mobile" style={{ width: '100%' }}>
        <div className="btn-row wrap" style={{ width: '100%' }}>
          {HF_BANDS.map((band) => (
            <button
              key={band.name}
              type="button"
              draggable
              onDragStart={(e) => {
                e.dataTransfer.setData(toolbarFavDragMime('band'), band.name);
                e.dataTransfer.effectAllowed = 'move';
              }}
              onClick={() => selectBand(band)}
              className={`btn sm ${currentBand === band.name ? 'active' : ''}`}
              title={`${band.name} — drag onto a toolbar favorite slot to pin`}
            >
              {band.name}
            </button>
          ))}
        </div>
      </div>

      {/* Mobile: dropdown */}
      <div className="ctrl-group show-mobile" style={{ display: 'none' }}>
        <select
          value={currentBand}
          onChange={(e) => {
            const band = HF_BANDS.find((b) => b.name === e.target.value);
            if (band) selectBand(band);
          }}
          className="band-select"
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
          {HF_BANDS.map((band) => (
            <option key={band.name} value={band.name}>
              {band.name}
            </option>
          ))}
        </select>
      </div>
    </>
  );
}
