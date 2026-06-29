// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// Shared dB ↔ fraction mapping for the immersive Meters panel. Mirrors the
// design prototype's helpers so all three widget primitives (BigArc /
// VuColumn / PullDownArc) project values onto their geometry consistently.
// Range −60..+6 dBFS is pro-audio standard; the prototype uses a linear
// mapping, not log-ish, despite the comment in the original — keeping
// linear here for visual parity.

export const DB_MIN = -60;
export const DB_MAX = 6;

export function clamp01(t: number): number {
  if (!isFinite(t)) return 0;
  return Math.max(0, Math.min(1, t));
}

export function dbToFrac(db: number): number {
  if (!isFinite(db)) return 0;
  return clamp01((db - DB_MIN) / (DB_MAX - DB_MIN));
}

export function fmtDb(db: number): string {
  if (!isFinite(db) || db <= DB_MIN + 0.05) return '−∞';
  const abs = Math.abs(db).toFixed(1);
  return (db >= 0 ? '+' : '−') + abs;
}

// WDSP / live-data sentinel: meters report ≤ −200 dBFS when bypassed or
// idle. Treat as "no signal" so we don't paint a misleading floor bar.
export function isSilent(db: number): boolean {
  return !isFinite(db) || db <= -200;
}
