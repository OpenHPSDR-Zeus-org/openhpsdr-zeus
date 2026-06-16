// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

import type { TxVfo } from '../api/client';
import { bandOf } from '../components/design/data';
import { useConnectionStore } from '../state/connection-store';

const WARNING_SUPPRESS_KEY = 'zeus.tx.newBandWarning.dismissed';
const LAST_ACKED_BAND_KEY = 'zeus.tx.lastAcknowledgedBand';

export type TxBandWarningRequest = {
  id: number;
  txVfo: TxVfo;
  band: string;
  freqHz: number;
};

type Listener = (request: TxBandWarningRequest | null) => void;

let nextRequestId = 1;
let activeRequest: (TxBandWarningRequest & { resolve: (approved: boolean) => void }) | null = null;
const listeners = new Set<Listener>();

function notify() {
  const request = activeRequest
    ? {
        id: activeRequest.id,
        txVfo: activeRequest.txVfo,
        band: activeRequest.band,
        freqHz: activeRequest.freqHz,
      }
    : null;
  listeners.forEach((listener) => listener(request));
}

function storageGet(key: string): string | null {
  try {
    return typeof localStorage === 'undefined' ? null : localStorage.getItem(key);
  } catch {
    return null;
  }
}

function storageSet(key: string, value: string): void {
  try {
    if (typeof localStorage !== 'undefined') localStorage.setItem(key, value);
  } catch {
    // Private-mode / quota failures should not block TX safety.
  }
}

function txSelection() {
  const s = useConnectionStore.getState();
  const freqHz = s.txVfo === 'B' ? s.vfoBHz : s.vfoHz;
  return {
    txVfo: s.txVfo,
    freqHz,
    band: bandOf(freqHz),
  };
}

function acknowledgedBandKey(txVfo: TxVfo, band: string): string {
  return `${txVfo}:${band}`;
}

function shouldWarn(): boolean {
  return storageGet(WARNING_SUPPRESS_KEY) !== '1';
}

function lastAcknowledgedBandKey(): string | null {
  return storageGet(LAST_ACKED_BAND_KEY);
}

function recordBandAcknowledged(txVfo: TxVfo, band: string): void {
  storageSet(LAST_ACKED_BAND_KEY, acknowledgedBandKey(txVfo, band));
}

function requestWarning(req: Omit<TxBandWarningRequest, 'id'>): Promise<boolean> {
  if (!shouldWarn()) return Promise.resolve(true);
  if (activeRequest) {
    activeRequest.resolve(false);
    activeRequest = null;
  }
  return new Promise((resolve) => {
    activeRequest = {
      id: nextRequestId++,
      ...req,
      resolve,
    };
    notify();
  });
}

export function subscribeTxBandWarning(listener: Listener): () => void {
  listeners.add(listener);
  listener(activeRequest);
  return () => {
    listeners.delete(listener);
  };
}

export function resolveTxBandWarning(approved: boolean, dontShowAgain = false): void {
  const request = activeRequest;
  if (!request) return;
  if (approved && dontShowAgain) storageSet(WARNING_SUPPRESS_KEY, '1');
  activeRequest = null;
  request.resolve(approved);
  notify();
}

export async function runTxBandPreflight(): Promise<boolean> {
  const selected = txSelection();
  if (!selected.band || selected.band === '—') return true;
  const key = acknowledgedBandKey(selected.txVfo, selected.band);
  if (lastAcknowledgedBandKey() === key) return true;

  const approved = await requestWarning(selected);
  if (!approved) return false;
  recordBandAcknowledged(selected.txVfo, selected.band);
  return true;
}
