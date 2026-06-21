// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

// Operator-selected variant for the 0x0A wire-byte alias family
// (issue #218 Phase 3). Mirrors Zeus.Contracts.OrionMkIIVariant. Default
// 'G2' preserves Zeus' shipping behaviour for every operator who never
// touches this setting — only consulted server-side when the connected
// board is OrionMkII.
export type OrionMkIIVariant =
  | 'G2'
  | 'G2_1K'
  | 'Anan7000DLE'
  | 'Anan8000DLE'
  | 'OrionMkII'
  | 'AnvelinaPro3'
  | 'RedPitaya';

export const ORION_MKII_VARIANT_LABELS: Record<OrionMkIIVariant, string> = {
  G2: 'ANAN-G2 / G2 MkII (Saturn)',
  G2_1K: 'ANAN-G2-1K (1 kW)',
  Anan7000DLE: 'ANAN-7000DLE',
  Anan8000DLE: 'ANAN-8000DLE',
  OrionMkII: 'Apache OrionMkII (original)',
  AnvelinaPro3: 'ANVELINA-PRO3',
  RedPitaya: 'Red Pitaya (OpenHPSDR)',
};

const ALL_VARIANTS: ReadonlyArray<OrionMkIIVariant> = [
  'G2',
  'G2_1K',
  'Anan7000DLE',
  'Anan8000DLE',
  'OrionMkII',
  'AnvelinaPro3',
  'RedPitaya',
];

export function isOrionMkIIVariant(v: unknown): v is OrionMkIIVariant {
  return typeof v === 'string' && ALL_VARIANTS.includes(v as OrionMkIIVariant);
}

export function normalizeOrionMkIIVariant(v: unknown): OrionMkIIVariant {
  return isOrionMkIIVariant(v) ? v : 'G2';
}

export async function fetchOrionMkIIVariant(signal?: AbortSignal): Promise<OrionMkIIVariant> {
  const res = await fetch('/api/radio/variant', { signal });
  if (!res.ok) throw new Error(`GET /api/radio/variant → ${res.status}`);
  const raw = (await res.json()) as { variant?: unknown };
  return normalizeOrionMkIIVariant(raw.variant);
}

export async function setOrionMkIIVariant(
  variant: OrionMkIIVariant,
  signal?: AbortSignal,
): Promise<OrionMkIIVariant> {
  const res = await fetch('/api/radio/variant', {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ variant }),
    signal,
  });
  if (!res.ok) throw new Error(`PUT /api/radio/variant → ${res.status}`);
  const raw = (await res.json()) as { variant?: unknown };
  return normalizeOrionMkIIVariant(raw.variant);
}
