// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.

import { useCallback, useEffect, useState } from 'react';
import { setTxFilter, type RxMode } from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { DriveSlider } from './DriveSlider';
import { TunePowerSlider } from './TunePowerSlider';
import { MicGainSlider } from './MicGainSlider';
import { LevelerMaxGainSlider } from './LevelerMaxGainSlider';

const CUSTOM_MIN = 0;
const CUSTOM_MAX = 10000;

// Mirror of ModeBandwidth's symmetry rule — AM/FM/DSB/SAM bandpasses are
// symmetric around 0 Hz; SSB / CW sidebands are one-sided with a sign
// determined by USB vs LSB.
function isSymmetricMode(mode: RxMode): boolean {
  return mode === 'AM' || mode === 'SAM' || mode === 'DSB' || mode === 'FM';
}

// Convert a signed (low, high) pair into absolute Hz the operator types into
// the custom inputs. LSB mode stores negative values; we show the positive
// magnitudes.
function signedToAbs(mode: RxMode, low: number, high: number): { lowAbs: number; highAbs: number } {
  if (isSymmetricMode(mode)) {
    return { lowAbs: 0, highAbs: Math.max(Math.abs(low), Math.abs(high)) };
  }
  const lo = Math.min(Math.abs(low), Math.abs(high));
  const hi = Math.max(Math.abs(low), Math.abs(high));
  return { lowAbs: lo, highAbs: hi };
}

// Re-sign the user's positive Hz inputs for the current mode's sideband
// convention. Matches RadioService.SignedFilterForMode on the server so the
// round-trip doesn't shift values under mode changes.
function absToSigned(mode: RxMode, lowAbs: number, highAbs: number): { low: number; high: number } {
  const lo = Math.max(CUSTOM_MIN, Math.min(CUSTOM_MAX, Math.round(lowAbs)));
  const hi = Math.max(CUSTOM_MIN, Math.min(CUSTOM_MAX, Math.round(highAbs)));
  const [lCap, hCap] = lo <= hi ? [lo, hi] : [hi, lo];
  switch (mode) {
    case 'USB':
    case 'DIGU':
    case 'CWU':
    case 'FREEDV':
      return { low: lCap, high: hCap };
    case 'LSB':
    case 'DIGL':
    case 'CWL':
      return { low: -hCap, high: -lCap };
    case 'AM':
    case 'SAM':
    case 'DSB':
    case 'FM':
      return { low: -hCap, high: hCap };
  }
}

// Operator-facing TX bandpass control. The presets row is deliberately empty
// for now — the frame and layout match the RX ModeBandwidth custom block so
// future preset chips (2.4k / 2.7k / 3.0k / 4k / 6k / 8k) can drop in beside
// the current CUSTOM pair without re-flowing the surrounding rail.
export function TxFilterPanel() {
  const mode = useConnectionStore((s) => s.mode);
  const low = useConnectionStore((s) => s.txFilterLowHz);
  const high = useConnectionStore((s) => s.txFilterHighHz);
  const applyState = useConnectionStore((s) => s.applyState);

  // Draft values so typing doesn't fire setTxFilter on every keystroke; we
  // commit on blur / Enter and reset the draft whenever the store-side pair
  // changes (mode flip, server reconciliation).
  const currentAbs = signedToAbs(mode, low, high);
  const [lowDraft, setLowDraft] = useState<string>(String(currentAbs.lowAbs));
  const [highDraft, setHighDraft] = useState<string>(String(currentAbs.highAbs));

  useEffect(() => {
    const abs = signedToAbs(mode, low, high);
    setLowDraft(String(abs.lowAbs));
    setHighDraft(String(abs.highAbs));
  }, [mode, low, high]);

  const commitCustom = useCallback(() => {
    const loAbs = Number.parseInt(lowDraft, 10);
    const hiAbs = Number.parseInt(highDraft, 10);
    if (!Number.isFinite(loAbs) || !Number.isFinite(hiAbs)) return;
    const { low: nextLow, high: nextHigh } = absToSigned(mode, loAbs, hiAbs);
    if (nextLow === low && nextHigh === high) return;
    useConnectionStore.setState({ txFilterLowHz: nextLow, txFilterHighHz: nextHigh });
    setTxFilter(nextLow, nextHigh)
      .then(applyState)
      .catch(() => {
        /* next state broadcast will reconcile */
      });
  }, [lowDraft, highDraft, mode, low, high, applyState]);

  const onCustomKeyDown = (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Enter') e.currentTarget.blur();
  };

  const lowDisabled = isSymmetricMode(mode);

  return (
    <div className="ctrl-group tx-filter-panel">
      <DriveSlider />
      <TunePowerSlider />
      {/* MIC + LVLR read as one mic-chain block. CSS gives each compact row
          a smaller slider floor and stacks them only when the tile gets too
          narrow, so dB readouts do not collide with the thumbs. */}
      <div className="tx-slider-stack">
        <MicGainSlider />
        <LevelerMaxGainSlider />
      </div>
      {/* FILTER section — labeled explicitly now that the panel header is just
          "TX". Top border separates the bandpass row from the sliders above so
          the operator reads them as distinct controls. */}
      <div
        className="label-xs ctrl-lbl tx-filter-label"
      >
        FILTER
      </div>
      <div className="tx-filter-custom-row">
        <span className="label-xs tx-filter-custom-label">CUSTOM</span>
        <input
          type="number"
          min={CUSTOM_MIN}
          max={CUSTOM_MAX}
          step={50}
          value={lowDraft}
          onChange={(e) => setLowDraft(e.currentTarget.value)}
          onBlur={commitCustom}
          onKeyDown={onCustomKeyDown}
          disabled={lowDisabled}
          aria-label="Custom TX filter low edge in Hz"
          className="mono tx-filter-input"
        />
        <span className="label-xs tx-filter-dash">–</span>
        <input
          type="number"
          min={CUSTOM_MIN}
          max={CUSTOM_MAX}
          step={50}
          value={highDraft}
          onChange={(e) => setHighDraft(e.currentTarget.value)}
          onBlur={commitCustom}
          onKeyDown={onCustomKeyDown}
          aria-label="Custom TX filter high edge in Hz"
          className="mono tx-filter-input"
        />
        <span className="label-xs mono tx-filter-unit">Hz</span>
        <span className="label-xs mono tx-filter-range-readout">
          [{Math.min(Math.abs(low), Math.abs(high))}…{Math.max(Math.abs(low), Math.abs(high))}]
        </span>
      </div>
    </div>
  );
}
