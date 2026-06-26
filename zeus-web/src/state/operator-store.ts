// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Operator identity (own callsign + Maidenhead grid) for the digital modes.
// Used by the FT8 workspace for the "calling me" decode highlight and
// click-to-call message generation, and (later) by PSK Reporter / WSPRnet
// spotting. Persisted to localStorage so it survives reloads — it is operator
// preference, not radio state, so it lives client-side.

import { create } from 'zustand';

const LS_KEY = 'zeus.operator';

interface Persisted {
  call: string;
  grid: string;
}

function load(): Persisted {
  try {
    const raw = localStorage.getItem(LS_KEY);
    if (!raw) return { call: '', grid: '' };
    const j = JSON.parse(raw) as Record<string, unknown>;
    return {
      call: typeof j.call === 'string' ? j.call : '',
      grid: typeof j.grid === 'string' ? j.grid : '',
    };
  } catch {
    return { call: '', grid: '' };
  }
}

function save(p: Persisted): void {
  try {
    localStorage.setItem(LS_KEY, JSON.stringify(p));
  } catch {
    /* private mode / no storage — non-fatal, identity is just not persisted */
  }
}

interface OperatorState extends Persisted {
  setCall: (call: string) => void;
  setGrid: (grid: string) => void;
}

export const useOperatorStore = create<OperatorState>((set, get) => ({
  ...load(),
  setCall: (call) => {
    const c = call.toUpperCase().trim();
    save({ call: c, grid: get().grid });
    set({ call: c });
  },
  setGrid: (grid) => {
    const g = grid.toUpperCase().trim();
    save({ call: get().call, grid: g });
    set({ grid: g });
  },
}));
