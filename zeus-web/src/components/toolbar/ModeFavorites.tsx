// SPDX-License-Identifier: GPL-2.0-or-later
//
// Mode picker for the control strip — three favorite mode buttons + a "⋯"
// dropdown listing every mode. Drag any mode chip in the dropdown onto a
// favorite slot to pin it.

import { useCallback } from 'react';
import { setMode, type RxMode } from '../../api/client';
import { useConnectionStore } from '../../state/connection-store';
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
  const applyState = useConnectionStore((s) => s.applyState);

  const onSelect = useCallback(
    (key: string) => {
      const m = key as RxMode;
      if (m === mode) return;
      useConnectionStore.setState({ mode: m });
      setMode(m).then(applyState).catch(() => {
        /* next state poll reconciles */
      });
    },
    [mode, applyState],
  );

  return (
    <ToolbarFavorites
      kind="mode"
      label="MODE"
      options={MODE_OPTIONS}
      currentKey={mode}
      onSelect={onSelect}
      minWidth={142}
    />
  );
}
