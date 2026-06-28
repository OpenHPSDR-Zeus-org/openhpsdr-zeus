// SPDX-License-Identifier: GPL-2.0-or-later
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { setCtun, setRadioLo, setZoom } from '../api/client';
import { useConnectionStore } from './connection-store';
import { useTxStore } from './tx-store';

// tx-store persists via zustand persist, whose storage is bound at import; local
// vitest's jsdom localStorage trips setItem (see feedback memory), so a setState
// throws AFTER updating state. Wrap the one place we must flip MOX so the state
// change still lands and the (local-only) persist error is swallowed; defaults
// are already moxOn/tunOn=false so the non-TX tests need no setState at all.
function setTransmitting(on: boolean): void {
  try {
    useTxStore.setState({ moxOn: on, tunOn: false });
  } catch {
    /* local-only persist/localStorage quirk — state already updated */
  }
}
import {
  FT8_FRAMING_ZOOM,
  FT8_PASSBAND_CENTER_HZ,
  USE_CTUN_CENTERING,
  applyFt8Framing,
  framingTargets,
  restoreFt8Framing,
} from './ft8-framing';

const DIAL = 14_074_000;

describe('framingTargets', () => {
  it('applies the entry zoom regardless of centring mode', () => {
    expect(framingTargets(DIAL).zoomLevel).toBe(FT8_FRAMING_ZOOM);
  });

  it('frames the passband per the active centring mode', () => {
    const t = framingTargets(DIAL);
    if (USE_CTUN_CENTERING) {
      // CTUN: enable CTUN and freeze the LO at dial + passband centre so the
      // band sits centred while the dial (decode reference) stays put.
      expect(t.ctunEnabled).toBe(true);
      expect(t.radioLoHz).toBe(DIAL + FT8_PASSBAND_CENTER_HZ);
    } else {
      // Fallback: dial-centred, CTUN untouched.
      expect(t.ctunEnabled).toBe(false);
      expect(t.radioLoHz).toBe(DIAL);
    }
  });

  it('rounds the LO to an integer Hz', () => {
    const t = framingTargets(DIAL + 0.4);
    expect(Number.isInteger(t.radioLoHz)).toBe(true);
  });
});

// Reversibility — the load-bearing entry/exit sequencing. These mock the radio
// POSTs and assert order/skip/re-freeze, the tricky part that, if wrong, strands
// the operator's dial under CTUN on exit.
vi.mock('../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api/client')>();
  return {
    ...actual,
    setCtun: vi.fn(async (on: boolean) => ({ ctunEnabled: on }) as never),
    setRadioLo: vi.fn(async (hz: number) => ({ radioLoHz: hz }) as never),
    setZoom: vi.fn(async (z: number) => ({ zoomLevel: z }) as never),
  };
});

describe('applyFt8Framing / restoreFt8Framing reversibility', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    setTransmitting(false);
    // Stub applyState so the framing's server-echo plumbing is inert in the test
    // (its real reducer needs a full RadioStateDto we are not constructing here).
    useConnectionStore.setState({ applyState: vi.fn() as never });
  });

  it('applies CTUN, then the frozen LO, then zoom (order matters)', async () => {
    if (!USE_CTUN_CENTERING) return;
    await applyFt8Framing(DIAL);
    expect(setCtun).toHaveBeenCalledWith(true);
    expect(setRadioLo).toHaveBeenCalledWith(DIAL + FT8_PASSBAND_CENTER_HZ);
    expect(setZoom).toHaveBeenCalledWith(FT8_FRAMING_ZOOM);
    const ctunOrder = (setCtun as ReturnType<typeof vi.fn>).mock.invocationCallOrder[0]!;
    const loOrder = (setRadioLo as ReturnType<typeof vi.fn>).mock.invocationCallOrder[0]!;
    const zoomOrder = (setZoom as ReturnType<typeof vi.fn>).mock.invocationCallOrder[0]!;
    expect(ctunOrder).toBeLessThan(loOrder);
    expect(loOrder).toBeLessThan(zoomOrder);
  });

  it('restore with CTUN-off zooms back and disables CTUN, NOT setRadioLo', async () => {
    await restoreFt8Framing({ ctunEnabled: false, radioLoHz: 0, zoomLevel: 3 });
    expect(setZoom).toHaveBeenCalledWith(3);
    expect(setCtun).toHaveBeenCalledWith(false);
    expect(setRadioLo).not.toHaveBeenCalled();
  });

  it('restore with CTUN-on re-freezes the saved LO', async () => {
    await restoreFt8Framing({ ctunEnabled: true, radioLoHz: 14_075_400, zoomLevel: 5 });
    expect(setZoom).toHaveBeenCalledWith(5);
    expect(setCtun).toHaveBeenCalledWith(true);
    expect(setRadioLo).toHaveBeenCalledWith(14_075_400);
  });

  it('both are no-ops while transmitting', async () => {
    setTransmitting(true);
    await applyFt8Framing(DIAL);
    await restoreFt8Framing({ ctunEnabled: true, radioLoHz: 1, zoomLevel: 1 });
    expect(setCtun).not.toHaveBeenCalled();
    expect(setRadioLo).not.toHaveBeenCalled();
    expect(setZoom).not.toHaveBeenCalled();
  });
});
