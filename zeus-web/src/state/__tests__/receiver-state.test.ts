// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';
import { KIWI_RECEIVER_INDEX, MAX_HARDWARE_RECEIVERS, receiverLabel } from '../receiver-state';

describe('receiver constants', () => {
  it('keeps Kiwi outside the ten hardware receiver slots', () => {
    expect(MAX_HARDWARE_RECEIVERS).toBe(10);
    expect(KIWI_RECEIVER_INDEX).toBe(MAX_HARDWARE_RECEIVERS);
  });
});

describe('receiverLabel', () => {
  it('falls back to a 1-based RX{n} label when name is null', () => {
    expect(receiverLabel({ index: 0, name: null })).toBe('RX1');
    expect(receiverLabel({ index: 1, name: null })).toBe('RX2');
    expect(receiverLabel({ index: 5, name: null })).toBe('RX6');
  });

  it('falls back to RX{n} when name is omitted', () => {
    expect(receiverLabel({ index: 2 })).toBe('RX3');
  });

  it('prefers an explicit name over the index label', () => {
    expect(receiverLabel({ index: KIWI_RECEIVER_INDEX, name: 'Kiwi' })).toBe('Kiwi');
  });

  it('uses an empty string name as-is rather than the fallback', () => {
    // `name ?? fallback` only falls back on null/undefined, so a non-null name
    // (even empty) wins — documents the nullish-coalescing contract.
    expect(receiverLabel({ index: 3, name: '' })).toBe('');
  });
});
