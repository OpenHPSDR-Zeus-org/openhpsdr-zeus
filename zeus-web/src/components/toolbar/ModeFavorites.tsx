// SPDX-License-Identifier: GPL-2.0-or-later
//
// Mode picker for the control strip — three favorite mode buttons + a "⋯"
// dropdown listing every mode. Drag any mode chip in the dropdown onto a
// favorite slot to pin it.

import { useCallback } from 'react';
import { type RxMode } from '../../api/client';
import { useConnectionStore } from '../../state/connection-store';
import {
  gangedReceiverAction,
  getReceiverMode,
  optimisticSetReceiverMode,
  postReceiverMode,
} from '../../state/receiver-state';
import { saveReceiverBandModeMemory } from '../../util/band-memory';
import { isDigitalEntryKey, toggleDigital } from '../../state/enter-digital';
import { useFt8Store } from '../../state/ft8-store';
import { useWsprStore } from '../../state/wspr-store';
import { ToolbarFavorites, type ToolbarOption } from './ToolbarFavorites';

const MODE_OPTIONS: readonly ToolbarOption[] = [
  { key: 'LSB', label: 'LSB' },
  { key: 'USB', label: 'USB' },
  { key: 'CWL', label: 'CWL' },
  { key: 'CWU', label: 'CWU' },
  { key: 'AM', label: 'AM' },
  { key: 'SAM', label: 'SAM' },
  { key: 'DSB', label: 'DSB' },
  { key: 'FM', label: 'FM' },
  { key: 'DIGL', label: 'DIGL' },
  { key: 'DIGU', label: 'DIGU' },
  { key: 'FREEDV', label: 'FreeDV' },
  // Zeus-level digital modes — these open the FT8/FT4/WSPR workspace and
  // auto-configure the radio (handled in onSelect, not via setReceiverMode).
  { key: 'FT8', label: 'FT8' },
  { key: 'FT4', label: 'FT4' },
  { key: 'WSPR', label: 'WSPR' },
];

export function ModeFavorites() {
  // Follow the focused receiver (0=RX1, 1=RX2, >=2=RX3+) so the favorite mode
  // buttons drive whichever receiver the operator is working in.
  const focusedRxIndex = useConnectionStore((s) => s.focusedRxIndex);
  const activeMode = useConnectionStore((s) => getReceiverMode(s, focusedRxIndex));

  // When a digital mode is engaged, report ITS key as current so the favorite
  // button shows depressed; un-toggling it returns to the WDSP demod mode.
  const ft8Open = useFt8Store((s) => s.open);
  const ft8Protocol = useFt8Store((s) => s.protocol);
  const wsprOpen = useWsprStore((s) => s.open);
  const engagedDigital = wsprOpen ? 'WSPR' : ft8Open ? ft8Protocol : null;
  const currentKey = engagedDigital ?? activeMode;

  const onSelect = useCallback(
    (key: string) => {
      // FT8/FT4/WSPR are Zeus-level digital modes — toggle their pop-out instead
      // of setting a WDSP demod mode.
      if (isDigitalEntryKey(key)) {
        toggleDigital(key);
        return;
      }
      const m = key as RxMode;
      if (m === activeMode) return;
      gangedReceiverAction({
        optimistic: (k) => optimisticSetReceiverMode(k, m),
        post: (k) => postReceiverMode(k, m),
      });
      if (focusedRxIndex <= 1) {
        saveReceiverBandModeMemory(focusedRxIndex === 1 ? 'B' : 'A', m);
      }
    },
    [focusedRxIndex, activeMode],
  );

  return (
    <ToolbarFavorites
      kind="mode"
      label="MODE"
      options={MODE_OPTIONS}
      currentKey={currentKey}
      onSelect={onSelect}
      minWidth={142}
    />
  );
}
