// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

// Protected digital sub-bands for the auto-notch carrier detector.
//
// FT8 (and FT4) sub-bands are a dense forest of narrow, dead-steady carriers —
// exactly what a carrier-line detector is built to flag. Notching them is wrong:
// the operator is (or may be) decoding there, and the notches gut the segment.
// These dial frequencies are fixed by worldwide convention, so they live here as
// an authoritative table rather than relying on the user-editable band plan
// (which only carries coarse "digital" segment markers).
//
// Each entry is a mode-2 (USB) dial frequency; the occupied RF runs from the
// dial up ~3 kHz of audio passband. We protect [dial − guard, dial + 3000 +
// guard] so a het sitting just off the edge of the segment is still left alone.

export type ProtectedRange = { lowHz: number; highHz: number };

/** The slotted digital modes Zeus can enter. WSPR is a beacon (no QSO), FT8/FT4
 *  are the QSO modes; all three transmit USB / DIGU. */
export type DigitalProtocol = 'FT8' | 'FT4' | 'WSPR';

/** One ham band's USB dial frequencies for the digital modes. A protocol's dial
 *  is `undefined` when there is no established sub-band for it on that band. */
export interface DigitalBand {
  /** Band label, e.g. "20m". */
  name: string;
  ft8Hz: number;
  ft4Hz?: number;
  wsprHz?: number;
}

/** Per-band USB dial frequencies for the slotted digital modes — the single
 *  source of truth for auto-QSY, the workspace band selector, and the auto-notch
 *  protected ranges below. FT8/FT4 values are the WSJT-X defaults; WSPR values
 *  are the WSPRnet standard dials. These are fixed by worldwide convention, but
 *  have shifted historically (e.g. WSPR 80 m moved to 3.5686 MHz) — re-check
 *  against the WSJT-X frequency table / WSPRnet before trusting blindly. */
export const DIGITAL_BANDS: readonly DigitalBand[] = [
  { name: '160m', ft8Hz: 1_840_000, wsprHz: 1_836_600 },
  { name: '80m', ft8Hz: 3_573_000, ft4Hz: 3_575_000, wsprHz: 3_568_600 },
  { name: '60m', ft8Hz: 5_357_000, wsprHz: 5_364_700 },
  { name: '40m', ft8Hz: 7_074_000, ft4Hz: 7_047_500, wsprHz: 7_038_600 },
  { name: '30m', ft8Hz: 10_136_000, ft4Hz: 10_140_000, wsprHz: 10_138_700 },
  { name: '20m', ft8Hz: 14_074_000, ft4Hz: 14_080_000, wsprHz: 14_095_600 },
  { name: '17m', ft8Hz: 18_100_000, ft4Hz: 18_104_000, wsprHz: 18_104_600 },
  { name: '15m', ft8Hz: 21_074_000, ft4Hz: 21_140_000, wsprHz: 21_094_600 },
  { name: '12m', ft8Hz: 24_915_000, ft4Hz: 24_919_000, wsprHz: 24_924_600 },
  { name: '10m', ft8Hz: 28_074_000, ft4Hz: 28_180_000, wsprHz: 28_124_600 },
  { name: '6m', ft8Hz: 50_313_000, ft4Hz: 50_318_000, wsprHz: 50_293_000 },
  { name: '2m', ft8Hz: 144_174_000, wsprHz: 144_489_000 },
];

/** The USB dial frequency for a protocol on a named band, or undefined if the
 *  band/protocol pair has no established sub-band. */
export function dialHzFor(protocol: DigitalProtocol, band: string): number | undefined {
  const b = DIGITAL_BANDS.find((x) => x.name === band);
  if (!b) return undefined;
  return protocol === 'FT8' ? b.ft8Hz : protocol === 'FT4' ? b.ft4Hz : b.wsprHz;
}

/** The digital band whose FT8 dial is closest to an arbitrary VFO frequency —
 *  lets "enter FT8" land the operator on the sub-band they were already near. */
export function nearestDigitalBand(hz: number): DigitalBand {
  let best = DIGITAL_BANDS[0]!;
  let bestDist = Infinity;
  for (const b of DIGITAL_BANDS) {
    const dist = Math.abs(b.ft8Hz - hz);
    if (dist < bestDist) {
      bestDist = dist;
      best = b;
    }
  }
  return best;
}

/** FT8/FT4 occupy a wide, flat USB sub-band — the decoder searches the whole
 *  ~0–3 kHz passband, so the RX filter must be WIDE, never narrowed onto a
 *  single signal. DIGU, ~150..3000 Hz. This is the auto-bandwidth applied on
 *  digital-mode entry. */
export const DIGITAL_RX_FILTER_LOW_HZ = 150;
export const DIGITAL_RX_FILTER_HIGH_HZ = 3000;

/** FT8 dial frequencies (USB), in Hz, derived from the band table. */
const FT8_DIAL_HZ: readonly number[] = DIGITAL_BANDS.map((b) => b.ft8Hz);

/** FT4 dial frequencies (USB), in Hz, derived from the band table. */
const FT4_DIAL_HZ: readonly number[] = DIGITAL_BANDS.map((b) => b.ft4Hz).filter(
  (hz): hz is number => hz != null,
);

const SEGMENT_AUDIO_HZ = 3_000; // FT8/FT4 occupy ~dial..dial+3 kHz of audio
const SEGMENT_GUARD_HZ = 300; // a little padding either side of the segment

function rangesFor(dials: readonly number[]): ProtectedRange[] {
  return dials.map((dial) => ({
    lowHz: dial - SEGMENT_GUARD_HZ,
    highHz: dial + SEGMENT_AUDIO_HZ + SEGMENT_GUARD_HZ,
  }));
}

/** Absolute-frequency windows where the auto-notch detector must not place a
 *  notch — the FT8 and FT4 sub-bands across every supported band. */
export const DIGITAL_PROTECTED_RANGES: readonly ProtectedRange[] = [
  ...rangesFor(FT8_DIAL_HZ),
  ...rangesFor(FT4_DIAL_HZ),
].sort((a, b) => a.lowHz - b.lowHz);

/** True when `hz` falls inside any protected digital sub-band. */
export function isInProtectedDigitalSegment(hz: number): boolean {
  for (const r of DIGITAL_PROTECTED_RANGES) {
    if (hz >= r.lowHz && hz <= r.highHz) return true;
  }
  return false;
}
