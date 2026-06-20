// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';
import type { LogEntry } from '../../api/log';
import { filterLogEntries, isQrzPublished, logEntrySearchText } from './logbook-search';

function entry(overrides: Partial<LogEntry>): LogEntry {
  return {
    id: 'base',
    qsoDateTimeUtc: '2026-06-19T14:22:00Z',
    callsign: 'N0CALL',
    name: null,
    frequencyMhz: 14.074,
    band: '20m',
    mode: 'FT8',
    rstSent: '599',
    rstRcvd: '579',
    grid: null,
    country: null,
    dxcc: null,
    cqZone: null,
    ituZone: null,
    state: null,
    comment: null,
    createdUtc: '2026-06-19T14:23:00Z',
    qrzLogId: null,
    qrzUploadedUtc: null,
    ...overrides,
  };
}

describe('logbook search', () => {
  const entries = [
    entry({
      id: 'a',
      callsign: 'N9WAR',
      name: 'Christian',
      frequencyMhz: 7.185,
      band: '40m',
      mode: 'LSB',
      grid: 'EN61',
      country: 'United States',
      state: 'IL',
      comment: 'net control',
      qrzLogId: 'QRZ-123',
    }),
    entry({
      id: 'b',
      callsign: 'EI6LF',
      name: 'Brian',
      frequencyMhz: 14.250,
      band: '20m',
      mode: 'USB',
      grid: 'IO63',
      country: 'Ireland',
      comment: 'long path',
    }),
  ];

  it('matches callsigns case-insensitively', () => {
    expect(filterLogEntries(entries, 'n9war').map((qso) => qso.id)).toEqual(['a']);
  });

  it('matches multiple terms across displayed fields', () => {
    expect(filterLogEntries(entries, '20m brian usb').map((qso) => qso.id)).toEqual(['b']);
  });

  it('matches location, comments, frequency, and QRZ sync text', () => {
    expect(filterLogEntries(entries, '7.185 net QRZ-123').map((qso) => qso.id)).toEqual(['a']);
  });

  it('returns all entries for blank queries', () => {
    expect(filterLogEntries(entries, '   ')).toBe(entries);
  });

  it('includes UTC date and time in the searchable text', () => {
    expect(logEntrySearchText(entries[0]!)).toContain('14:22');
    expect(filterLogEntries(entries, '2026 14:22').map((qso) => qso.id)).toEqual(['a', 'b']);
  });

  it('handles imported entries without a frequency', () => {
    const imported = entry({ id: 'imported', callsign: 'K1ABC', frequencyMhz: null, band: '20M', mode: 'SSB' });

    expect(logEntrySearchText(imported)).not.toContain('null');
    expect(filterLogEntries([imported], 'k1abc 20m ssb').map((qso) => qso.id)).toEqual(['imported']);
  });

  it('can hide QRZ-published entries', () => {
    expect(isQrzPublished(entries[0]!)).toBe(true);
    expect(filterLogEntries(entries, ' ', { hideQrzPublished: true }).map((qso) => qso.id)).toEqual(['b']);
  });

  it('composes QRZ-published hiding with text search', () => {
    expect(filterLogEntries(entries, '20m', { hideQrzPublished: true }).map((qso) => qso.id)).toEqual(['b']);
    expect(filterLogEntries(entries, '40m', { hideQrzPublished: true })).toEqual([]);
  });
});
