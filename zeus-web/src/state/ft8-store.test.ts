// SPDX-License-Identifier: GPL-2.0-or-later
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useFt8Store, type Ft8DecodeBatch } from './ft8-store';
import { configureRadioForDigital, restoreRadioWhenIdle, snapshotRadio } from './digital-mode';
import { useConnectionStore } from './connection-store';

// The pre-engage radio config snapshotRadio() returns at workspace entry. The
// integration test below proves an in-FT8 band change never overwrites it, so
// exit restores THIS (band-A) snapshot — the heart of BUG 2.
const PRIOR_SNAPSHOT = {
  mode: 'USB' as const,
  filterLowHz: -2700,
  filterHighHz: -300,
  vfoHz: 14_200_000, // band A (20 m phone) — the operator's pre-FT8 dial
  ctunEnabled: false,
  radioLoHz: 14_200_000,
  zoomLevel: 1,
};

vi.mock('./digital-mode', () => ({
  // Radio mutation is exercised in digital-mode.test.ts; here we only assert the
  // store hands the ORIGINAL snapshot to the restore on exit.
  configureRadioForDigital: vi.fn(async () => {}),
  restoreRadioWhenIdle: vi.fn(),
  snapshotRadio: vi.fn(() => ({ ...PRIOR_SNAPSHOT })),
}));

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
    expect(rows[0]?.slotStartUnixMs).toBe(1000);
    expect(rows[0]?.id).toMatch(/^0:1000:/);
  });

  it('puts the newest slot first', () => {
    useFt8Store.getState().ingest(batch(1000, ['old']));
    useFt8Store.getState().ingest(batch(2000, ['new']));
    expect(useFt8Store.getState().rows[0]?.text).toBe('new');
    expect(useFt8Store.getState().rows[1]?.text).toBe('old');
  });

  it('bounds the table to MAX_ROWS', () => {
    for (let s = 0; s < 60; s++) {
      const texts = Array.from({ length: 20 }, (_, i) => `msg-${s}-${i}`);
      useFt8Store.getState().ingest(batch(s * 15000, texts));
    }
    // 60 slots * 20 = 1200 ingested, capped at 500.
    expect(useFt8Store.getState().rows.length).toBe(500);
    // Newest slot's rows survived.
    expect(useFt8Store.getState().rows[0]?.text).toMatch(/^msg-59-/);
  });

  it('clear() empties the table', () => {
    useFt8Store.getState().ingest(batch(1000, ['a', 'b']));
    useFt8Store.getState().clear();
    expect(useFt8Store.getState().rows).toHaveLength(0);
  });
});

describe('ft8-store prior-radio snapshot survives an in-FT8 band change (BUG 2)', () => {
  const flush = () => new Promise((r) => setTimeout(r, 0));

  beforeEach(() => {
    vi.clearAllMocks();
    useFt8Store.setState({ open: false, priorRadio: null, band: '20m', protocol: 'FT8' });
    // Park on band A's FT8 sub-band so openWorkspace's nearestDigitalBand lands
    // there; the snapshot itself is the mocked PRIOR_SNAPSHOT.
    useConnectionStore.setState({ vfoHz: 14_074_000 });
    // enable()/disable() POST to the backend — stub fetch so the workspace
    // entry/exit IIFEs complete without a network.
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => ({
        ok: true,
        json: async () => ({ enabled: true, nativeAvailable: true }),
      })) as never,
    );
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('engage on band A → band-change to B → exit restores A’s original freq+mode', async () => {
    // Engage FT8 on band A. The snapshot is captured ONCE here.
    useFt8Store.getState().openWorkspace();
    await flush();
    expect(snapshotRadio).toHaveBeenCalledTimes(1);
    expect(useFt8Store.getState().priorRadio).toEqual(PRIOR_SNAPSHOT);

    // Change band WHILE engaged (band B = 40 m). This reconfigures the radio for
    // digital on B but MUST NOT re-snapshot or overwrite the band-A prior.
    useFt8Store.getState().qsyBand('40m');
    await flush();
    expect(configureRadioForDigital).toHaveBeenLastCalledWith('FT8', '40m');
    expect(snapshotRadio).toHaveBeenCalledTimes(1); // still only the entry snapshot
    expect(useFt8Store.getState().priorRadio).toEqual(PRIOR_SNAPSHOT);

    // Exit. The restore must receive band A's ORIGINAL config — not band B's DIGU
    // dial — so the radio snaps back to where the operator was before FT8.
    useFt8Store.getState().closeWorkspace();
    await flush();
    expect(restoreRadioWhenIdle).toHaveBeenCalledTimes(1);
    expect(restoreRadioWhenIdle).toHaveBeenCalledWith(PRIOR_SNAPSHOT);
    expect(useFt8Store.getState().priorRadio).toBeNull();
  });

  it('a `prior` carried from another digital mode wins over re-snapshotting', async () => {
    // FT8↔WSPR carry-forward: the TRUE pre-digital snapshot is passed in and must
    // be the one restored on exit, never a fresh (already-DIGU) snapshot.
    const carried = { ...PRIOR_SNAPSHOT, vfoHz: 7_074_000, mode: 'LSB' as const };
    useFt8Store.getState().openWorkspace({ prior: carried });
    await flush();
    expect(snapshotRadio).not.toHaveBeenCalled();
    expect(useFt8Store.getState().priorRadio).toEqual(carried);

    useFt8Store.getState().closeWorkspace();
    await flush();
    expect(restoreRadioWhenIdle).toHaveBeenCalledWith(carried);
  });
});
