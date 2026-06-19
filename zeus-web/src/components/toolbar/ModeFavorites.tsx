// SPDX-License-Identifier: GPL-2.0-or-later
//
// Mode picker for the control strip — three favorite mode buttons + a "⋯"
// dropdown listing every mode. Drag any mode chip in the dropdown onto a
// favorite slot to pin it.

import { useCallback } from 'react';
import { setMode, type RxMode, type TxVfo } from '../../api/client';
import { useConnectionStore } from '../../state/connection-store';
import { saveReceiverBandModeMemory } from '../../util/band-memory';
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
];

export function ModeFavorites() {
  const mode = useConnectionStore((s) => s.mode);
  const modeB = useConnectionStore((s) => s.modeB);
  const rx2Enabled = useConnectionStore((s) => s.rx2Enabled);
  const rxFocus = useConnectionStore((s) => s.rxFocus);
  const applyState = useConnectionStore((s) => s.applyState);
  const activeReceiver: TxVfo = rxFocus === 'B' && rx2Enabled ? 'B' : 'A';
  const activeMode = activeReceiver === 'B' ? modeB : mode;

  const onSelect = useCallback(
    (key: string) => {
      const m = key as RxMode;
      if (m === activeMode) return;
      useConnectionStore.setState(activeReceiver === 'B' ? { modeB: m } : { mode: m });
      saveReceiverBandModeMemory(activeReceiver, m);
      setMode(m, undefined, activeReceiver)
        .then((state) => {
          applyState(state);
          saveReceiverBandModeMemory(activeReceiver, undefined, state);
        })
        .catch(() => {
          /* next state poll reconciles */
        });
    },
    [activeReceiver, activeMode, applyState],
  );

  return (
    <ToolbarFavorites
      kind="mode"
      label="MODE"
      options={MODE_OPTIONS}
      currentKey={activeMode}
      onSelect={onSelect}
      minWidth={142}
    />
  );
}
