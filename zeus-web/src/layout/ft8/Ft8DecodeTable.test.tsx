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

function row(text: string, i: number): Ft8Row {
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
  };
}

const ROWS: Ft8Row[] = [
  row('CQ K1ABC FN42', 0), // cq
  row('K7XYZ K9QQQ FN31', 1), // normal (neither CQ, me, nor worked)
  row('MYCALL K3ZZZ -05', 2), // me (directed at MYCALL)
  row('K5AAA W1AW R-12', 3), // worked (sender W1AW is in the logbook)
];

function bodyRowCount(container: HTMLElement): number {
  return container.querySelectorAll('tbody tr').length;
}

describe('Ft8DecodeTable filters', () => {
  beforeEach(() => {
    act(() => {
      useFt8Store.setState({ rows: ROWS });
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

  it('Hide-worked-before drops rows whose sender is already logged', () => {
    const { container, unmount } = render(
      createElement(Ft8DecodeTable, {
        myCall: 'MYCALL',
        hideWorkedBefore: true,
        workedCalls: new Set(['W1AW']),
      }),
    );
    expect(bodyRowCount(container)).toBe(3); // the W1AW row is hidden
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
});
