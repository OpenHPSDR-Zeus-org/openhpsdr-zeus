// SPDX-License-Identifier: GPL-2.0-or-later
//
// Band picker for the control strip — three favorite band buttons + a "⋯"
// dropdown listing every HF band. Drag any band chip in the dropdown onto
// a favorite slot to pin it. Selecting a band restores the saved (hz, mode)
// from server-side band memory, matching BandButtons behaviour.

import { useCallback, useEffect, useRef, useState } from 'react';
import {
  fetchBandMemory,
  saveBandMemory,
  setMode,
  setVfo,
  type BandMemoryEntry,
  type RxMode,
} from '../../api/client';
import { useConnectionStore } from '../../state/connection-store';
import { BANDS, bandOf } from '../design/data';
import { ToolbarFavorites, type ToolbarOption } from './ToolbarFavorites';

type BandEntry = {
  name: string;
  centerHz: number;
};

const HF_BANDS: readonly BandEntry[] = BANDS.slice(0, 10).map((b) => ({
  name: b.n + 'm',
  centerHz: b.center,
}));

const BAND_OPTIONS: readonly ToolbarOption[] = HF_BANDS.map((b) => ({
  key: b.name,
  label: b.name,
}));

const SAVE_DEBOUNCE_MS = 500;

export function BandFavorites() {
  const vfoHz = useConnectionStore((s) => s.vfoHz);
  const mode = useConnectionStore((s) => s.mode);
  const applyState = useConnectionStore((s) => s.applyState);

  const [currentBand, setCurrentBand] = useState<string>(() => bandOf(vfoHz));
  const memoryRef = useRef<Map<string, BandMemoryEntry>>(new Map());
  const saveTimerRef = useRef<number | null>(null);

  useEffect(() => {
    const ac = new AbortController();
    fetchBandMemory(ac.signal)
      .then((entries) => {
        const m = new Map<string, BandMemoryEntry>();
        for (const e of entries) m.set(e.band, e);
        memoryRef.current = m;
      })
      .catch(() => {
        /* offline / older server — band click will fall back to centre defaults */
      });
    return () => ac.abort();
  }, []);

  useEffect(() => {
    const band = bandOf(vfoHz);
    setCurrentBand(band);
    if (band === '—') return;
    if (saveTimerRef.current !== null) window.clearTimeout(saveTimerRef.current);
    saveTimerRef.current = window.setTimeout(() => {
      saveTimerRef.current = null;
      memoryRef.current.set(band, { band, hz: vfoHz, mode });
      saveBandMemory(band, vfoHz, mode).catch(() => { /* next tune retries */ });
    }, SAVE_DEBOUNCE_MS);
    return () => {
      if (saveTimerRef.current !== null) {
        window.clearTimeout(saveTimerRef.current);
        saveTimerRef.current = null;
      }
    };
  }, [vfoHz, mode]);

  const onSelect = useCallback(
    (key: string) => {
      const band = HF_BANDS.find((b) => b.name === key);
      if (!band) return;
      const stored = memoryRef.current.get(band.name);
      const targetHz = stored?.hz ?? band.centerHz;
      const targetMode: RxMode | null = stored?.mode ?? null;

      useConnectionStore.setState({ vfoHz: targetHz });
      setVfo(targetHz).then(applyState).catch(() => { /* next state poll reconciles */ });

      if (targetMode && targetMode !== mode) {
        useConnectionStore.setState({ mode: targetMode });
        setMode(targetMode).then(applyState).catch(() => { /* next state poll reconciles */ });
      }
    },
    [applyState, mode],
  );

  return (
    <ToolbarFavorites
      kind="band"
      label="BAND"
      options={BAND_OPTIONS}
      currentKey={currentBand}
      onSelect={onSelect}
      minWidth={170}
    />
  );
}
