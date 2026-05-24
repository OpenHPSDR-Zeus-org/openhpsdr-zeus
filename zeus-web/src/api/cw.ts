// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

// CW endpoints: /api/cw/send, /api/cw/abort, /api/cw/settings.
// Backend lives in Zeus.Server.Hosting/CwEngine.cs (engine) +
// CwSettingsStore.cs (persistence). Status feedback for in-flight
// playback arrives over WebSocket as MsgType.CwEngineStatus (0x30) —
// consumed in realtime/ws-client.ts and pushed into useCwStore.

export type CwSettings = {
  wpm: number;
  // Farnsworth char-rate floor (slower than wpm). null = pure WPM, no
  // Farnsworth — current default.
  farnsworthWpm: number | null;
  // Exactly 6 slots; empty strings are valid (renders the slot as an
  // unlabelled placeholder button in the 2×3 grid).
  macros: string[];
  sidetoneGainDb: number;
  sidetoneHz: number;
};

type CwSettingsDtoRaw = {
  wpm?: number;
  farnsworthWpm?: number | null;
  macros?: string[];
  sidetoneGainDb?: number;
  sidetoneHz?: number;
};

const DEFAULT_MACROS = ['CQ CQ CQ', 'TU 73', 'QRZ?', 'AGN?', '5NN TU', 'UR RST'];

// Single source of truth for the fallback shape — used when the backend
// is unreachable on first load so the UI still renders a sensible macro
// pad. Server has the authoritative defaults; this just mirrors them so
// the UI doesn't flicker through "empty" on slow networks.
export const DEFAULT_CW_SETTINGS: CwSettings = {
  wpm: 22,
  farnsworthWpm: null,
  macros: [...DEFAULT_MACROS],
  sidetoneGainDb: -10,
  sidetoneHz: 600,
};

function normalize(raw: CwSettingsDtoRaw): CwSettings {
  // Variable-length: pass through whatever the server sent (operator
  // chose the count). Map non-string entries to empty strings defensively
  // so a malformed wire payload never crashes the renderer.
  const macros = (raw.macros ?? []).map((m) => (typeof m === 'string' ? m : ''));
  return {
    wpm: typeof raw.wpm === 'number' ? raw.wpm : DEFAULT_CW_SETTINGS.wpm,
    farnsworthWpm:
      typeof raw.farnsworthWpm === 'number' ? raw.farnsworthWpm : null,
    macros,
    sidetoneGainDb:
      typeof raw.sidetoneGainDb === 'number'
        ? raw.sidetoneGainDb
        : DEFAULT_CW_SETTINGS.sidetoneGainDb,
    sidetoneHz:
      typeof raw.sidetoneHz === 'number'
        ? raw.sidetoneHz
        : DEFAULT_CW_SETTINGS.sidetoneHz,
  };
}

export async function fetchCwSettings(signal?: AbortSignal): Promise<CwSettings> {
  const res = await fetch('/api/cw/settings', { signal });
  if (!res.ok) throw new Error(`GET /api/cw/settings → ${res.status}`);
  return normalize((await res.json()) as CwSettingsDtoRaw);
}

/** PATCH-shaped PUT — only the fields you pass are sent. */
export async function saveCwSettings(
  patch: Partial<CwSettings>,
  signal?: AbortSignal,
): Promise<CwSettings> {
  const body: CwSettingsDtoRaw = {};
  if (patch.wpm !== undefined) body.wpm = patch.wpm;
  if (patch.farnsworthWpm !== undefined) body.farnsworthWpm = patch.farnsworthWpm;
  if (patch.macros !== undefined) body.macros = patch.macros;
  if (patch.sidetoneGainDb !== undefined) body.sidetoneGainDb = patch.sidetoneGainDb;
  if (patch.sidetoneHz !== undefined) body.sidetoneHz = patch.sidetoneHz;

  const res = await fetch('/api/cw/settings', {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body),
    signal,
  });
  if (!res.ok) throw new Error(`PUT /api/cw/settings → ${res.status}`);
  return normalize((await res.json()) as CwSettingsDtoRaw);
}

/** Enqueue text for transmission. Returns when the server has accepted
 * the job (HTTP 202) — playback happens on the engine worker. WPM omitted
 * = use the persisted operator default (CwSettingsStore.Wpm). */
export async function sendCw(text: string, wpm?: number, signal?: AbortSignal): Promise<void> {
  const res = await fetch('/api/cw/send', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(wpm === undefined ? { text } : { text, wpm }),
    signal,
  });
  if (!res.ok && res.status !== 202) {
    throw new Error(`POST /api/cw/send → ${res.status}`);
  }
}

/** Hard abort. Drops the queue + cancels in-flight playback. Best-effort
 * — always returns OK (server-side too). */
export async function abortCw(signal?: AbortSignal): Promise<void> {
  await fetch('/api/cw/abort', { method: 'POST', signal });
}
