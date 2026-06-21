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

/** FT8 dial frequencies (USB), in Hz. HF + 6 m + 2 m. */
const FT8_DIAL_HZ: readonly number[] = [
  1_840_000, // 160 m
  3_573_000, // 80 m
  5_357_000, // 60 m
  7_074_000, // 40 m
  10_136_000, // 30 m
  14_074_000, // 20 m
  18_100_000, // 17 m
  21_074_000, // 15 m
  24_915_000, // 12 m
  28_074_000, // 10 m
  50_313_000, // 6 m
  144_174_000, // 2 m
];

/** FT4 dial frequencies (USB), in Hz. Same carrier-forest problem as FT8. */
const FT4_DIAL_HZ: readonly number[] = [
  3_575_000, // 80 m
  7_047_500, // 40 m
  10_140_000, // 30 m
  14_080_000, // 20 m
  18_104_000, // 17 m
  21_140_000, // 15 m
  24_919_000, // 12 m
  28_180_000, // 10 m
  50_318_000, // 6 m
];

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
