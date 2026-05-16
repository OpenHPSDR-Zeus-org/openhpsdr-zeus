// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// Tiny client for /api/theme-settings — persists the operator's chosen
// theme ("dark" | "light") and per-CSS-variable colour overrides in LiteDB
// so the look-and-feel follows them across browsers + devices.
// Mirrors src/api/nrUiPrefs.ts; same minimal-API call shape.

export type ThemeIdRaw = 'dark' | 'light';

export type ThemeSettingsState = {
  theme: ThemeIdRaw;
  overrides: Record<string, string>;
};

type ThemeSettingsDtoRaw = {
  theme?: string;
  overrides?: Record<string, string> | null;
};

function normalize(raw: ThemeSettingsDtoRaw): ThemeSettingsState {
  const theme: ThemeIdRaw = raw.theme === 'light' ? 'light' : 'dark';
  const overrides: Record<string, string> = {};
  if (raw.overrides) {
    for (const [k, v] of Object.entries(raw.overrides)) {
      if (typeof v === 'string' && /^#[0-9A-Fa-f]{6}$/.test(v)) {
        overrides[k] = v.toUpperCase();
      }
    }
  }
  return { theme, overrides };
}

export async function fetchThemeSettings(signal?: AbortSignal): Promise<ThemeSettingsState> {
  const res = await fetch('/api/theme-settings', { signal });
  if (!res.ok) throw new Error(`GET /api/theme-settings → ${res.status}`);
  return normalize((await res.json()) as ThemeSettingsDtoRaw);
}

export async function updateThemeSettings(
  next: ThemeSettingsState,
  signal?: AbortSignal,
): Promise<ThemeSettingsState> {
  const res = await fetch('/api/theme-settings', {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ theme: next.theme, overrides: next.overrides }),
    signal,
  });
  if (!res.ok) throw new Error(`PUT /api/theme-settings → ${res.status}`);
  return normalize((await res.json()) as ThemeSettingsDtoRaw);
}
