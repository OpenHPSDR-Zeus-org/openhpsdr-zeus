// SPDX-License-Identifier: GPL-2.0-or-later

import {
  saveBandMemory,
  type ReceiverDto,
  type RxMode,
  type TxVfo,
} from '../api/client';
import { bandOf } from '../components/design/data';
import { useConnectionStore } from '../state/connection-store';

export const BAND_MEMORY_UPDATED_EVENT = 'zeus-band-memory-updated';

// Accepts both the live connection store and a server RadioStateDto. RX2 reads
// prefer the canonical receivers[] entry and fall back to the flat *B fields.
type BandMemoryState = {
  vfoHz: number;
  vfoBHz: number;
  rx2Enabled: boolean;
  mode: RxMode;
  modeB: RxMode;
  receivers?: readonly ReceiverDto[];
};

export type BandMemoryUpdatedDetail = {
  band: string;
  hz: number;
  mode: RxMode;
};

function dispatchBandMemoryUpdated(detail: BandMemoryUpdatedDetail): void {
  if (typeof window === 'undefined') return;
  window.dispatchEvent(new CustomEvent<BandMemoryUpdatedDetail>(
    BAND_MEMORY_UPDATED_EVENT,
    { detail },
  ));
}

export function saveBandModeMemory(hz: number, mode: RxMode): void {
  const band = bandOf(hz);
  if (band === '—') return;
  const optimistic = { band, hz, mode };
  dispatchBandMemoryUpdated(optimistic);
  saveBandMemory(band, hz, mode)
    .then((saved) => dispatchBandMemoryUpdated(saved))
    .catch(() => {
      /* best-effort; the next tune or mode change retries */
    });
}

export function saveReceiverBandModeMemory(
  receiver: TxVfo,
  modeOverride?: RxMode,
  state: BandMemoryState = useConnectionStore.getState(),
): void {
  const targetB = receiver === 'B' && state.rx2Enabled;
  const entry = targetB ? state.receivers?.find((r) => r.index === 1) : undefined;
  const hz = entry?.vfoHz ?? (targetB ? state.vfoBHz : state.vfoHz);
  const mode = modeOverride ?? entry?.mode ?? (targetB ? state.modeB : state.mode);
  saveBandModeMemory(hz, mode);
}
