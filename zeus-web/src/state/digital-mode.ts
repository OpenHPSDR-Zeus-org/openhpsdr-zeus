// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Digital-mode radio orchestration. Entering FT8/FT4/WSPR is a Zeus-level mode
// (like FreeDV): the radio is put into DIGU with a wide, flat RX filter and
// QSY'd to the band's standard dial frequency so the decoder is fed the whole
// sub-band — the documented fix for "workspace opens but nothing decodes"
// (operator was parked off the FT8 sub-band). Entry snapshots the prior RX
// config so exit is fully reversible.
//
// SAFETY: nothing here ever retunes or re-modes a TRANSMITTING radio. An
// external HF amp can be keyed; silently QSYing mid-transmit is exactly the
// kind of operator-felt surprise the project's hard rules forbid. Every radio
// mutation is gated on !txActive().

import { setFilter, setMode, setVfo, type RxMode } from '../api/client';
import { useConnectionStore } from './connection-store';
import { useTxStore } from './tx-store';
import {
  DIGITAL_RX_FILTER_HIGH_HZ,
  DIGITAL_RX_FILTER_LOW_HZ,
  dialHzFor,
  nearestDigitalBand,
  type DigitalProtocol,
} from '../dsp/digital-segments';
import { applyFt8Framing, restoreFt8Framing } from './ft8-framing';

/** The RX config captured at digital-mode entry so exit can restore it.
 *  Includes the display framing (CTUN / LO / zoom) the FT8 waterfall applies, so
 *  exit reverses the framing alongside mode/filter/VFO. */
export interface RadioModeSnapshot {
  mode: RxMode;
  filterLowHz: number;
  filterHighHz: number;
  vfoHz: number;
  ctunEnabled: boolean;
  radioLoHz: number;
  zoomLevel: number;
}

/** True while the radio is keyed or tuning — radio reconfig is suppressed. */
function txActive(): boolean {
  const tx = useTxStore.getState();
  return tx.moxOn || tx.tunOn;
}

/** Snapshot the radio's current RX config so digital-mode entry is reversible. */
export function snapshotRadio(): RadioModeSnapshot {
  const s = useConnectionStore.getState();
  return {
    mode: s.mode,
    filterLowHz: s.filterLowHz,
    filterHighHz: s.filterHighHz,
    vfoHz: s.vfoHz,
    ctunEnabled: s.ctunEnabled,
    radioLoHz: s.radioLoHz,
    zoomLevel: s.zoomLevel,
  };
}

/**
 * Configure the radio for a digital mode: DIGU, wide flat RX filter, and QSY to
 * the band's dial frequency. `bandName` overrides the band; otherwise the band
 * whose FT8 dial is nearest the current VFO is used (so "enter FT8" lands on the
 * sub-band the operator was already near). No-op on the radio while
 * transmitting.
 */
export async function configureRadioForDigital(
  protocol: DigitalProtocol,
  bandName?: string,
): Promise<void> {
  if (txActive()) return;
  const vfoHz = useConnectionStore.getState().vfoHz;
  const near = nearestDigitalBand(vfoHz);
  const band = bandName ?? near.name;
  // Fall back to the nearest band's FT8 dial if this protocol has no sub-band on
  // the requested band (e.g. FT4 on 160 m).
  const dial = dialHzFor(protocol, band) ?? near.ft8Hz;
  try {
    await setMode('DIGU');
    await setFilter(DIGITAL_RX_FILTER_LOW_HZ, DIGITAL_RX_FILTER_HIGH_HZ);
    if (dial && Math.abs(dial - vfoHz) > 1) await setVfo(dial);
    // Frame the waterfall onto the FT8 passband (CTUN / LO / zoom). Uses the
    // post-QSY dial so the passband centres on the sub-band we just moved to.
    // FT8-specific: WSPR shares this orchestration but renders no passband
    // overlay and never requested CTUN/LO/zoom framing, so it is excluded.
    if (protocol !== 'WSPR') await applyFt8Framing(dial ?? vfoHz);
  } catch {
    // Best-effort: the decoder still runs on whatever audio is present, and the
    // operator can tune manually. Don't let a transient radio error block entry.
  }
}

/**
 * QSY to a specific band's dial for the active protocol (workspace band
 * buttons). Returns the dial Hz it moved to, or null when the band/protocol has
 * no sub-band or the move was suppressed (transmitting).
 */
export async function qsyToDigitalBand(
  protocol: DigitalProtocol,
  bandName: string,
): Promise<number | null> {
  if (txActive()) return null;
  const dial = dialHzFor(protocol, bandName);
  if (!dial) return null;
  try {
    await setVfo(dial);
    // Re-frame the passband around the new dial (under CTUN the frozen LO offset
    // would otherwise stay anchored to the previous band's dial). FT8-specific —
    // WSPR has no passband overlay and never framed on entry, so it never
    // reframes on QSY either.
    if (protocol !== 'WSPR') await applyFt8Framing(dial);
    return dial;
  } catch {
    return null;
  }
}

/** Restore a snapshot taken at entry (mode/filter/VFO) for a clean exit. */
export async function restoreRadio(snap: RadioModeSnapshot | null): Promise<void> {
  if (!snap || txActive()) return;
  try {
    // Reverse the FT8 waterfall framing (CTUN / LO / zoom) FIRST, then restore
    // mode/filter/VFO, so the dial lands on the operator's pre-FT8 frequency
    // rather than the CTUN-frozen LO offset.
    await restoreFt8Framing({
      ctunEnabled: snap.ctunEnabled,
      radioLoHz: snap.radioLoHz,
      zoomLevel: snap.zoomLevel,
    });
    await setMode(snap.mode);
    await setFilter(snap.filterLowHz, snap.filterHighHz);
    await setVfo(snap.vfoHz);
  } catch {
    // Best-effort restore.
  }
}
