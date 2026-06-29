// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Issue #1162 — flip the workspace to the operator's designated "TX layout"
// while MOX or TUN is asserted, and back to the layout they came from when
// they unkey. The designation lives per radio in layout-store.txLayoutId
// (server-persisted); when empty the auto-switch disengages and this hook
// is inert.

import { useEffect, useRef } from 'react';
import { useLayoutStore } from '../state/layout-store';
import { useTxStore } from '../state/tx-store';

export function useTxLayoutSwitch(): void {
  const moxOn = useTxStore((s) => s.moxOn);
  const tunOn = useTxStore((s) => s.tunOn);
  // Captured at the rising key edge so unkey returns to whatever the operator
  // was on, even if they manually tab-switched while transmitting. Reset to
  // null on the falling edge so we never restore stale state on a later
  // session.
  const previousLayoutIdRef = useRef<string | null>(null);

  useEffect(() => {
    const keyed = moxOn || tunOn;
    const { txLayoutId, activeLayoutId, layouts, setActiveLayout } =
      useLayoutStore.getState();

    if (keyed) {
      // No TX layout designated, the TX layout is already active, or the
      // designated id has been deleted — nothing to do, but still mark that
      // we observed the rising edge so the falling edge doesn't try to
      // restore a stale "previous" from before.
      if (
        !txLayoutId ||
        txLayoutId === activeLayoutId ||
        !layouts.some((l) => l.id === txLayoutId)
      ) {
        if (previousLayoutIdRef.current === null) {
          previousLayoutIdRef.current = activeLayoutId;
        }
        return;
      }
      if (previousLayoutIdRef.current === null) {
        previousLayoutIdRef.current = activeLayoutId;
      }
      setActiveLayout(txLayoutId);
      return;
    }

    const prev = previousLayoutIdRef.current;
    previousLayoutIdRef.current = null;
    if (!prev) return;
    // Only auto-return when we're still on the TX layout. If the operator
    // hand-picked a different tab while transmitting, respect that choice
    // and leave them on it.
    if (
      txLayoutId &&
      activeLayoutId === txLayoutId &&
      prev !== txLayoutId &&
      layouts.some((l) => l.id === prev)
    ) {
      setActiveLayout(prev);
    }
  }, [moxOn, tunOn]);
}
