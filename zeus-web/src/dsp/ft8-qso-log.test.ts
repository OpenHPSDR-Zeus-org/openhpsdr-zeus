// SPDX-License-Identifier: GPL-2.0-or-later
import { describe, expect, it } from 'vitest';
import { computeFt8Stats, qsoStateToLogEntry } from './ft8-qso-log';
import { startCq, type QsoState } from './ft8-sequencer';
import type { LogEntry } from '../api/log';

function qso(overrides: Partial<QsoState> = {}): QsoState {
  return {
    ...startCq({ myCall: 'KB2UKA', myGrid4: 'FN42', mode: 'FT8' }),
    dxCall: 'K1ABC',
    dxGrid4: 'FN31',
    sentReportToHim: -12,
    rcvdReportFromHim: -7,
    progress: 'done',
    ...overrides,
  };
}

const CTX = { band: '20m', freqMhz: 14.074, mode: 'FT8' as const };

describe('qsoStateToLogEntry', () => {
  it('maps a completed QSO into a create request', () => {
    const now = new Date('2026-06-26T12:00:00.000Z');
    const req = qsoStateToLogEntry(qso(), CTX, now);
    expect(req).toEqual({
      callsign: 'K1ABC',
      frequencyMhz: 14.074,
      band: '20M', // uppercased for ADIF/QRZ
      mode: 'FT8',
      rstSent: '-12',
      rstRcvd: '-07',
      grid: 'FN31',
      qsoDateTimeUtc: '2026-06-26T12:00:00.000Z',
    });
  });

  it('formats positive reports with a sign and 2 digits', () => {
    const req = qsoStateToLogEntry(qso({ sentReportToHim: 3, rcvdReportFromHim: 0 }), CTX);
    expect(req?.rstSent).toBe('+03');
    expect(req?.rstRcvd).toBe('+00');
  });

  it('returns null when there is no DX call to log', () => {
    expect(qsoStateToLogEntry(qso({ dxCall: null }), CTX)).toBeNull();
  });

  it('leaves RST blank when a report was never exchanged', () => {
    const req = qsoStateToLogEntry(qso({ sentReportToHim: null, rcvdReportFromHim: null }), CTX);
    expect(req?.rstSent).toBe('');
    expect(req?.rstRcvd).toBe('');
  });

  it('carries FT4 mode through', () => {
    const req = qsoStateToLogEntry(qso(), { ...CTX, mode: 'FT4' });
    expect(req?.mode).toBe('FT4');
  });
});

function entry(overrides: Partial<LogEntry> = {}): LogEntry {
  return {
    id: Math.random().toString(36),
    qsoDateTimeUtc: '2026-06-26T12:00:00.000Z',
    callsign: 'K1ABC',
    name: null,
    frequencyMhz: 14.074,
    band: '20M',
    mode: 'FT8',
    rstSent: '-12',
    rstRcvd: '-07',
    grid: 'FN31',
    country: null,
    dxcc: null,
    cqZone: null,
    ituZone: null,
    state: null,
    comment: null,
    createdUtc: '2026-06-26T12:00:00.000Z',
    qrzLogId: null,
    qrzUploadedUtc: null,
    ...overrides,
  };
}

describe('computeFt8Stats', () => {
  const now = new Date('2026-06-26T18:00:00.000Z');

  it('counts QSOs today by UTC day', () => {
    const stats = computeFt8Stats(
      [
        entry({ qsoDateTimeUtc: '2026-06-26T01:00:00.000Z' }),
        entry({ qsoDateTimeUtc: '2026-06-25T23:00:00.000Z' }),
      ],
      'FN42',
      now,
    );
    expect(stats.qsosToday).toBe(1);
    expect(stats.qsosTotal).toBe(2);
  });

  it('counts confirmed (QRZ-uploaded) and distinct grids/dxcc', () => {
    const stats = computeFt8Stats(
      [
        entry({ grid: 'FN31', dxcc: 291, qrzUploadedUtc: '2026-06-26T13:00:00.000Z' }),
        entry({ grid: 'FN31ab', dxcc: 291 }), // same 4-char grid + dxcc
        entry({ grid: 'IO91', dxcc: 223 }),
      ],
      'FN42',
      now,
    );
    expect(stats.confirmed).toBe(1);
    expect(stats.distinctGrids).toBe(2);
    expect(stats.distinctDxcc).toBe(2);
  });

  it('averages received SNR and finds best DX from the operator grid', () => {
    const stats = computeFt8Stats(
      [
        entry({ callsign: 'K1ABC', grid: 'FN31', rstRcvd: '-10' }),
        entry({ callsign: 'JA1XYZ', grid: 'PM95', rstRcvd: '-20' }),
      ],
      'FN42',
      now,
    );
    expect(stats.avgSnrRx).toBe(-15);
    expect(stats.bestDx?.callsign).toBe('JA1XYZ'); // Japan farther than FN31
    expect(stats.bestDx!.km).toBeGreaterThan(5000);
  });

  it('excludes phone RST (e.g. 59) from the SNR average', () => {
    const stats = computeFt8Stats(
      [
        entry({ mode: 'FT8', rstRcvd: '-10' }),
        entry({ mode: 'SSB', rstRcvd: '59' }), // RST, not a dB SNR
      ],
      null,
      now,
    );
    expect(stats.avgSnrRx).toBe(-10);
  });

  it('returns null best DX / avg SNR when data is missing', () => {
    const stats = computeFt8Stats([entry({ grid: null, rstRcvd: '' })], null, now);
    expect(stats.bestDx).toBeNull();
    expect(stats.avgSnrRx).toBeNull();
  });
});
