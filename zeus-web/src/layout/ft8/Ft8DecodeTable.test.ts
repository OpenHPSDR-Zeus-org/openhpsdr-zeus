// SPDX-License-Identifier: GPL-2.0-or-later
import { describe, expect, it } from 'vitest';
import { classifyDecode } from './Ft8DecodeTable';
import type { Ft8Row } from '../../state/ft8-store';

function row(text: string): Ft8Row {
  return {
    id: 'x',
    receiver: 0,
    protocol: 'FT8',
    slotStartUnixMs: 0,
    snrDb: -10,
    dtSec: 0.1,
    freqHz: 1234,
    score: 20,
    text,
  };
}

describe('classifyDecode', () => {
  it('flags CQ calls', () => {
    expect(classifyDecode(row('CQ RK9AX MO05'))).toBe('cq');
    // A CQ stays a CQ even if it is my own call CQing.
    expect(classifyDecode(row('CQ KB2UKA FN12'), 'KB2UKA')).toBe('cq');
  });

  it('flags messages directed at my call (call-to slot)', () => {
    expect(classifyDecode(row('KB2UKA RK9AX -12'), 'KB2UKA')).toBe('me');
    expect(classifyDecode(row('kb2uka rk9ax 73'), 'KB2UKA')).toBe('me'); // case-insensitive
  });

  it('does NOT flag me when I am only the caller (second slot)', () => {
    expect(classifyDecode(row('RK9AX KB2UKA RR73'), 'KB2UKA')).toBe('normal');
  });

  it('dims worked-before stations by caller', () => {
    const worked = new Set(['RK9AX']);
    expect(classifyDecode(row('GJ0KYZ RK9AX MO05'), undefined, worked)).toBe('worked');
    expect(classifyDecode(row('GJ0KYZ DL2XYZ JO62'), undefined, worked)).toBe('normal');
  });

  it('defaults to normal', () => {
    expect(classifyDecode(row('GJ0KYZ RK9AX MO05'))).toBe('normal');
  });
});
