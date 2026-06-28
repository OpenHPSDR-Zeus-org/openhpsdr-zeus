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

  it('lights a new (unworked) grid on a directed decode', () => {
    const grids = new Set(['FN42']);
    // MO05 not yet worked → new; FN42 already worked → normal.
    expect(classifyDecode(row('GJ0KYZ RK9AX MO05'), undefined, undefined, grids)).toBe('new');
    expect(classifyDecode(row('GJ0KYZ RK9AX FN42'), undefined, undefined, grids)).toBe('normal');
  });

  it('keeps CQ green even when its grid is new', () => {
    const grids = new Set<string>();
    expect(classifyDecode(row('CQ RK9AX MO05'), undefined, undefined, grids)).toBe('cq');
  });

  it('prefers worked-before over new-grid', () => {
    const worked = new Set(['RK9AX']);
    const grids = new Set<string>();
    expect(classifyDecode(row('GJ0KYZ RK9AX MO05'), undefined, worked, grids)).toBe('worked');
  });

  it('returns normal for a directed decode with no grid', () => {
    const grids = new Set<string>();
    expect(classifyDecode(row('GJ0KYZ RK9AX -12'), undefined, undefined, grids)).toBe('normal');
  });
});
