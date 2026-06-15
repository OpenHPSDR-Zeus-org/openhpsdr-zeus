// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import { beforeEach, describe, expect, it } from 'vitest';
import {
  DEFAULT_WF_ROW_CADENCE,
  FIXED_DB_MAX,
  FIXED_DB_MIN,
  useDisplaySettingsStore,
} from './display-settings-store';

function resetStore() {
  useDisplaySettingsStore.setState({
    autoRange: false,
    dbMin: FIXED_DB_MIN,
    dbMax: FIXED_DB_MAX,
    waterfallRowCadence: DEFAULT_WF_ROW_CADENCE,
  });
}

describe('display-settings-store', () => {
  beforeEach(resetStore);

  it('returns the fixed range by default', () => {
    const { dbMin, dbMax, autoRange } = useDisplaySettingsStore.getState();
    expect(autoRange).toBe(false);
    expect(dbMin).toBe(FIXED_DB_MIN);
    expect(dbMax).toBe(FIXED_DB_MAX);
  });

  it('ignores updateAutoRange while autoRange is off', () => {
    const arr = new Float32Array(64).fill(-90);
    useDisplaySettingsStore.getState().updateAutoRange(arr);
    const { dbMin, dbMax } = useDisplaySettingsStore.getState();
    expect(dbMin).toBe(FIXED_DB_MIN);
    expect(dbMax).toBe(FIXED_DB_MAX);
  });

  it('snaps back to fixed range when turned off', () => {
    const s = useDisplaySettingsStore.getState();
    s.setAutoRange(true);
    useDisplaySettingsStore.setState({ dbMin: -80, dbMax: -40 });
    useDisplaySettingsStore.getState().setAutoRange(false);
    const { dbMin, dbMax } = useDisplaySettingsStore.getState();
    expect(dbMin).toBe(FIXED_DB_MIN);
    expect(dbMax).toBe(FIXED_DB_MAX);
  });

  it('drifts dbMin/dbMax toward percentile target with smoothing', () => {
    useDisplaySettingsStore.getState().setAutoRange(true);

    // Flat noise floor at -95, plus a top 5% of strong peaks at -50.
    const n = 200;
    const arr = new Float32Array(n);
    for (let i = 0; i < n; i++) arr[i] = i < n * 0.95 ? -95 : -50;

    for (let k = 0; k < 200; k++) {
      useDisplaySettingsStore.getState().updateAutoRange(arr);
    }

    const { dbMin, dbMax } = useDisplaySettingsStore.getState();
    // After many iterations the smoothed range converges on
    // (p5 - AUTO_FLOOR_MARGIN, p95 + AUTO_CEIL_MARGIN). p5 = -95, p95 = -50,
    // so targets are ≈ -103 and ≈ -44. Both should have moved well away
    // from the fixed defaults (-120, -30) toward the data.
    expect(dbMin).toBeGreaterThan(-110);
    expect(dbMax).toBeLessThan(-40);
    expect(dbMin).toBeLessThan(dbMax);
  });

  it('enforces a minimum span when the signal is flat', () => {
    useDisplaySettingsStore.getState().setAutoRange(true);
    const flat = new Float32Array(128).fill(-80);
    for (let k = 0; k < 400; k++) {
      useDisplaySettingsStore.getState().updateAutoRange(flat);
    }
    const { dbMin, dbMax } = useDisplaySettingsStore.getState();
    expect(dbMax - dbMin).toBeGreaterThanOrEqual(19.9);
  });

  it('handles an empty array without producing NaN', () => {
    useDisplaySettingsStore.getState().setAutoRange(true);
    useDisplaySettingsStore.getState().updateAutoRange(new Float32Array(0));
    const { dbMin, dbMax } = useDisplaySettingsStore.getState();
    expect(Number.isFinite(dbMin)).toBe(true);
    expect(Number.isFinite(dbMax)).toBe(true);
  });

  it('keeps balanced waterfall row cadence by default', () => {
    const { waterfallRowCadence } = useDisplaySettingsStore.getState();
    expect(waterfallRowCadence).toBe(DEFAULT_WF_ROW_CADENCE);
  });

  it('updates waterfall row cadence to valid operator choices only', () => {
    const s = useDisplaySettingsStore.getState();

    s.setWaterfallRowCadence(1);
    expect(useDisplaySettingsStore.getState().waterfallRowCadence).toBe(1);

    s.setWaterfallRowCadence(3);
    expect(useDisplaySettingsStore.getState().waterfallRowCadence).toBe(3);

    s.setWaterfallRowCadence(99 as never);
    expect(useDisplaySettingsStore.getState().waterfallRowCadence).toBe(DEFAULT_WF_ROW_CADENCE);
  });
});
