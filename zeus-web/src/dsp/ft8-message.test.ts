// SPDX-License-Identifier: GPL-2.0-or-later
import { describe, expect, it } from 'vitest';
import { parseFt8Message } from './ft8-message';

describe('parseFt8Message', () => {
  it('parses a plain CQ with grid', () => {
    const m = parseFt8Message('CQ K1ABC FN42');
    expect(m.kind).toBe('cq');
    expect(m.deCall).toBe('K1ABC');
    expect(m.grid).toBe('FN42');
    expect(m.cqDirective).toBeNull();
    expect(m.targetCall).toBeNull();
  });

  it('parses CQ DX with directive', () => {
    const m = parseFt8Message('CQ DX G0XYZ IO91');
    expect(m.kind).toBe('cq');
    expect(m.cqDirective).toBe('DX');
    expect(m.deCall).toBe('G0XYZ');
    expect(m.grid).toBe('IO91');
  });

  it('parses a directed CQ (contest directive)', () => {
    const m = parseFt8Message('CQ TEST K1ABC FN42');
    expect(m.kind).toBe('cq');
    expect(m.cqDirective).toBe('TEST');
    expect(m.deCall).toBe('K1ABC');
  });

  it('parses CQ without a grid', () => {
    const m = parseFt8Message('CQ W9XYZ');
    expect(m.kind).toBe('cq');
    expect(m.deCall).toBe('W9XYZ');
    expect(m.grid).toBeNull();
  });

  it('parses Tx1 grid reply', () => {
    const m = parseFt8Message('K1ABC G0XYZ IO91');
    expect(m.kind).toBe('grid');
    expect(m.targetCall).toBe('K1ABC');
    expect(m.deCall).toBe('G0XYZ');
    expect(m.grid).toBe('IO91');
  });

  it('parses a negative signal report', () => {
    const m = parseFt8Message('G0XYZ K1ABC -19');
    expect(m.kind).toBe('report');
    expect(m.reportDb).toBe(-19);
    expect(m.targetCall).toBe('G0XYZ');
    expect(m.deCall).toBe('K1ABC');
  });

  it('parses a positive signal report', () => {
    const m = parseFt8Message('W9XYZ PJ4A +03');
    expect(m.kind).toBe('report');
    expect(m.reportDb).toBe(3);
  });

  it('parses a roger-report', () => {
    const m = parseFt8Message('K1ABC G0XYZ R-22');
    expect(m.kind).toBe('rreport');
    expect(m.reportDb).toBe(-22);
  });

  it('parses RR73 and does not confuse it with a grid', () => {
    const m = parseFt8Message('K1ABC G0XYZ RR73');
    expect(m.kind).toBe('rr73');
    expect(m.grid).toBeNull();
  });

  it('parses RRR', () => {
    expect(parseFt8Message('G0XYZ K1ABC RRR').kind).toBe('rrr');
  });

  it('parses 73', () => {
    expect(parseFt8Message('K1ABC G0XYZ 73').kind).toBe('73');
  });

  it('flags messages directed at my call', () => {
    const m = parseFt8Message('G0XYZ K1ABC -19', 'g0xyz');
    expect(m.isCallingMe).toBe(true);
  });

  it('does not flag messages directed at someone else', () => {
    const m = parseFt8Message('K1ABC G0XYZ -19', 'g0xyz');
    expect(m.isCallingMe).toBe(false);
  });

  it('does not flag a CQ as calling me', () => {
    const m = parseFt8Message('CQ K1ABC FN42', 'g0xyz');
    expect(m.isCallingMe).toBe(false);
  });

  it('handles hashed/nonstandard callsigns in angle brackets', () => {
    const m = parseFt8Message('<PJ4/K1ABC> W9XYZ +03');
    expect(m.kind).toBe('report');
    expect(m.targetCall).toBe('PJ4/K1ABC');
    expect(m.deCall).toBe('W9XYZ');
  });

  it('classifies free text as free', () => {
    const m = parseFt8Message('K1ABC G0XYZ TNX BOB');
    expect(m.kind).toBe('free');
    expect(m.targetCall).toBe('K1ABC');
  });

  it('never throws on empty or junk input', () => {
    expect(parseFt8Message('').kind).toBe('free');
    expect(parseFt8Message('   ').kind).toBe('free');
    expect(parseFt8Message('????').kind).toBe('free');
  });
});
