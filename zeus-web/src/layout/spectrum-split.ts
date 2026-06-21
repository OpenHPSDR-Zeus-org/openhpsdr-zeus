// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// Shared panadapter/waterfall split model. The desktop HeroPanel and the
// mobile spectrum stack both let the operator drag a divider to rebalance how
// much height goes to the panadapter vs. the waterfall. The fraction, clamp
// bounds, and localStorage mirror live here so both surfaces agree and a split
// chosen on one follows the operator to the other (same browser origin).

// Persisted spectrum/waterfall split: fraction of the stack height given to
// the panadapter (the waterfall gets the remainder). Default 0.4 so the
// waterfall is the larger of the two out of the box; the operator can drag
// the divider to rebalance and the choice survives reloads via localStorage.
export const SPLIT_STORAGE_KEY = 'zeus.layout.spectrumSplit';
export const SPLIT_CONFIG_KEY = 'spectrumSplit';
export const DEFAULT_SPLIT = 0.4;
export const MIN_SPLIT = 0.08;
export const MAX_SPLIT = 0.85;

export function clampSplit(v: number): number {
  return Math.min(MAX_SPLIT, Math.max(MIN_SPLIT, v));
}

export function isValidSplit(v: number): boolean {
  return Number.isFinite(v) && v >= MIN_SPLIT && v <= MAX_SPLIT;
}

export function readInstanceSplit(raw: unknown): number | null {
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return null;
  const v = (raw as Record<string, unknown>)[SPLIT_CONFIG_KEY];
  return typeof v === 'number' && isValidSplit(v) ? v : null;
}

export function mergeInstanceSplit(raw: unknown, split: number): Record<string, unknown> {
  const base =
    raw && typeof raw === 'object' && !Array.isArray(raw)
      ? { ...(raw as Record<string, unknown>) }
      : {};
  base[SPLIT_CONFIG_KEY] = clampSplit(split);
  return base;
}

export function readLegacySplit(): number | null {
  try {
    if (typeof localStorage === 'undefined') return null;
    const raw = localStorage.getItem(SPLIT_STORAGE_KEY);
    if (raw === null) return null;
    const v = Number.parseFloat(raw);
    if (!isValidSplit(v)) return null;
    return v;
  } catch {
    return null;
  }
}

export function readInitialSplit(raw: unknown): number {
  return readInstanceSplit(raw) ?? readLegacySplit() ?? DEFAULT_SPLIT;
}

export function writeLegacySplit(v: number): void {
  try {
    if (typeof localStorage === 'undefined') return;
    localStorage.setItem(SPLIT_STORAGE_KEY, String(clampSplit(v)));
  } catch {
    // quota exceeded / private mode — in-memory state still holds for this session.
  }
}
