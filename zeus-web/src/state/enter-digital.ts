// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Entry point for the Zeus-level digital modes (FT8/FT4/WSPR). They are NOT WDSP
// demod modes — selecting one opens its dedicated workspace and auto-configures
// the radio. Shared by every mode picker (the Mode tile and the toolbar mode
// favorites) so the digital modes are reachable wherever modes are selected.
// Lives in its own module (not ft8-store/digital-mode) to avoid an import cycle.

import { useFt8Store } from './ft8-store';
import { useWsprStore } from './wspr-store';

export const DIGITAL_ENTRY_KEYS = ['FT8', 'FT4', 'WSPR'] as const;
export type DigitalEntryKey = (typeof DIGITAL_ENTRY_KEYS)[number];

export function isDigitalEntryKey(key: string): key is DigitalEntryKey {
  return (DIGITAL_ENTRY_KEYS as readonly string[]).includes(key);
}

/**
 * Enter a digital mode, closing whichever other digital workspace is open
 * (FT8/FT4/WSPR are mutually exclusive — they all retune the radio). Entry
 * snapshots the prior freq+mode and QSYs the MAIN radio to the mode's digital
 * dial; the floating DigitalWindow pops open off the same store `open` flag.
 */
export function enterDigital(target: DigitalEntryKey): void {
  const ft8 = useFt8Store.getState();
  const wspr = useWsprStore.getState();
  if (target === 'WSPR') {
    if (ft8.open) {
      // Switching FT8/FT4 → WSPR: carry FT8's pre-digital snapshot forward and
      // close WITHOUT restoring (the close's async restore would otherwise race
      // WSPR's QSY, and snapshotting again in openWorkspace would capture the
      // still-DIGU dial). WSPR's exit then restores the operator's real config.
      const carried = ft8.priorRadio ?? undefined;
      ft8.closeWorkspace({ restore: false });
      wspr.openWorkspace({ prior: carried });
    } else {
      wspr.openWorkspace();
    }
  } else {
    if (wspr.open) {
      const carried = wspr.priorRadio ?? undefined;
      wspr.closeWorkspace({ restore: false });
      ft8.openWorkspace({ protocol: target, prior: carried });
    } else {
      ft8.openWorkspace({ protocol: target });
    }
  }
}

/**
 * Leave whichever digital mode is engaged, restoring the radio to the freq+mode
 * it was on before entry (closeWorkspace → restoreRadio). Safe to call when
 * nothing is engaged (no-op). Closing the pop-out window routes through here.
 */
export function exitDigital(): void {
  const ft8 = useFt8Store.getState();
  const wspr = useWsprStore.getState();
  if (ft8.open) ft8.closeWorkspace();
  if (wspr.open) wspr.closeWorkspace();
}

/**
 * True when `target` is the currently engaged digital mode. WSPR matches on the
 * WSPR store; FT8/FT4 match on the FT8 store being open AND its active protocol
 * (so the FT8 button is "depressed" only for FT8, the FT4 button only for FT4).
 */
export function isDigitalEngaged(target: DigitalEntryKey): boolean {
  const ft8 = useFt8Store.getState();
  const wspr = useWsprStore.getState();
  if (target === 'WSPR') return wspr.open;
  return ft8.open && ft8.protocol === target;
}

/**
 * Mode-button toggle: if `target` is already engaged, exit (un-depress, restore
 * the radio); otherwise enter it. Drives the depressed-while-engaged behaviour
 * of the FT8/FT4/WSPR mode-picker buttons.
 */
export function toggleDigital(target: DigitalEntryKey): void {
  if (isDigitalEngaged(target)) {
    exitDigital();
  } else {
    enterDigital(target);
  }
}
