// SPDX-License-Identifier: GPL-2.0-or-later
//
// Decode-table client-side filter tests (FT8 Settings → Decode): Show-only-CQ
// and Hide-worked-before. Import the harness first so its localStorage polyfill
// and act-environment flag are installed before any store module loads.

import { describe, expect, it, beforeEach } from 'vitest';
import { createElement } from 'react';
import { act, render } from '../../components/meters/__tests__/harness';
import { Ft8DecodeTable } from './Ft8DecodeTable';
import { useFt8Store, type Ft8Row } from '../../state/ft8-store';
import { useDigitalWorkedStore } from '../../state/digital-worked-store';

function row(text: string, i: number, extra?: Partial<Ft8Row>): Ft8Row {
  return {
    id: `r${i}`,
    receiver: 0,
    protocol: 'FT8',
    slotStartUnixMs: 0,
    snrDb: -10,
    dtSec: 0.1,
    freqHz: 1000 + i,
    score: 0,
    text,
    ...extra,
  };
}

const ROWS: Ft8Row[] = [
  row('CQ K1ABC FN42', 0), // cq
  row('K7XYZ K9QQQ FN31', 1), // normal (neither CQ, me, nor worked)
  row('MYCALL K3ZZZ -05', 2), // me (directed at MYCALL)
  row('K5AAA W1AW R-12', 3, { workedBefore: true }), // worked (server flag)
];

function bodyRowCount(container: HTMLElement): number {
  return container.querySelectorAll('tbody tr').length;
}

describe('Ft8DecodeTable filters', () => {
  beforeEach(() => {
    act(() => {
      useFt8Store.setState({ rows: ROWS });
      useDigitalWorkedStore.setState({ calls: new Set<string>(), loaded: false });
    });
  });

  it('shows every row with no filter active', () => {
    const { container, unmount } = render(
      createElement(Ft8DecodeTable, { myCall: 'MYCALL' }),
    );
    expect(bodyRowCount(container)).toBe(4);
    unmount();
  });

  it('Show-only-CQ keeps CQ rows and rows calling me', () => {
    const { container, unmount } = render(
      createElement(Ft8DecodeTable, { myCall: 'MYCALL', showOnlyCq: true }),
    );
    // The CQ row + the row directed at MYCALL survive; the other two drop.
    expect(bodyRowCount(container)).toBe(2);
    unmount();
  });

  it('Hide-worked-before drops rows the server flagged worked', () => {
    const { container, unmount } = render(
      createElement(Ft8DecodeTable, {
        myCall: 'MYCALL',
        hideWorkedBefore: true,
      }),
    );
    expect(bodyRowCount(container)).toBe(3); // the workedBefore W1AW row is hidden
    unmount();
  });

  it('decorates worked-before at render time from digital-worked-store', () => {
    act(() => {
      // K9QQQ is the sender of the 'K7XYZ K9QQQ FN31' row — once the worked
      // set lands, that row lights up WITHOUT re-ingesting anything.
      useDigitalWorkedStore.setState({ calls: new Set(['K9QQQ']), loaded: true });
    });
    const { container, unmount } = render(
      createElement(Ft8DecodeTable, { myCall: 'MYCALL' }),
    );
    const worked = container.querySelectorAll('tbody tr.ft8-row--worked');
    // The set-decorated K9QQQ row + the legacy-flagged W1AW row.
    expect(worked.length).toBe(2);
    unmount();
  });

  it('Hide-worked-before also drops rows decorated from the worked set', () => {
    act(() => {
      useDigitalWorkedStore.setState({ calls: new Set(['K9QQQ']), loaded: true });
    });
    const { container, unmount } = render(
      createElement(Ft8DecodeTable, { myCall: 'MYCALL', hideWorkedBefore: true }),
    );
    // 4 rows − legacy-flag W1AW − set-decorated K9QQQ = 2.
    expect(bodyRowCount(container)).toBe(2);
    unmount();
  });

  it('renders an abbreviated Country column to the right of Message', () => {
    act(() => {
      useFt8Store.setState({
        rows: [row('CQ DL1ABC JO31', 0, { country: 'GER' })],
      });
    });
    const { container, unmount } = render(createElement(Ft8DecodeTable, {}));
    // Header has a Country column after Message.
    const headers = Array.from(container.querySelectorAll('thead th')).map(
      (th) => th.textContent,
    );
    expect(headers).toEqual(['UTC', 'dB', 'DT', 'Freq', 'Message', 'Country']);
    // The decode row carries the resolved country in its country cell.
    const countryCell = container.querySelector('tbody tr td.ft8-country');
    expect(countryCell?.textContent).toBe('GER');
    unmount();
  });

  it('leaves the Country cell blank when the decode has no country', () => {
    act(() => {
      useFt8Store.setState({ rows: [row('CQ N0XYZ', 0)] });
    });
    const { container, unmount } = render(createElement(Ft8DecodeTable, {}));
    const countryCell = container.querySelector('tbody tr td.ft8-country');
    expect(countryCell?.textContent).toBe('');
    unmount();
  });

  it('shows a filter-specific empty message when nothing matches', () => {
    act(() => {
      useFt8Store.setState({ rows: [row('K7XYZ K9QQQ FN31', 9)] });
    });
    const { container, unmount } = render(
      createElement(Ft8DecodeTable, { myCall: 'MYCALL', showOnlyCq: true }),
    );
    expect(bodyRowCount(container)).toBe(0);
    expect(container.textContent).toContain('No decodes match');
    unmount();
  });

  it('interleaves our own TX echoes as distinct, non-clickable rows', () => {
    const txEchoes = [
      {
        id: 'tx:1',
        timeUtcMs: 10, // newer than the received rows (slotStartUnixMs 0)
        message: 'CQ MYCALL FN30',
        mode: 'FT8',
        slot: 'even',
        audioHz: 1200,
      },
    ];
    const { container, unmount } = render(
      createElement(Ft8DecodeTable, { myCall: 'MYCALL', txEchoes }),
    );
    // 4 received rows + 1 TX echo row.
    expect(bodyRowCount(container)).toBe(5);
    const txRow = container.querySelector('tbody tr.ft8-row--tx');
    expect(txRow).not.toBeNull();
    expect(txRow?.textContent).toContain('CQ MYCALL FN30');
    expect(txRow?.querySelector('.ft8-row__tx-badge')?.textContent).toBe('TX');
    // Newest-first: the TX echo (t=10) sorts above the received rows (t=0).
    expect(container.querySelector('tbody tr')?.classList.contains('ft8-row--tx')).toBe(true);
    unmount();
  });

  it('renders TX echoes even when no decodes have arrived', () => {
    act(() => {
      useFt8Store.setState({ rows: [] });
    });
    const txEchoes = [
      { id: 'tx:1', timeUtcMs: 5, message: 'CQ MYCALL FN30', mode: 'FT8', slot: 'odd', audioHz: 800 },
    ];
    const { container, unmount } = render(createElement(Ft8DecodeTable, { txEchoes }));
    expect(bodyRowCount(container)).toBe(1);
    expect(container.textContent).not.toContain('Waiting for decodes');
    unmount();
  });
});
