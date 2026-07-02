// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Digital worked-before set. The Zeus Digital plugin has no logbook access, so
// the server-side workedBefore enrichment the decode table used to receive on
// every 0x38 frame moved HERE: the CORE endpoint GET /api/log/digital-worked
// (wraps LogService.GetDigitalWorkedCallsignsAsync — callsigns with a prior
// FT8/FT4 QSO, full logbook history) feeds this set, and Ft8DecodeTable
// decorates rows at RENDER time. Render-time decoration self-heals: batches
// that arrive before the fetch resolves highlight as soon as it lands.
//
// Refresh cadence mirrors the old server TTL: on FT8 pop-out open, every 60 s
// while it stays open, and after each logged QSO (auto or manual). A failed
// fetch keeps the last good set — the highlight goes stale, never breaks.

import { create } from 'zustand';
import { useFt8Store } from './ft8-store';

/** Matches the old server-side Ft8BroadcastService cache TTL. */
const REFRESH_INTERVAL_MS = 60_000;

interface DigitalWorkedState {
  /** Upper-case callsigns with a prior FT8/FT4 QSO in the logbook. */
  calls: ReadonlySet<string>;
  /** True once the first fetch has succeeded. */
  loaded: boolean;
  /** Re-fetch the worked set from GET /api/log/digital-worked. */
  refresh: (signal?: AbortSignal) => Promise<void>;
}

export const useDigitalWorkedStore = create<DigitalWorkedState>((set) => ({
  calls: new Set<string>(),
  loaded: false,

  refresh: async (signal) => {
    try {
      const res = await fetch('/api/log/digital-worked', { signal });
      if (!res.ok) return; // keep the last good set
      const j = (await res.json()) as { calls?: unknown };
      if (!Array.isArray(j.calls)) return;
      const calls = new Set<string>();
      for (const c of j.calls) {
        if (typeof c === 'string' && c.trim().length > 0) calls.add(c.trim().toUpperCase());
      }
      set({ calls, loaded: true });
    } catch {
      /* transient — the next trigger recovers */
    }
  },
}));

// Refresh lifecycle: fetch on FT8 workspace open and every 60 s while open.
// (The after-log trigger lives at the log call sites in Ft8PopBody.)
if (typeof window !== 'undefined') {
  let timer: ReturnType<typeof setInterval> | null = null;
  let wasOpen = useFt8Store.getState().open;
  useFt8Store.subscribe((s) => {
    if (s.open === wasOpen) return;
    wasOpen = s.open;
    if (s.open) {
      void useDigitalWorkedStore.getState().refresh();
      timer ??= setInterval(() => void useDigitalWorkedStore.getState().refresh(), REFRESH_INTERVAL_MS);
    } else if (timer != null) {
      clearInterval(timer);
      timer = null;
    }
  });
}
