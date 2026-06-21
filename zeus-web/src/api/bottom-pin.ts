// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

export type BottomPinState = {
  logbook: boolean;
  txmeters: boolean;
};

type BottomPinDtoRaw = {
  // C# record properties serialise camelCase via System.Text.Json defaults
  // (ASP.NET minimal API uses JsonSerializerDefaults.Web).
  logbook?: boolean;
  txMeters?: boolean;
};

function normalize(raw: BottomPinDtoRaw): BottomPinState {
  return {
    logbook: typeof raw.logbook === 'boolean' ? raw.logbook : true,
    txmeters: typeof raw.txMeters === 'boolean' ? raw.txMeters : true,
  };
}

export async function fetchBottomPin(signal?: AbortSignal): Promise<BottomPinState> {
  const res = await fetch('/api/bottom-pin', { signal });
  if (!res.ok) throw new Error(`GET /api/bottom-pin → ${res.status}`);
  return normalize((await res.json()) as BottomPinDtoRaw);
}

export async function updateBottomPin(
  next: BottomPinState,
  signal?: AbortSignal,
): Promise<BottomPinState> {
  const res = await fetch('/api/bottom-pin', {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ logbook: next.logbook, txMeters: next.txmeters }),
    signal,
  });
  if (!res.ok) throw new Error(`PUT /api/bottom-pin → ${res.status}`);
  return normalize((await res.json()) as BottomPinDtoRaw);
}
