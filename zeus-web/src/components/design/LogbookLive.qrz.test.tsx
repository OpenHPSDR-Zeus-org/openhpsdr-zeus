// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// "On QRZ" logbook indicator: any QSO already on QRZ.com — uploaded by Zeus or
// imported as already-sent (ADIF APP_QRZLOG_LOGID) — gets a translucent green
// row tint + the ✓ QRZ pill, with a footer legend. Both markers count
// (qrzLogId OR qrzUploadedUtc).

import { describe, expect, it, beforeEach } from 'vitest';
import { createElement } from 'react';
import { act, render } from '../meters/__tests__/harness';
import { LogbookLive, isQrzPublished } from './LogbookLive';
import { useLoggerStore } from '../../state/logger-store';
import type { LogEntry } from '../../api/log';

function entry(over: Partial<LogEntry> & Pick<LogEntry, 'id' | 'callsign'>): LogEntry {
  return {
    qsoDateTimeUtc: '2026-05-24T12:00:00Z',
    name: null,
    frequencyMhz: 14.074,
    band: '20m',
    mode: 'FT8',
    rstSent: '-10',
    rstRcvd: '-12',
    grid: 'FN31',
    country: null,
    dxcc: null,
    cqZone: null,
    ituZone: null,
    state: null,
    comment: null,
    createdUtc: '2026-05-24T12:00:05Z',
    qrzLogId: null,
    qrzUploadedUtc: null,
    ...over,
  };
}

describe('isQrzPublished', () => {
  it('is true when uploaded by Zeus (qrzLogId set)', () => {
    expect(isQrzPublished(entry({ id: '1', callsign: 'K1ABC', qrzLogId: 'ABC123' }))).toBe(true);
  });
  it('is true when only the upload timestamp is set (defensive)', () => {
    expect(
      isQrzPublished(entry({ id: '2', callsign: 'K2DEF', qrzUploadedUtc: '2026-05-24T12:01:00Z' })),
    ).toBe(true);
  });
  it('is false for an un-uploaded QSO', () => {
    expect(isQrzPublished(entry({ id: '3', callsign: 'K3GHI' }))).toBe(false);
  });
  it('treats an empty qrzLogId as not published', () => {
    expect(isQrzPublished(entry({ id: '4', callsign: 'K4JKL', qrzLogId: '' }))).toBe(false);
  });
});

describe('LogbookLive — On QRZ row highlight + legend', () => {
  beforeEach(() => {
    act(() => {
      useLoggerStore.setState({
        entries: [
          entry({ id: 'pub', callsign: 'W1PUB', qrzLogId: 'QRZ-1' }),
          entry({ id: 'unp', callsign: 'W2UNP' }),
        ],
        totalCount: 2,
        loading: false,
      });
    });
  });

  it('tints only the QRZ-published row and shows its pill', () => {
    const { container, unmount } = render(
      createElement(LogbookLive, { searchText: '', hideQrzPublished: false }),
    );
    const tinted = container.querySelectorAll('.log-row--qrz');
    expect(tinted.length).toBe(1);
    // The tinted row is the published one and carries the ✓ QRZ pill.
    expect(tinted[0]?.textContent).toContain('W1PUB');
    expect(tinted[0]?.querySelector('.log-sync-pill')).not.toBeNull();
    // The un-published row is neither tinted nor pilled.
    const pills = container.querySelectorAll('.log-sync-pill');
    expect(pills.length).toBe(1);
    unmount();
  });

  it('renders the "On QRZ" footer legend key', () => {
    const { container, unmount } = render(
      createElement(LogbookLive, { searchText: '', hideQrzPublished: false }),
    );
    const legend = container.querySelector('.log-legend');
    expect(legend).not.toBeNull();
    expect(legend?.textContent).toContain('On QRZ');
    expect(legend?.querySelector('.log-legend__sw')).not.toBeNull();
    unmount();
  });
});
