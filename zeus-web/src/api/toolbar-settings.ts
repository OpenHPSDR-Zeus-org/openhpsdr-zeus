// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// REST client for the server-side toolbar settings (Mode/Band/Step favorite
// pins + the live tuning step). Mirrors api/display.ts. Persisting these on
// the backend instead of localStorage fixes the Photino desktop bug where the
// webview binds a fresh random loopback port every launch, orphaning the
// per-origin localStorage and resetting the tuning step to 500 Hz.

export type ToolbarSettings = {
  // Three favorite-slot keys per picker, or null when the server has never
  // stored a value (fresh install / first run after upgrade). The frontend
  // keeps its built-in defaults and pushes them up on next interaction.
  mode: string[] | null;
  band: string[] | null;
  step: string[] | null;
  // Live tuning step in Hz, or null when never stored.
  stepHz: number | null;
};

type ToolbarSettingsDtoRaw = {
  mode?: string[] | null;
  band?: string[] | null;
  step?: string[] | null;
  stepHz?: number | null;
};

function normalizeSlots(raw: string[] | null | undefined): string[] | null {
  if (!Array.isArray(raw) || raw.length !== 3) return null;
  if (!raw.every((s) => typeof s === 'string' && s.length > 0)) return null;
  return raw;
}

function normalizeStepHz(raw: number | null | undefined): number | null {
  return typeof raw === 'number' && Number.isFinite(raw) && raw > 0 ? raw : null;
}

function normalize(raw: ToolbarSettingsDtoRaw): ToolbarSettings {
  return {
    mode: normalizeSlots(raw.mode),
    band: normalizeSlots(raw.band),
    step: normalizeSlots(raw.step),
    stepHz: normalizeStepHz(raw.stepHz),
  };
}

export async function fetchToolbarSettings(signal?: AbortSignal): Promise<ToolbarSettings> {
  const res = await fetch('/api/toolbar-settings', { signal });
  if (!res.ok) throw new Error(`GET /api/toolbar-settings → ${res.status}`);
  return normalize((await res.json()) as ToolbarSettingsDtoRaw);
}

// Patch only the supplied fields — null/undefined fields leave the stored
// value untouched on the server, so a step change doesn't reset the favorite
// pins and vice-versa.
export async function updateToolbarSettings(
  patch: Partial<{
    mode: string[];
    band: string[];
    step: string[];
    stepHz: number;
  }>,
  signal?: AbortSignal,
): Promise<ToolbarSettings> {
  const res = await fetch('/api/toolbar-settings', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(patch),
    signal,
  });
  if (!res.ok) throw new Error(`POST /api/toolbar-settings → ${res.status}`);
  return normalize((await res.json()) as ToolbarSettingsDtoRaw);
}
