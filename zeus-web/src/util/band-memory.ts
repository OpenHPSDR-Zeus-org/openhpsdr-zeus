// SPDX-License-Identifier: GPL-2.0-or-later

import {
  saveBandMemory,
  type RxMode,
  type TxVfo,
} from '../api/client';
import { bandOf } from '../components/design/data';
import { useConnectionStore } from '../state/connection-store';

type BandMemoryState = {
  vfoHz: number;
  vfoBHz: number;
  rx2Enabled: boolean;
  mode: RxMode;
  modeB: RxMode;
};

export function saveBandModeMemory(hz: number, mode: RxMode): void {
  const band = bandOf(hz);
  if (band === '—') return;
  saveBandMemory(band, hz, mode).catch(() => {
    /* best-effort; the next tune or mode change retries */
  });
}

export function saveReceiverBandModeMemory(
  receiver: TxVfo,
  modeOverride?: RxMode,
  state: BandMemoryState = useConnectionStore.getState(),
): void {
  const targetB = receiver === 'B' && state.rx2Enabled;
  const hz = targetB ? state.vfoBHz : state.vfoHz;
  const mode = modeOverride ?? (targetB ? state.modeB : state.mode);
  saveBandModeMemory(hz, mode);
}
