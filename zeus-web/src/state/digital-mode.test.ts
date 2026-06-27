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
import { setCtun, setMode, setRadioLo, setVfo, setZoom } from '../api/client';
import { useConnectionStore } from './connection-store';
import { useTxStore } from './tx-store';
import { configureRadioForDigital } from './digital-mode';

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
