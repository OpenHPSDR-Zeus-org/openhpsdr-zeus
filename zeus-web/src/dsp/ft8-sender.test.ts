// SPDX-License-Identifier: GPL-2.0-or-later
//
// ft8-sender tests — MIRROR the backend Ft8MessageParseTests vectors 1:1 so
// the render-time worked-before highlight extracts the same senders the server
// used to flag before the digital suite moved into the Zeus Digital plugin.
// Do not add divergent acceptance rules here; change the vectors in lockstep
// with the plugin-side C# tests or the highlight silently drifts.

import { describe, expect, it } from 'vitest';
import { tryParseSender } from './ft8-sender';

describe('tryParseSender (Ft8MessageParse.TryParseSender parity)', () => {
  it('CQ with grid', () => {
    expect(tryParseSender('CQ K1ABC FN42')).toEqual({ call: 'K1ABC', grid: 'FN42' });
  });

  it('CQ DX directive', () => {
    expect(tryParseSender('CQ DX G0XYZ IO91')).toEqual({ call: 'G0XYZ', grid: 'IO91' });
  });

  it('CQ POTA directive', () => {
    expect(tryParseSender('CQ POTA W9XYZ EN52')).toEqual({ call: 'W9XYZ', grid: 'EN52' });
  });

  it('CQ without grid', () => {
    expect(tryParseSender('CQ W9XYZ')).toEqual({ call: 'W9XYZ', grid: null });
  });

  it('directed Tx1 grid reply — sender is the DE call, not the target', () => {
    expect(tryParseSender('K1ABC G0XYZ IO91')).toEqual({ call: 'G0XYZ', grid: 'IO91' });
  });

  it('directed report — no grid', () => {
    expect(tryParseSender('K1ABC G0XYZ -12')).toEqual({ call: 'G0XYZ', grid: null });
  });

  it('directed R-report — no grid', () => {
    expect(tryParseSender('K1ABC G0XYZ R+05')).toEqual({ call: 'G0XYZ', grid: null });
  });

  it('RR73 is not a grid (matches the grid regex but must be excluded)', () => {
    expect(tryParseSender('K1ABC G0XYZ RR73')).toEqual({ call: 'G0XYZ', grid: null });
  });

  it('RRR and 73 — sender extracted, no grid', () => {
    expect(tryParseSender('K1ABC G0XYZ 73')).toEqual({ call: 'G0XYZ', grid: null });
    expect(tryParseSender('K1ABC G0XYZ RRR')).toEqual({ call: 'G0XYZ', grid: null });
  });

  it('rejects a purely-hashed sender', () => {
    expect(tryParseSender('K1ABC <...> RR73')).toBeNull();
  });

  it('accepts a hashed target with a real sender', () => {
    expect(tryParseSender('<...> G0XYZ IO91')).toEqual({ call: 'G0XYZ', grid: 'IO91' });
  });

  it('accepts a hashed sender with a real call inside', () => {
    expect(tryParseSender('K1ABC <PJ4/K1ABC> +03')).toEqual({ call: 'PJ4/K1ABC', grid: null });
  });

  it('normalizes lowercase', () => {
    expect(tryParseSender('cq k1abc fn42')).toEqual({ call: 'K1ABC', grid: 'FN42' });
  });

  it.each(['', '   ', 'HELLO WORLD', 'TNX FER QSO'])('free text returns null (%j)', (text) => {
    expect(tryParseSender(text)).toBeNull();
  });

  it('null/undefined return null', () => {
    expect(tryParseSender(null)).toBeNull();
    expect(tryParseSender(undefined)).toBeNull();
  });

  it.each([
    'K1ABC FN42', // 2-token free text: token[1] is a grid, not a call
    'G0XYZ FN42 73', // 3-token free text: token[1] is a grid, not a call
    'CQ FN42', // CQ followed by a grid where the call should be
  ])('rejects a grid-like sender token (%s)', (text) => {
    // A bare 4-char Maidenhead locator satisfies the loose letter+digit core
    // check but must never be reported as a transmitting callsign.
    expect(tryParseSender(text)).toBeNull();
  });

  it('accepts a portable call with a slash', () => {
    expect(tryParseSender('CQ K1ABC/P FN42')).toEqual({ call: 'K1ABC/P', grid: 'FN42' });
  });
});
