// SPDX-License-Identifier: GPL-2.0-or-later

import {
  selectDisplaySlice,
  useDisplayStore,
  type SpectrumReceiver,
} from '../state/display-store';

export type StitchDisplayKind = 'pan' | 'waterfall';

const MAX_STITCH_FLOOR_SHIFT_DB = 18;

function clampShiftDb(db: number): number {
  return Math.max(-MAX_STITCH_FLOOR_SHIFT_DB, Math.min(MAX_STITCH_FLOOR_SHIFT_DB, db));
}

function floorFor(receiver: SpectrumReceiver, kind: StitchDisplayKind): number | null {
  const slice = selectDisplaySlice(useDisplayStore.getState(), receiver);
  return kind === 'pan' ? slice.panFloorDb : slice.wfFloorDb;
}

export function stitchFloorShiftDb(
  receiver: SpectrumReceiver,
  kind: StitchDisplayKind,
): number {
  const a = floorFor('A', kind);
  const b = floorFor('B', kind);
  if (a === null || b === null) return 0;
  const own = receiver === 'B' ? b : a;
  const target = (a + b) / 2;
  return clampShiftDb(target - own);
}

export function normalizeStitchedBins(
  input: Float32Array,
  scratch: Float32Array | null,
  shiftDb: number,
): Float32Array {
  if (Math.abs(shiftDb) < 0.05) return input;
  const output = scratch && scratch.length === input.length
    ? scratch
    : new Float32Array(input.length);
  for (let i = 0; i < input.length; i++) {
    const v = input[i];
    output[i] = v !== undefined && Number.isFinite(v) ? v + shiftDb : (v ?? 0);
  }
  return output;
}
