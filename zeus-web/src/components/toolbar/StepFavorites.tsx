// SPDX-License-Identifier: GPL-2.0-or-later
//
// Tuning-step picker for the control strip — three favorite step buttons +
// a "⋯" dropdown listing every step. Drag any chip in the dropdown onto a
// favorite slot to pin it. Step state is shared with the side-stack
// TuningStepWidget via the toolbar-favorites store.

import { useCallback } from 'react';
import { useToolbarFavoritesStore } from '../../state/toolbar-favorites-store';
import { ToolbarFavorites, type ToolbarOption } from './ToolbarFavorites';

const STEP_OPTIONS: readonly ToolbarOption[] = [
  { key: '1', label: '1 Hz' },
  { key: '10', label: '10 Hz' },
  { key: '50', label: '50 Hz' },
  { key: '100', label: '100 Hz' },
  { key: '250', label: '250 Hz' },
  { key: '500', label: '500 Hz' },
  { key: '1000', label: '1 kHz' },
  { key: '5000', label: '5 kHz' },
  { key: '9000', label: '9 kHz' },
  { key: '10000', label: '10 kHz' },
  { key: '100000', label: '100 kHz' },
  { key: '250000', label: '250 kHz' },
  { key: '1000000', label: '1 MHz' },
];

export function StepFavorites() {
  const stepHz = useToolbarFavoritesStore((s) => s.stepHz);
  const setStepHz = useToolbarFavoritesStore((s) => s.setStepHz);

  const onSelect = useCallback(
    (key: string) => {
      const hz = Number(key);
      if (Number.isFinite(hz) && hz > 0) setStepHz(hz);
    },
    [setStepHz],
  );

  return (
    <ToolbarFavorites
      kind="step"
      label="STEP"
      options={STEP_OPTIONS}
      currentKey={String(stepHz)}
      onSelect={onSelect}
      minWidth={154}
    />
  );
}
