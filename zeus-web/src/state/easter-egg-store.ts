// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
// See LICENSE for the full GPL text.

import { create } from 'zustand';

// Hidden HARDWARE diagnostics surface.
//
// The HARDWARE settings folder (board/DSP diagnostics) is hidden by default
// and unlocked as an easter egg: tapping the header brand-mark lightning bolt
// HARDWARE_UNLOCK_CLICKS times reveals it. State is intentionally in-memory
// only — it re-locks on every launch, so a fresh session never lists the
// folder and an accidental discovery never permanently exposes it.

export const HARDWARE_UNLOCK_CLICKS = 4;

type EasterEggState = {
  hardwareUnlocked: boolean;
  boltClicks: number;
  // Counts a brand-mark bolt tap; unlocks HARDWARE once the click threshold is
  // reached. No-op once already unlocked.
  registerBoltClick: () => void;
};

export const useEasterEggStore = create<EasterEggState>((set) => ({
  hardwareUnlocked: false,
  boltClicks: 0,
  registerBoltClick: () =>
    set((s) => {
      if (s.hardwareUnlocked) return s;
      const boltClicks = s.boltClicks + 1;
      return boltClicks >= HARDWARE_UNLOCK_CLICKS
        ? { boltClicks: 0, hardwareUnlocked: true }
        : { boltClicks };
    }),
}));
