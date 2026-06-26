// FreeDV display-sideband convention for the panadapter/waterfall overlays.
//
// FreeDV carries no sideband of its own: the radio runs a real SSB demod on the
// band convention (LSB below 10 MHz, USB at/above), and the backend re-signs the
// WDSP bandpass at the engine seam while leaving the STORED filter USB-positive.
// displayFilterEdgesHz applies the same convention for the overlays so the
// passband/cursor draw on the side the demod is actually on. Mirrors the server
// FreeDvSidebandConventionTests + RadioService.SignedFilterForMode.

import { describe, expect, it } from 'vitest';
import { displayFilterEdgesHz } from './receiver-state';

describe('displayFilterEdgesHz — FreeDV band-convention sideband', () => {
  // Stored FreeDV passband is USB-positive (e.g. 200..2800 Hz).
  const LO = 200;
  const HI = 2800;

  it.each([
    ['160m', 1_800_000],
    ['80m', 3_573_000],
    ['40m', 7_177_000],
    ['just below 10 MHz', 9_999_999],
  ])('signs FreeDV LSB below 10 MHz (%s)', (_label, vfoHz) => {
    expect(displayFilterEdgesHz('FREEDV', vfoHz, LO, HI)).toEqual({
      lowHz: -HI,
      highHz: -LO,
    });
  });

  it.each([
    ['10 MHz boundary (inclusive → USB)', 10_000_000],
    ['20m', 14_236_000],
    ['10m', 28_330_000],
  ])('keeps FreeDV USB at/above 10 MHz (%s)', (_label, vfoHz) => {
    expect(displayFilterEdgesHz('FREEDV', vfoHz, LO, HI)).toEqual({
      lowHz: LO,
      highHz: HI,
    });
  });

  it('is sign-agnostic about the stored edges (uses magnitudes)', () => {
    // Even if state ever carried a negative-signed pair, the magnitudes decide.
    expect(displayFilterEdgesHz('FREEDV', 7_177_000, -HI, -LO)).toEqual({
      lowHz: -HI,
      highHz: -LO,
    });
  });

  it.each(['USB', 'LSB', 'CWU', 'CWL', 'AM', 'DIGU', 'DIGL'] as const)(
    'is a no-op for non-FreeDV mode %s (stored width passes through)',
    (mode) => {
      expect(displayFilterEdgesHz(mode, 7_177_000, -2800, -100)).toEqual({
        lowHz: -2800,
        highHz: -100,
      });
    },
  );
});
