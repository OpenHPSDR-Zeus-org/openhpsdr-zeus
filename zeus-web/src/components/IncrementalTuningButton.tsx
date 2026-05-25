// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Simone Fabris (IU3QEZ), and contributors.

import { useCallback } from 'react';
import {
  setIncrementalTuning,
  type IncrementalTuningMode,
} from '../api/client';
import { useConnectionStore } from '../state/connection-store';

const CYCLE: IncrementalTuningMode[] = ['Off', 'Rit', 'Xit'];

export function IncrementalTuningButton() {
  const itMode = useConnectionStore((s) => s.itMode);
  const ritOffsetHz = useConnectionStore((s) => s.ritOffsetHz);
  const xitOffsetHz = useConnectionStore((s) => s.xitOffsetHz);
  const applyState = useConnectionStore((s) => s.applyState);

  const cycle = useCallback(() => {
    const idx = CYCLE.indexOf(itMode);
    const next = CYCLE[(idx + 1) % CYCLE.length] ?? 'Off';
    const offset =
      next === 'Rit' ? ritOffsetHz : next === 'Xit' ? xitOffsetHz : 0;
    setIncrementalTuning(next, offset).then(applyState).catch(() => {});
  }, [itMode, ritOffsetHz, xitOffsetHz, applyState]);

  const active = itMode !== 'Off';
  const label = active ? itMode.toUpperCase() : 'RIT/XIT';

  return (
    <button
      type="button"
      className={`btn ghost hide-mobile${active ? ' accent' : ''}`}
      onClick={cycle}
      title="Cycle: Off → RIT → XIT → Off"
    >
      <span className={`led${active ? ' on' : ''}`} style={{ marginRight: 6 }} />
      {label}
    </button>
  );
}
