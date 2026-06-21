// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

// String-valued board kinds are mirrored from Zeus.Contracts.HpsdrBoardKind
// (post-#218 Phase 4 unification); we intentionally use strings over the
// wire (not numeric bytes) so the JSON is legible when debugging with curl
// and resilient to new boards being added on the backend without a frontend
// recompile.
export type BoardKind =
  | 'Auto'
  | 'Metis'
  | 'Hermes'
  | 'HermesII'
  | 'Angelia'
  | 'Orion'
  | 'HermesLite2'
  | 'OrionMkII'
  | 'HermesC10'
  | 'Unknown';

export interface RadioSelection {
  preferred: BoardKind;
  connected: BoardKind;
  effective: BoardKind;
  overrideDetection: boolean;
}

// Operator-facing labels per board. Keep in sync with BOARD_OPTIONS in
// RadioSelector.tsx.
export const BOARD_LABELS: Record<BoardKind, string> = {
  Auto: 'Auto-detect',
  Metis: 'HPSDR Metis',
  Hermes: 'Hermes / ANAN-10 / ANAN-100',
  HermesII: 'Hermes-II / ANAN-10E / 100B',
  Angelia: 'ANAN-100D',
  Orion: 'ANAN-200D',
  HermesLite2: 'Hermes Lite 2',
  OrionMkII: 'ANAN G2 / 7000D / 8000D / variant',
  HermesC10: 'ANAN-G2E',
  Unknown: 'Unknown',
};

function normalizeBoard(v: unknown): BoardKind {
  if (typeof v !== 'string') return 'Unknown';
  switch (v) {
    case 'Auto':
    case 'Metis':
    case 'Hermes':
    case 'HermesII':
    case 'Angelia':
    case 'Orion':
    case 'HermesLite2':
    case 'OrionMkII':
    case 'HermesC10':
      return v;
    // Pre-#218 Phase 4 backward compatibility — the unification renamed
    // Griffin to HermesII (P1 collapse) and Atlas to Metis (P2 collapse).
    // Old persisted preferences or legacy clients sending the old names
    // get coerced to the new canonical values.
    case 'Griffin':
      return 'HermesII';
    case 'Atlas':
      return 'Metis';
    case 'Unknown':
      return v;
    default:
      return 'Unknown';
  }
}

export async function fetchRadioSelection(signal?: AbortSignal): Promise<RadioSelection> {
  const res = await fetch('/api/radio/selection', { signal });
  if (!res.ok) throw new Error(`GET /api/radio/selection → ${res.status}`);
  const raw = (await res.json()) as {
    preferred?: unknown;
    connected?: unknown;
    effective?: unknown;
    overrideDetection?: unknown;
  };
  return {
    preferred: normalizeBoard(raw.preferred),
    connected: normalizeBoard(raw.connected),
    effective: normalizeBoard(raw.effective),
    overrideDetection: typeof raw.overrideDetection === 'boolean' ? raw.overrideDetection : false,
  };
}

export async function updateRadioSelection(
  preferred: BoardKind,
  overrideDetection?: boolean,
  signal?: AbortSignal,
): Promise<RadioSelection> {
  const body: { preferred: string; overrideDetection?: boolean } = { preferred };
  if (overrideDetection !== undefined) {
    body.overrideDetection = overrideDetection;
  }
  const res = await fetch('/api/radio/selection', {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body),
    signal,
  });
  if (!res.ok) throw new Error(`PUT /api/radio/selection → ${res.status}`);
  const raw = (await res.json()) as {
    preferred?: unknown;
    connected?: unknown;
    effective?: unknown;
    overrideDetection?: unknown;
  };
  return {
    preferred: normalizeBoard(raw.preferred),
    connected: normalizeBoard(raw.connected),
    effective: normalizeBoard(raw.effective),
    overrideDetection: typeof raw.overrideDetection === 'boolean' ? raw.overrideDetection : false,
  };
}
