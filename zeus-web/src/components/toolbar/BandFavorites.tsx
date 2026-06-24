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
  type BandMemoryEntry,
  type RadioStateDto,
  type RxMode,
} from '../../api/client';
import { useConnectionStore } from '../../state/connection-store';
import {
  getReceiverMode,
  getReceiverVfoHz,
  optimisticSetReceiverMode,
  optimisticSetReceiverVfo,
  postReceiverMode,
  postReceiverVfo,
  rxIndexOf,
  type ReceiverKey,
} from '../../state/receiver-state';
import { viewCenterFor } from '../../state/view-center';
import {
  BAND_MEMORY_UPDATED_EVENT,
  type BandMemoryUpdatedDetail,
} from '../../util/band-memory';
import { BANDS, bandOf } from '../design/data';
import { ToolbarFavorites, type ToolbarOption } from './ToolbarFavorites';

type BandEntry = {
  name: string;
  centerHz: number;
};

type PendingBandHzSave = {
  band: string;
  hz: number;
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

function withRestoredMode(state: RadioStateDto, receiver: ReceiverKey, mode: RxMode): RadioStateDto {
  const idx = rxIndexOf(receiver);
  if (idx === 0) return { ...state, mode };
  if (idx === 1) return { ...state, modeB: mode };
  return {
    ...state,
    receivers: state.receivers?.map((r) => (r.index === idx ? { ...r, mode } : r)) ?? state.receivers,
  };
}

export function BandFavorites() {
  // Follow the focused receiver (0=RX1, 1=RX2, >=2=RX3+) so band selection tunes
  // whichever receiver the operator is working in.
  const focusedRxIndex = useConnectionStore((s) => s.focusedRxIndex);
  const activeVfoHz = useConnectionStore((s) => getReceiverVfoHz(s, focusedRxIndex));
  const activeMode = useConnectionStore((s) => getReceiverMode(s, focusedRxIndex));
  const applyState = useConnectionStore((s) => s.applyState);

  const [currentBand, setCurrentBand] = useState<string>(() => bandOf(activeVfoHz));
  const memoryRef = useRef<Map<string, BandMemoryEntry>>(new Map());
  const saveTimerRef = useRef<number | null>(null);
  const pendingSaveRef = useRef<PendingBandHzSave | null>(null);
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

  useEffect(() => {
    const onBandMemoryUpdated = (event: Event) => {
      const { detail } = event as CustomEvent<BandMemoryUpdatedDetail>;
      if (!detail) return;
      memoryRef.current.set(detail.band, detail);
    };

    window.addEventListener(BAND_MEMORY_UPDATED_EVENT, onBandMemoryUpdated);
    return () => {
      window.removeEventListener(BAND_MEMORY_UPDATED_EVENT, onBandMemoryUpdated);
    };
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
    const remembered = memoryRef.current.get(pending.band);
    if (!remembered) return;

    const next = { ...remembered, hz: pending.hz };
    memoryRef.current.set(next.band, next);
    saveBandMemory(next.band, next.hz, next.mode).catch(() => { /* next tune retries */ });
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

    pendingSaveRef.current = { band, hz: activeVfoHz };
    clearSaveTimer();
    saveTimerRef.current = window.setTimeout(flushPendingSave, SAVE_DEBOUNCE_MS);
  }, [activeVfoHz, clearSaveTimer, flushPendingSave]);

  const onSelect = useCallback(
    (key: string) => {
      const band = HF_BANDS.find((b) => b.name === key);
      if (!band) return;
      if (band.name !== currentBand) flushPendingSave();
      const stored = memoryRef.current.get(band.name);
      const targetHz = stored?.hz ?? band.centerHz;
      const targetMode: RxMode | null = stored?.mode ?? null;

      viewCenterFor(focusedRxIndex).markOptimisticTune();
      optimisticSetReceiverVfo(focusedRxIndex, targetHz);
      if (targetMode && targetMode !== activeMode) {
        optimisticSetReceiverMode(focusedRxIndex, targetMode);
      }

      void (async () => {
        let modeRestored = !targetMode || targetMode === activeMode;
        if (targetMode && targetMode !== activeMode) {
          try {
            applyState(withRestoredMode(
              await postReceiverMode(focusedRxIndex, targetMode),
              focusedRxIndex,
              targetMode,
            ));
            modeRestored = true;
          } catch {
            /* next state poll reconciles */
          }
        }
        try {
          const next = await postReceiverVfo(focusedRxIndex, targetHz);
          applyState(
            targetMode && modeRestored
              ? withRestoredMode(next, focusedRxIndex, targetMode)
              : next,
          );
        } catch {
          /* next state poll reconciles */
        }
      })();
    },
    [focusedRxIndex, activeMode, applyState, currentBand, flushPendingSave],
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
