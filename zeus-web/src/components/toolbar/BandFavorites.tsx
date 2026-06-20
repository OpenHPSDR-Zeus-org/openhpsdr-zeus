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
  setVfoB,
  type BandMemoryEntry,
  type RadioStateDto,
  type RxMode,
  type TxVfo,
} from '../../api/client';
import { useConnectionStore } from '../../state/connection-store';
import { viewCenterFor } from '../../state/view-center';
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

function withRestoredMode(state: RadioStateDto, receiver: TxVfo, mode: RxMode): RadioStateDto {
  return receiver === 'B' ? { ...state, modeB: mode } : { ...state, mode };
}

export function BandFavorites() {
  const vfoHz = useConnectionStore((s) => s.vfoHz);
  const vfoBHz = useConnectionStore((s) => s.vfoBHz);
  const rx2Enabled = useConnectionStore((s) => s.rx2Enabled);
  const rxFocus = useConnectionStore((s) => s.rxFocus);
  const mode = useConnectionStore((s) => s.mode);
  const modeB = useConnectionStore((s) => s.modeB);
  const applyState = useConnectionStore((s) => s.applyState);
  const activeReceiver: TxVfo = rxFocus === 'B' && rx2Enabled ? 'B' : 'A';
  const activeVfoHz = activeReceiver === 'B' ? vfoBHz : vfoHz;
  const activeMode = activeReceiver === 'B' ? modeB : mode;

  const [currentBand, setCurrentBand] = useState<string>(() => bandOf(activeVfoHz));
  const memoryRef = useRef<Map<string, BandMemoryEntry>>(new Map());
  const saveTimerRef = useRef<number | null>(null);
  const pendingSaveRef = useRef<BandMemoryEntry | null>(null);
  const lastBandRef = useRef<string>(currentBand);

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
    saveBandMemory(pending.band, pending.hz, pending.mode).catch(() => { /* next tune retries */ });
  }, [clearSaveTimer]);

  useEffect(() => {
    return () => {
      clearSaveTimer();
      pendingSaveRef.current = null;
    };
  }, [clearSaveTimer]);

  useEffect(() => {
    const band = bandOf(activeVfoHz);
    setCurrentBand(band);
    if (lastBandRef.current !== band) {
      flushPendingSave();
      lastBandRef.current = band;
    }
    if (band === '—') return;

    pendingSaveRef.current = { band, hz: activeVfoHz, mode: activeMode };
    clearSaveTimer();
    saveTimerRef.current = window.setTimeout(flushPendingSave, SAVE_DEBOUNCE_MS);
  }, [activeVfoHz, activeMode, clearSaveTimer, flushPendingSave]);

  const onSelect = useCallback(
    (key: string) => {
      const band = HF_BANDS.find((b) => b.name === key);
      if (!band) return;
      if (band.name !== currentBand) flushPendingSave();
      const stored = memoryRef.current.get(band.name);
      const targetHz = stored?.hz ?? band.centerHz;
      const targetMode: RxMode | null = stored?.mode ?? null;

      viewCenterFor(activeReceiver).markOptimisticTune();
      useConnectionStore.setState(
        activeReceiver === 'B'
          ? targetMode && targetMode !== activeMode
            ? { vfoBHz: targetHz, modeB: targetMode }
            : { vfoBHz: targetHz }
          : targetMode && targetMode !== activeMode
          ? { vfoHz: targetHz, mode: targetMode }
          : { vfoHz: targetHz },
      );
      const postVfo = activeReceiver === 'B' ? setVfoB : setVfo;

      void (async () => {
        let modeRestored = !targetMode || targetMode === activeMode;
        if (targetMode && targetMode !== activeMode) {
          try {
            applyState(withRestoredMode(
              await setMode(targetMode, undefined, activeReceiver),
              activeReceiver,
              targetMode,
            ));
            modeRestored = true;
          } catch {
            /* next state poll reconciles */
          }
        }
        try {
          const next = await postVfo(targetHz);
          applyState(
            targetMode && modeRestored
              ? withRestoredMode(next, activeReceiver, targetMode)
              : next,
          );
        } catch {
          /* next state poll reconciles */
        }
      })();
    },
    [activeReceiver, activeMode, applyState, currentBand, flushPendingSave],
  );

  return (
    <ToolbarFavorites
      kind="band"
      label="BAND"
      options={BAND_OPTIONS}
      currentKey={currentBand}
      onSelect={onSelect}
      minWidth={142}
    />
  );
}
