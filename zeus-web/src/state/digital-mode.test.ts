// SPDX-License-Identifier: GPL-2.0-or-later
//
// digital-mode orchestration tests. The shared configureRadioForDigital drives
// BOTH FT8/FT4 and WSPR, for entry AND band-change QSY (the stores route band
// buttons through it). FT8 frames the waterfall onto its audio passband (CTUN /
// LO / zoom); WSPR renders no passband overlay and must NOT inherit that
// framing. These assert the protocol gate so opening or QSYing the WSPR
// workspace never silently enables CTUN or zooms the display. The QSY tests also
// pin the ordering invariant: setVfo before setMode('DIGU') so the server's
// cross-band per-band mode recall can't clobber DIGU.

import { beforeEach, describe, expect, it, vi } from 'vitest';
import { setCtun, setFilter, setMode, setRadioLo, setVfo, setZoom } from '../api/client';
import { useConnectionStore } from './connection-store';
import { useTxStore } from './tx-store';
import { configureRadioForDigital, restoreRadio, type RadioModeSnapshot } from './digital-mode';

vi.mock('../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api/client')>();
  const ok = vi.fn(async () => ({}) as never);
  return {
    ...actual,
    setMode: vi.fn(async () => ({}) as never),
    setFilter: vi.fn(async () => ({}) as never),
    setVfo: vi.fn(async () => ({}) as never),
    setCtun: ok,
    setRadioLo: ok,
    setZoom: ok,
  };
});

beforeEach(() => {
  vi.clearAllMocks();
  // Park on the 20 m FT8 sub-band; stub applyState so the framing's server-echo
  // plumbing is inert (its real reducer needs a full RadioStateDto we don't
  // build here). tx-store defaults to moxOn/tunOn=false (not transmitting), so
  // we don't setState it — that would trip the persist/localStorage quirk local
  // vitest has (see feedback memory), and the defaults are exactly what we want.
  useConnectionStore.setState({ vfoHz: 14_074_000, applyState: vi.fn() as never });
  expect(useTxStore.getState().moxOn || useTxStore.getState().tunOn).toBe(false);
});

describe('digital-mode framing gate', () => {
  it('FT8 entry frames the waterfall (CTUN + LO + zoom)', async () => {
    await configureRadioForDigital('FT8', '20m');
    expect(setMode).toHaveBeenCalledWith('DIGU');
    expect(setCtun).toHaveBeenCalled();
    expect(setZoom).toHaveBeenCalled();
    expect(setRadioLo).toHaveBeenCalled();
  });

  it('WSPR entry does NOT post CTUN / zoom / LO framing', async () => {
    await configureRadioForDigital('WSPR', '20m');
    // Still configured for digital (mode/QSY happen)…
    expect(setMode).toHaveBeenCalledWith('DIGU');
    expect(setVfo).toHaveBeenCalled();
    // …but the FT8-only passband framing must be skipped.
    expect(setCtun).not.toHaveBeenCalled();
    expect(setZoom).not.toHaveBeenCalled();
    expect(setRadioLo).not.toHaveBeenCalled();
  });

  it('WSPR QSY does NOT reframe the passband', async () => {
    await configureRadioForDigital('WSPR', '40m');
    expect(setVfo).toHaveBeenCalled();
    expect(setMode).toHaveBeenCalledWith('DIGU');
    expect(setCtun).not.toHaveBeenCalled();
    expect(setZoom).not.toHaveBeenCalled();
    expect(setRadioLo).not.toHaveBeenCalled();
  });

  it('FT8 QSY reframes the passband', async () => {
    await configureRadioForDigital('FT8', '40m');
    expect(setVfo).toHaveBeenCalled();
    expect(setCtun).toHaveBeenCalled();
    expect(setZoom).toHaveBeenCalled();
  });

  it('is a NO-OP when already on the target digital config (pop-out churn fix)', async () => {
    // Radio already configured for 20 m FT8: DIGU + 150..3000 filter + on dial.
    // The band-follow effect / redundant qsyBand must issue NOTHING — a stray
    // setVfo would trip the server per-band mode recall and perturb decode.
    useConnectionStore.setState({
      vfoHz: 14_074_000,
      mode: 'DIGU' as never,
      filterLowHz: 150,
      filterHighHz: 3000,
    });
    await configureRadioForDigital('FT8', '20m');
    expect(setVfo).not.toHaveBeenCalled();
    expect(setMode).not.toHaveBeenCalled();
    expect(setCtun).not.toHaveBeenCalled();
  });

  it('STILL reconfigures when the server flipped mode off DIGU (recall recovery)', async () => {
    // Same dial + filter, but the server's per-band recall knocked mode to USB:
    // we must re-assert DIGU so decode survives.
    useConnectionStore.setState({
      vfoHz: 14_074_000,
      mode: 'USB' as never,
      filterLowHz: 150,
      filterHighHz: 3000,
    });
    await configureRadioForDigital('FT8', '20m');
    expect(setMode).toHaveBeenCalledWith('DIGU');
  });

  it('cross-band QSY moves the VFO BEFORE asserting DIGU (recall-proof order)', async () => {
    const order: string[] = [];
    (setVfo as unknown as ReturnType<typeof vi.fn>).mockImplementation(async () => {
      order.push('vfo');
      return {} as never;
    });
    (setMode as unknown as ReturnType<typeof vi.fn>).mockImplementation(async () => {
      order.push('mode');
      return {} as never;
    });
    await configureRadioForDigital('FT8', '40m');
    expect(order).toEqual(['vfo', 'mode']);
  });
});

describe('digital-mode exit restore (recall-proof, BUG 2)', () => {
  const snapA: RadioModeSnapshot = {
    mode: 'USB',
    filterLowHz: -2700,
    filterHighHz: -300,
    vfoHz: 14_200_000, // band A (20 m phone), the pre-engage dial
    ctunEnabled: false,
    radioLoHz: 14_200_000,
    zoomLevel: 1,
  };

  it('QSYs back FIRST, then re-asserts mode + filter LAST (wins over #974 recall)', async () => {
    // The exact failure mode of BUG 2: on a cross-band exit, restoring the VFO
    // crosses a band edge and trips the server's per-band mode recall
    // (SetVfo → RestoreBandMode → SetMode). If mode/filter were restored BEFORE
    // the VFO they'd be re-clobbered to the band's stored (DIGU) mode. Asserting
    // them AFTER the QSY makes the snapshot win.
    const order: string[] = [];
    (setVfo as unknown as ReturnType<typeof vi.fn>).mockImplementation(async () => {
      order.push('vfo');
      return {} as never;
    });
    (setMode as unknown as ReturnType<typeof vi.fn>).mockImplementation(async () => {
      order.push('mode');
      return {} as never;
    });
    (setFilter as unknown as ReturnType<typeof vi.fn>).mockImplementation(async () => {
      order.push('filter');
      return {} as never;
    });

    await restoreRadio(snapA);

    expect(order).toEqual(['vfo', 'mode', 'filter']);
    expect(setVfo).toHaveBeenCalledWith(14_200_000);
    expect(setMode).toHaveBeenCalledWith('USB');
    expect(setFilter).toHaveBeenCalledWith(-2700, -300);
  });

  it('never touches the radio while transmitting', async () => {
    // Spy on getState rather than setState({moxOn}) — tx-store's persist
    // middleware writes localStorage, which local vitest can't honour (see
    // feedback memory); the spy keeps the txActive() gate exercised without it.
    const real = useTxStore.getState();
    const spy = vi
      .spyOn(useTxStore, 'getState')
      .mockReturnValue({ ...real, moxOn: true, tunOn: false });
    try {
      await restoreRadio(snapA);
      expect(setVfo).not.toHaveBeenCalled();
      expect(setMode).not.toHaveBeenCalled();
      expect(setFilter).not.toHaveBeenCalled();
    } finally {
      spy.mockRestore();
    }
  });
});
