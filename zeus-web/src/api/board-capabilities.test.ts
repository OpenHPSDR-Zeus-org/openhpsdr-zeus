// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.

import { describe, expect, it } from 'vitest';

import {
  parseBoardCapabilities,
  UNKNOWN_BOARD_CAPABILITIES,
} from './board-capabilities';

describe('parseBoardCapabilities', () => {
  it('coerces a well-formed Saturn-class snapshot', () => {
    // Mirrors what the server emits for an OrionMkII (G2 default variant).
    const caps = parseBoardCapabilities({
      rxAdcCount: 2,
      mkiiBpf: true,
      adcSupplyMv: 50,
      lrAudioSwap: false,
      hasVolts: true,
      hasAmps: true,
      hasAudioAmplifier: true,
      hasSteppedAttenuationRx2: true,
      supportsPathIllustrator: false,
      hasHl2OptionalToggles: false,
      maxPowerWatts: 120,
    });
    expect(caps.rxAdcCount).toBe(2);
    expect(caps.mkiiBpf).toBe(true);
    expect(caps.adcSupplyMv).toBe(50);
    expect(caps.hasVolts).toBe(true);
    expect(caps.supportsPathIllustrator).toBe(false);
    expect(caps.maxPowerWatts).toBe(120);
  });

  it('falls back to UNKNOWN_BOARD_CAPABILITIES on garbage input', () => {
    const caps = parseBoardCapabilities(null);
    expect(caps).toEqual(UNKNOWN_BOARD_CAPABILITIES);
  });

  it('fills missing fields from defaults', () => {
    const caps = parseBoardCapabilities({ rxAdcCount: 2 });
    expect(caps.rxAdcCount).toBe(2);
    expect(caps.adcSupplyMv).toBe(UNKNOWN_BOARD_CAPABILITIES.adcSupplyMv);
    expect(caps.hasVolts).toBe(false);
    expect(caps.maxPowerWatts).toBe(UNKNOWN_BOARD_CAPABILITIES.maxPowerWatts);
  });

  it('rejects non-positive maxPowerWatts and falls back to default', () => {
    // A misconfigured backend (or an old server pre-MaxPowerWatts) might
    // ship 0 or a negative number. The parser should treat that as "unset"
    // and use the safe fallback so the meter axis stays usable.
    expect(parseBoardCapabilities({ maxPowerWatts: 0 }).maxPowerWatts).toBe(
      UNKNOWN_BOARD_CAPABILITIES.maxPowerWatts,
    );
    expect(parseBoardCapabilities({ maxPowerWatts: -5 }).maxPowerWatts).toBe(
      UNKNOWN_BOARD_CAPABILITIES.maxPowerWatts,
    );
  });

  it('accepts kilowatt-class radios (G2-1K)', () => {
    const caps = parseBoardCapabilities({ maxPowerWatts: 1000 });
    expect(caps.maxPowerWatts).toBe(1000);
  });

  it('hasHl2OptionalToggles defaults to false and round-trips when set', () => {
    expect(parseBoardCapabilities({}).hasHl2OptionalToggles).toBe(false);
    expect(
      parseBoardCapabilities({ hasHl2OptionalToggles: true }).hasHl2OptionalToggles,
    ).toBe(true);
  });
});
