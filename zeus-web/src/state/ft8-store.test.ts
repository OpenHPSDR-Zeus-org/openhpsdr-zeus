// SPDX-License-Identifier: GPL-2.0-or-later
import { beforeEach, describe, expect, it } from 'vitest';
import { useFt8Store, type Ft8DecodeBatch } from './ft8-store';

function batch(slotMs: number, texts: string[], receiver = 0): Ft8DecodeBatch {
  return {
    receiver,
    slotStartUnixMs: slotMs,
    protocol: 'FT8',
    decodes: texts.map((text, i) => ({
      snrDb: -10 + i,
      dtSec: 0.1,
      freqHz: 1000 + i * 50,
      score: 20 + i,
      text,
    })),
  };
}

describe('ft8-store ingest (0x38 decode frames)', () => {
  beforeEach(() => {
    useFt8Store.setState({ rows: [] });
  });

  it('flattens a batch into rows', () => {
    useFt8Store.getState().ingest(batch(1000, ['CQ KB2UKA FN12', 'K1JT FN20']));
    const rows = useFt8Store.getState().rows;
    expect(rows).toHaveLength(2);
    expect(rows.map((r) => r.text)).toContain('CQ KB2UKA FN12');
    expect(rows[0].slotStartUnixMs).toBe(1000);
    expect(rows[0].id).toMatch(/^0:1000:/);
  });

  it('puts the newest slot first', () => {
    useFt8Store.getState().ingest(batch(1000, ['old']));
    useFt8Store.getState().ingest(batch(2000, ['new']));
    expect(useFt8Store.getState().rows[0].text).toBe('new');
    expect(useFt8Store.getState().rows[1].text).toBe('old');
  });

  it('bounds the table to MAX_ROWS', () => {
    for (let s = 0; s < 60; s++) {
      const texts = Array.from({ length: 20 }, (_, i) => `msg-${s}-${i}`);
      useFt8Store.getState().ingest(batch(s * 15000, texts));
    }
    // 60 slots * 20 = 1200 ingested, capped at 500.
    expect(useFt8Store.getState().rows.length).toBe(500);
    // Newest slot's rows survived.
    expect(useFt8Store.getState().rows[0].text).toMatch(/^msg-59-/);
  });

  it('clear() empties the table', () => {
    useFt8Store.getState().ingest(batch(1000, ['a', 'b']));
    useFt8Store.getState().clear();
    expect(useFt8Store.getState().rows).toHaveLength(0);
  });
});
