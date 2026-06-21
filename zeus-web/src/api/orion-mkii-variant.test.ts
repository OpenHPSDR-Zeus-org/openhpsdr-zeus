// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

import { describe, expect, it } from 'vitest';

import {
  isOrionMkIIVariant,
  normalizeOrionMkIIVariant,
  ORION_MKII_VARIANT_LABELS,
} from './orion-mkii-variant';

describe('isOrionMkIIVariant', () => {
  it('accepts every defined variant', () => {
    for (const v of Object.keys(ORION_MKII_VARIANT_LABELS)) {
      expect(isOrionMkIIVariant(v)).toBe(true);
    }
  });

  it('rejects unknown strings and non-strings', () => {
    expect(isOrionMkIIVariant('SomethingElse')).toBe(false);
    expect(isOrionMkIIVariant(42)).toBe(false);
    expect(isOrionMkIIVariant(null)).toBe(false);
    expect(isOrionMkIIVariant(undefined)).toBe(false);
  });
});

describe('normalizeOrionMkIIVariant', () => {
  it('returns G2 for unknown input — preserves shipping default', () => {
    expect(normalizeOrionMkIIVariant('garbage')).toBe('G2');
    expect(normalizeOrionMkIIVariant(undefined)).toBe('G2');
  });

  it('passes through every defined variant', () => {
    expect(normalizeOrionMkIIVariant('Anan8000DLE')).toBe('Anan8000DLE');
    expect(normalizeOrionMkIIVariant('G2_1K')).toBe('G2_1K');
  });
});
