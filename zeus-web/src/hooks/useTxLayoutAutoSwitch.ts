// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// TX layout auto-switch (issue #1164). When the operator has ticked
// "Auto-switch to this workspace when transmitting" on one workspace tab,
// keying MOX/TUN flips the active layout to that tab and un-keying returns
// to whichever layout was active before. The "previous" layout is held in a
// ref local to the hook so it doesn't survive reload — auto-switch is a
// session-only behaviour, the next session starts fresh on the user-picked
// active layout.

import { useEffect, useRef } from 'react';
import { useLayoutStore, parseLayoutOrDefault } from '../state/layout-store';
import { useTxStore } from '../state/tx-store';

export function useTxLayoutAutoSwitch(): void {
  // Tracks the layout we left when TX started, so un-key can return to it.
  // Cleared whenever the operator manually picks a different layout while TX
  // is held (we don't want to drag them off the workspace they intentionally
  // navigated to).
  const restoreLayoutIdRef = useRef<string | null>(null);

  useEffect(() => {
    let prevTxOn =
      useTxStore.getState().moxOn || useTxStore.getState().tunOn;
    let prevActiveLayoutId = useLayoutStore.getState().activeLayoutId;

    // TX edges drive the auto-switch.
    const unsubTx = useTxStore.subscribe((state) => {
      const txOn = state.moxOn || state.tunOn;
      if (txOn === prevTxOn) return;
      prevTxOn = txOn;
      const layouts = useLayoutStore.getState().layouts;
      const target = layouts.find(
        (l) => parseLayoutOrDefault(l.layoutJson).autoSwitchOnTx === true,
      );
      if (!target) return;

      const active = useLayoutStore.getState().activeLayoutId;
      if (txOn) {
        // TX edge ON — remember where we were so we can come back, and switch
        // to the TX target unless we're already on it.
        if (active !== target.id) {
          restoreLayoutIdRef.current = active;
          useLayoutStore.getState().setActiveLayout(target.id);
        }
      } else {
        // TX edge OFF — restore the prior layout, but only if we're still on
        // the TX target (the operator may have moved off it during TX, in
        // which case their choice wins).
        const restore = restoreLayoutIdRef.current;
        restoreLayoutIdRef.current = null;
        if (restore && active === target.id && layouts.some((l) => l.id === restore)) {
          useLayoutStore.getState().setActiveLayout(restore);
        }
      }
    });

    // Operator manually switching layouts while TX is held cancels the
    // restore — don't yank them back to the previous workspace when they
    // deliberately navigated elsewhere.
    const unsubLayout = useLayoutStore.subscribe((state) => {
      const next = state.activeLayoutId;
      if (next === prevActiveLayoutId) return;
      const txOn =
        useTxStore.getState().moxOn || useTxStore.getState().tunOn;
      const target = state.layouts.find(
        (l) => parseLayoutOrDefault(l.layoutJson).autoSwitchOnTx === true,
      );
      if (txOn && target && next !== target.id) {
        restoreLayoutIdRef.current = null;
      }
      prevActiveLayoutId = next;
    });

    return () => {
      unsubTx();
      unsubLayout();
    };
  }, []);
}
