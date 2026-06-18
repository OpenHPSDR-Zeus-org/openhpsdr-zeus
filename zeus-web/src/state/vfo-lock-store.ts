// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// VFO lock — client-only flag that suppresses outbound `setVfo` calls so a
// user can pin the radio on a frequency without accidental retunes from
// touch gestures, scrolls, or band picks. Lives in its own store so
// `api/client.ts` (which has no dependency on `connection-store`) can read
// the gate without introducing a circular import.

import { create } from 'zustand';
import { persist } from 'zustand/middleware';

export type VfoLockState = {
  locked: boolean;
  toggle: () => void;
  setLocked: (locked: boolean) => void;
};

// Persisted to localStorage so the lock survives reloads — an operator who
// pins the dial expects it to stay pinned until they deliberately unlock,
// not silently re-arm on a refresh. The public API (locked / toggle /
// setLocked) is byte-identical to the previous in-memory store, so every
// reader (api/client.setVfo + setRadioLo, DbScale, WfDbScale, MobileApp,
// the new desktop VfoLockButton) is untouched. partialize keeps only the
// boolean — the action closures are recreated on each boot.
export const useVfoLockStore = create<VfoLockState>()(
  persist(
    (set) => ({
      locked: false,
      toggle: () => set((s) => ({ locked: !s.locked })),
      setLocked: (locked) => set({ locked }),
    }),
    {
      name: 'zeus.vfoLock',
      partialize: (s) => ({ locked: s.locked }),
    },
  ),
);
