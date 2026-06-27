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
 * (FT8/FT4/WSPR are mutually exclusive — they all retune the radio).
 */
export function enterDigital(target: DigitalEntryKey): void {
  const ft8 = useFt8Store.getState();
  const wspr = useWsprStore.getState();
  if (target === 'WSPR') {
    if (ft8.open) ft8.closeWorkspace();
    wspr.openWorkspace();
  } else {
    if (wspr.open) wspr.closeWorkspace();
    ft8.openWorkspace({ protocol: target });
  }
}
