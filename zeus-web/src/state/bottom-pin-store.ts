// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
// See LICENSE for the full GPL text.

import { create } from 'zustand';
import { fetchBottomPin, updateBottomPin } from '../api/bottom-pin';

// Per-slot pin state for the classic-layout bottom row (Logbook + TX
// Stage Meters). Persisted server-side in zeus-prefs.db so the layout
// choice follows the operator across browsers / devices.
//
// Initial render uses an optimistic both-pinned default until the
// hydrateFromServer call below resolves. After that, every togglePin
// PUTs the new state to /api/bottom-pin and rolls back on error.

export type BottomSlotId = 'logbook' | 'txmeters';

const LEGACY_STORAGE_KEY = 'zeus.classic.bottomPin';

type BottomPinState = {
  pinned: Record<BottomSlotId, boolean>;
  togglePin: (slot: BottomSlotId) => Promise<void>;
};

export const useBottomPinStore = create<BottomPinState>((set, get) => ({
  // Optimistic defaults — replaced when hydrateFromServer resolves. New
  // operators will see "both pinned" briefly even while the fetch is in
  // flight, which matches the historical layout.
  pinned: { logbook: true, txmeters: true },
  togglePin: async (slot) => {
    const prev = get().pinned;
    const next = { ...prev, [slot]: !prev[slot] };
    set({ pinned: next });
    try {
      const result = await updateBottomPin(next);
      // If the server normalised anything, reflect that.
      if (result.logbook !== next.logbook || result.txmeters !== next.txmeters) {
        set({ pinned: result });
      }
    } catch {
      // Network / server failure: revert the optimistic toggle so the UI
      // stays consistent with the persisted state.
      set({ pinned: prev });
    }
  },
}));

function readLegacyLocalStorage(): Partial<Record<BottomSlotId, boolean>> | null {
  if (typeof localStorage === 'undefined') return null;
  try {
    const raw = localStorage.getItem(LEGACY_STORAGE_KEY);
    if (!raw) return null;
    const parsed = JSON.parse(raw) as Partial<Record<BottomSlotId, boolean>>;
    return {
      logbook: typeof parsed.logbook === 'boolean' ? parsed.logbook : undefined,
      txmeters: typeof parsed.txmeters === 'boolean' ? parsed.txmeters : undefined,
    };
  } catch {
    return null;
  }
}

function clearLegacyLocalStorage(): void {
  try {
    if (typeof localStorage !== 'undefined') {
      localStorage.removeItem(LEGACY_STORAGE_KEY);
    }
  } catch {
    /* private mode — nothing to clean up */
  }
}

async function hydrateFromServer(): Promise<void> {
  let server: Awaited<ReturnType<typeof fetchBottomPin>>;
  try {
    server = await fetchBottomPin();
  } catch {
    // Backend unreachable; keep the defaults. Subsequent togglePin calls
    // will retry via PUT.
    return;
  }

  // One-shot migration: if the server is at default state and the browser
  // has a non-default localStorage entry from the v1 implementation, push
  // it up to the server so the operator's choice survives the move.
  const serverIsDefault = server.logbook && server.txmeters;
  const legacy = readLegacyLocalStorage();
  const legacyHasContent =
    legacy && (legacy.logbook === false || legacy.txmeters === false);

  if (serverIsDefault && legacyHasContent && legacy) {
    try {
      const migrated = await updateBottomPin({
        logbook: legacy.logbook ?? true,
        txmeters: legacy.txmeters ?? true,
      });
      server = migrated;
    } catch {
      // Migration failed — leave the legacy key in place so we retry next
      // load. Don't clear it.
      useBottomPinStore.setState({ pinned: server });
      return;
    }
  }

  clearLegacyLocalStorage();
  useBottomPinStore.setState({ pinned: server });
}

void hydrateFromServer();
