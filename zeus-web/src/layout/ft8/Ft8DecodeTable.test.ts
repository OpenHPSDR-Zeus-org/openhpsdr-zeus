// SPDX-License-Identifier: GPL-2.0-or-later
import { describe, expect, it } from 'vitest';
import { classifyDecode } from './Ft8DecodeTable';
import type { Ft8Row } from '../../state/ft8-store';

function row(text: string, extra?: Partial<Ft8Row>): Ft8Row {
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
    ...extra,
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

  it('flags worked-before from the authoritative server row flag', () => {
    expect(classifyDecode(row('GJ0KYZ RK9AX MO05', { workedBefore: true }))).toBe('worked');
    // Same message WITHOUT the flag is not worked — the old client-side
    // workedCalls heuristic is gone.
    expect(classifyDecode(row('GJ0KYZ RK9AX MO05', { workedBefore: false }))).toBe('normal');
    expect(classifyDecode(row('GJ0KYZ RK9AX MO05'))).toBe('normal');
  });

  it('defaults to normal', () => {
    expect(classifyDecode(row('GJ0KYZ RK9AX MO05'))).toBe('normal');
  });

  it('lights a new (unworked) grid on a directed decode', () => {
    const grids = new Set(['FN42']);
    // MO05 not yet worked → new; FN42 already worked → normal.
    expect(classifyDecode(row('GJ0KYZ RK9AX MO05'), undefined, grids)).toBe('new');
    expect(classifyDecode(row('GJ0KYZ RK9AX FN42'), undefined, grids)).toBe('normal');
  });

  it('keeps CQ green even when its grid is new', () => {
    const grids = new Set<string>();
    expect(classifyDecode(row('CQ RK9AX MO05'), undefined, grids)).toBe('cq');
  });

  it('prefers worked-before over new-grid', () => {
    const grids = new Set<string>();
    expect(classifyDecode(row('GJ0KYZ RK9AX MO05', { workedBefore: true }), undefined, grids)).toBe(
      'worked',
    );
  });

  it('prefers calling-me over worked-before', () => {
    // I am being called AND the sender is worked-before → 'me' wins.
    expect(
      classifyDecode(row('KB2UKA RK9AX -12', { workedBefore: true }), 'KB2UKA'),
    ).toBe('me');
  });

  it('keeps CQ above worked-before', () => {
    expect(classifyDecode(row('CQ RK9AX MO05', { workedBefore: true }))).toBe('cq');
  });

  it('returns normal for a directed decode with no grid', () => {
    const grids = new Set<string>();
    expect(classifyDecode(row('GJ0KYZ RK9AX -12'), undefined, grids)).toBe('normal');
  });
});
