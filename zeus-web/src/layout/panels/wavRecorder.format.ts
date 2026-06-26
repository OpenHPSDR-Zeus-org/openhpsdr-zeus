// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Pure formatting + folder-tree helpers for the WAV Recorder panel.
// Kept side-effect-free so they can be unit-tested without a DOM.

/** Human-readable byte count: B / KB / MB / GB. */
export function fmtBytes(b: number): string {
  if (!Number.isFinite(b) || b < 0) return '—';
  if (b < 1024) return `${b} B`;
  if (b < 1024 * 1024) return `${(b / 1024).toFixed(0)} KB`;
  if (b < 1024 * 1024 * 1024) return `${(b / 1024 / 1024).toFixed(1)} MB`;
  return `${(b / 1024 / 1024 / 1024).toFixed(2)} GB`;
}

/** Seconds → mm:ss (clamped at 0, no negative). Hours roll into minutes. */
export function fmtClock(seconds: number): string {
  const s = Number.isFinite(seconds) && seconds > 0 ? Math.floor(seconds) : 0;
  const mm = Math.floor(s / 60);
  const ss = s % 60;
  return `${String(mm).padStart(2, '0')}:${String(ss).padStart(2, '0')}`;
}

/** dBFS readout. The backend floors at -100; treat anything at/below as -∞. */
export function fmtDb(db: number): string {
  if (!Number.isFinite(db) || db <= -100) return '-∞';
  return db.toFixed(1);
}

/** Local short date+time for a recording's modified timestamp. */
export function fmtDate(unixMs: number): string {
  if (!Number.isFinite(unixMs) || unixMs <= 0) return '—';
  const d = new Date(unixMs);
  return d.toLocaleString(undefined, {
    year: 'numeric',
    month: 'short',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
  });
}

/** Parent folder of a forward-slash root-relative path. Root → ''. */
export function parentOf(path: string): string {
  const i = path.lastIndexOf('/');
  return i < 0 ? '' : path.slice(0, i);
}

/** Last path segment (display name) of a forward-slash path. */
export function baseName(path: string): string {
  const i = path.lastIndexOf('/');
  return i < 0 ? path : path.slice(i + 1);
}

/** Join a parent folder and a child segment into a root-relative path. */
export function joinFolder(parent: string, child: string): string {
  const p = parent.replace(/\/+$/, '');
  const c = child.replace(/^\/+/, '').replace(/\/+$/, '');
  return p ? `${p}/${c}` : c;
}

/** Immediate child folders of `current`, sorted by display name. */
export function childFolders(folders: readonly string[], current: string): string[] {
  return folders
    .filter((f) => f !== '' && parentOf(f) === current)
    .slice()
    .sort((a, b) => baseName(a).localeCompare(baseName(b)));
}

/** Breadcrumb segments from root to `current`, each with its cumulative path. */
export function breadcrumb(current: string): Array<{ label: string; path: string }> {
  const crumbs: Array<{ label: string; path: string }> = [{ label: 'Recordings', path: '' }];
  if (!current) return crumbs;
  const parts = current.split('/');
  let acc = '';
  for (const part of parts) {
    if (!part) continue;
    acc = acc ? `${acc}/${part}` : part;
    crumbs.push({ label: part, path: acc });
  }
  return crumbs;
}

/**
 * Map a 0..1 peak fraction to a count of lit LED segments, and classify each
 * segment index into its colour zone. Zones are by segment POSITION (classic
 * LED ladder), not the overall level: green up to ~66%, amber to ~88%, red on
 * the top. Returns a helper closure the canvas draw loop calls per segment.
 */
export type Zone = 'green' | 'amber' | 'red';
export function segmentZone(index: number, total: number): Zone {
  const frac = total <= 1 ? 0 : index / (total - 1);
  if (frac >= 0.88) return 'red';
  if (frac >= 0.66) return 'amber';
  return 'green';
}

/** Number of lit segments for a given 0..1 level over `total` segments. */
export function litSegments(level: number, total: number): number {
  const clamped = Number.isFinite(level) ? Math.max(0, Math.min(1, level)) : 0;
  return Math.round(clamped * total);
}

/**
 * Abbreviate an absolute filesystem path for a compact button label, keeping
 * the last `tailSegments` path segments and prefixing an ellipsis when the
 * path was shortened. Handles both POSIX (`/`) and Windows (`\`) separators so
 * the label is correct regardless of the server platform. The full path still
 * belongs in a `title` tooltip. Empty input → ''.
 */
export function abbreviatePath(path: string, tailSegments = 2): string {
  if (!path) return '';
  const sep = path.includes('\\') ? '\\' : '/';
  const parts = path.split(/[\\/]+/).filter(Boolean);
  if (parts.length <= tailSegments) return path;
  return `…${sep}${parts.slice(-tailSegments).join(sep)}`;
}
