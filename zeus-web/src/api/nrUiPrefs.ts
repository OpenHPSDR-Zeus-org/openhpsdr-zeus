// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// Tiny client for /api/nr-ui-prefs — persists the per-mode expand/collapse
// state of the inline NR settings accordion (NR1/NR2/NR4) in LiteDB so the
// chevron-open preference follows the operator across browsers + devices.
// Mirrors src/api/bottom-pin.ts; same minimal-API call shape.

export type NrUiPrefsState = {
  nr1Expanded: boolean;
  nr2Expanded: boolean;
  nr4Expanded: boolean;
};

type NrUiPrefsDtoRaw = {
  // C# records serialise camelCase via System.Text.Json defaults
  // (JsonSerializerDefaults.Web on the ASP.NET minimal API).
  nr1Expanded?: boolean;
  nr2Expanded?: boolean;
  nr4Expanded?: boolean;
};

function normalize(raw: NrUiPrefsDtoRaw): NrUiPrefsState {
  return {
    nr1Expanded: raw.nr1Expanded === true,
    nr2Expanded: raw.nr2Expanded === true,
    nr4Expanded: raw.nr4Expanded === true,
  };
}

export async function fetchNrUiPrefs(signal?: AbortSignal): Promise<NrUiPrefsState> {
  const res = await fetch('/api/nr-ui-prefs', { signal });
  if (!res.ok) throw new Error(`GET /api/nr-ui-prefs → ${res.status}`);
  return normalize((await res.json()) as NrUiPrefsDtoRaw);
}

export async function updateNrUiPrefs(
  next: NrUiPrefsState,
  signal?: AbortSignal,
): Promise<NrUiPrefsState> {
  const res = await fetch('/api/nr-ui-prefs', {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({
      nr1Expanded: next.nr1Expanded,
      nr2Expanded: next.nr2Expanded,
      nr4Expanded: next.nr4Expanded,
    }),
    signal,
  });
  if (!res.ok) throw new Error(`PUT /api/nr-ui-prefs → ${res.status}`);
  return normalize((await res.json()) as NrUiPrefsDtoRaw);
}
