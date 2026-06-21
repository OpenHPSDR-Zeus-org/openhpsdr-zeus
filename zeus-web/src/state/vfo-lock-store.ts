// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// VFO lock — client-only flag that suppresses outbound `setVfo` calls so a
// user can pin the radio on a frequency without accidental retunes from
// touch gestures, scrolls, or band picks. Lives in its own store so
// `api/client.ts` (which has no dependency on `connection-store`) can read
// the gate without introducing a circular import.

import { create } from 'zustand';

export type VfoLockState = {
  locked: boolean;
  toggle: () => void;
  setLocked: (locked: boolean) => void;
};

export const useVfoLockStore = create<VfoLockState>((set) => ({
  locked: false,
  toggle: () => set((s) => ({ locked: !s.locked })),
  setLocked: (locked) => set({ locked }),
}));
