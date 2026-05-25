// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Simone Fabris (IU3QEZ), and contributors.

import { useCallback, useRef } from 'react';
import {
  setIncrementalTuning,
  type IncrementalTuningMode,
} from '../api/client';
import { useConnectionStore } from '../state/connection-store';

const DEBOUNCE_MS = 100;

function filterAwareStep(bwHz: number): number {
  return bwHz <= 250 ? 5 : 10;
}

export function RitXitOffsetRow() {
  const itMode = useConnectionStore((s) => s.itMode);
  const ritOffsetHz = useConnectionStore((s) => s.ritOffsetHz);
  const xitOffsetHz = useConnectionStore((s) => s.xitOffsetHz);
  const applyState = useConnectionStore((s) => s.applyState);

  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);

  const post = useCallback(
    (mode: IncrementalTuningMode, newOffset: number) => {
      if (timer.current != null) clearTimeout(timer.current);
      timer.current = setTimeout(() => {
        timer.current = null;
        setIncrementalTuning(mode, newOffset)
          .then(applyState)
          .catch(() => {});
      }, DEBOUNCE_MS);
    },
    [applyState],
  );

  const nudge = useCallback(
    (dir: 1 | -1) => {
      const s = useConnectionStore.getState();
      const mode = s.itMode;
      const curOffset = mode === 'Rit' ? s.ritOffsetHz : s.xitOffsetHz;
      const bw = Math.abs(s.filterHighHz - s.filterLowHz);
      const stp = filterAwareStep(bw);
      const next = Math.max(-3000, Math.min(3000, curOffset + dir * stp));
      useConnectionStore.setState(
        mode === 'Rit' ? { ritOffsetHz: next } : { xitOffsetHz: next },
      );
      post(mode, next);
    },
    [post],
  );

  const clear = useCallback(() => {
    setIncrementalTuning('Off', 0, true).then(applyState).catch(() => {});
  }, [applyState]);

  if (itMode === 'Off') return null;

  const offset = itMode === 'Rit' ? ritOffsetHz : xitOffsetHz;
  const { filterLowHz, filterHighHz } = useConnectionStore.getState();
  const bw = Math.abs(filterHighHz - filterLowHz);
  const step = filterAwareStep(bw);
  const sign = offset >= 0 ? '+' : '';

  return (
    <div
      className="freq-bot mono"
      style={{
        display: 'flex',
        alignItems: 'center',
        gap: 6,
        marginTop: 2,
        fontSize: '0.8rem',
        color: 'var(--fg-1)',
      }}
    >
      <span
        style={{ color: 'var(--accent)', fontWeight: 700, minWidth: 24 }}
      >
        {itMode.toUpperCase()}
      </span>
      <button
        type="button"
        className="btn ghost"
        style={{ padding: '0 4px', fontSize: '0.75rem', lineHeight: 1 }}
        onClick={() => nudge(-1)}
        title={`−${step} Hz`}
      >
        ▼
      </button>
      <span style={{ minWidth: 60, textAlign: 'center' }}>
        {sign}{offset} Hz
      </span>
      <button
        type="button"
        className="btn ghost"
        style={{ padding: '0 4px', fontSize: '0.75rem', lineHeight: 1 }}
        onClick={() => nudge(1)}
        title={`+${step} Hz`}
      >
        ▲
      </button>
      <button
        type="button"
        className="btn ghost"
        style={{ padding: '0 4px', fontSize: '0.7rem' }}
        onClick={clear}
        title="Clear offset and turn off"
      >
        Clr
      </button>
    </div>
  );
}
