// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.
//
// REST client for the persisted FT8/FT4 workspace preferences (the curated
// WSJT-X/JTDX KEEP set that is pure behaviour/UI, not radio control). Backed by
// the server (zeus-prefs.db) so the operator's choices survive desktop restarts.
// Defaults mirror Zeus.Contracts.Ft8Settings exactly — surfacing existing engine
// knobs, never changing a default an operator already feels. TX still requires an
// explicit arm; none of these flags transmit on their own.

import { ApiError } from './client';

export type Ft8Settings = {
  // TX & auto-sequence
  autoSequence: boolean;
  callFirst: boolean;
  holdTxFreq: boolean;
  disableTxAfter73: boolean;
  /** 0 = 1st (even), 1 = 2nd (odd). */
  defaultTxSlot: number;
  defaultTxOffsetHz: number;
  // Advanced sequence flags (default off to match WSJT-X)
  rr73InsteadOfRrr: boolean;
  skipGrid: boolean;
  /** Caller max retries before giving up (0 = unlimited). */
  callerMaxRetries: number;
  // Macros
  cqMessage: string;
  cqDxMessage: string;
  freeTextMacro: string;
  // Decode
  /** Decode depth as passes, matching the engine scale (1 = Normal/floor,
   *  >1 = Deep/multi). Default 3 mirrors the engine. 1..4. */
  decodePasses: number;
  showOnlyCq: boolean;
  hideWorkedBefore: boolean;
  // Logging
  autoLog: boolean;
  promptBeforeLog: boolean;
  clearDxAfterLog: boolean;
  reportToComment: boolean;
};

/** Defaults must match Zeus.Contracts.Ft8Settings defaults exactly. */
export const FT8_SETTINGS_DEFAULTS: Ft8Settings = {
  autoSequence: true,
  callFirst: false,
  holdTxFreq: false,
  disableTxAfter73: true,
  defaultTxSlot: 0,
  defaultTxOffsetHz: 1500,
  rr73InsteadOfRrr: true,
  skipGrid: false,
  callerMaxRetries: 0,
  cqMessage: 'CQ',
  cqDxMessage: 'CQ DX',
  freeTextMacro: '',
  decodePasses: 3,
  showOnlyCq: false,
  hideWorkedBefore: false,
  autoLog: true,
  promptBeforeLog: false,
  clearDxAfterLog: true,
  reportToComment: false,
};

function normalize(raw: unknown): Ft8Settings {
  const r = (raw ?? {}) as Record<string, unknown>;
  const d = FT8_SETTINGS_DEFAULTS;
  const bool = (v: unknown, f: boolean) => (typeof v === 'boolean' ? v : f);
  const num = (v: unknown, f: number) => (typeof v === 'number' && Number.isFinite(v) ? v : f);
  const str = (v: unknown, f: string) => (typeof v === 'string' ? v : f);
  return {
    autoSequence: bool(r.autoSequence, d.autoSequence),
    callFirst: bool(r.callFirst, d.callFirst),
    holdTxFreq: bool(r.holdTxFreq, d.holdTxFreq),
    disableTxAfter73: bool(r.disableTxAfter73, d.disableTxAfter73),
    defaultTxSlot: num(r.defaultTxSlot, d.defaultTxSlot) === 0 ? 0 : 1,
    defaultTxOffsetHz: num(r.defaultTxOffsetHz, d.defaultTxOffsetHz),
    rr73InsteadOfRrr: bool(r.rr73InsteadOfRrr, d.rr73InsteadOfRrr),
    skipGrid: bool(r.skipGrid, d.skipGrid),
    callerMaxRetries: num(r.callerMaxRetries, d.callerMaxRetries),
    cqMessage: str(r.cqMessage, d.cqMessage),
    cqDxMessage: str(r.cqDxMessage, d.cqDxMessage),
    freeTextMacro: str(r.freeTextMacro, d.freeTextMacro),
    decodePasses: num(r.decodePasses, d.decodePasses),
    showOnlyCq: bool(r.showOnlyCq, d.showOnlyCq),
    hideWorkedBefore: bool(r.hideWorkedBefore, d.hideWorkedBefore),
    autoLog: bool(r.autoLog, d.autoLog),
    promptBeforeLog: bool(r.promptBeforeLog, d.promptBeforeLog),
    clearDxAfterLog: bool(r.clearDxAfterLog, d.clearDxAfterLog),
    reportToComment: bool(r.reportToComment, d.reportToComment),
  };
}

async function jsonFetch(input: RequestInfo, init: RequestInit | undefined): Promise<Ft8Settings> {
  const res = await fetch(input, init);
  if (!res.ok) throw new ApiError(res.status, `${res.status} ${res.statusText}`);
  return normalize((await res.json()) as unknown);
}

export function getFt8Settings(signal?: AbortSignal): Promise<Ft8Settings> {
  return jsonFetch('/api/ft8/settings', { signal });
}

export function postFt8Settings(settings: Ft8Settings, signal?: AbortSignal): Promise<Ft8Settings> {
  return jsonFetch('/api/ft8/settings', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(settings),
    signal,
  });
}
