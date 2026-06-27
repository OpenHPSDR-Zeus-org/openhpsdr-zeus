// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// ft8-framing — frames the shipping RF panadapter/waterfall onto the FT8/FT4
// audio passband on entry, and is fully reversible on exit. NO new renderer and
// NO audio-baseband FFT: an RF display zoomed tight onto [dial .. dial+~3 kHz]
// IS the 0..3000 Hz audio cascade WSJT-X shows (FT8 is USB).
//
// Two framings, behind one switch:
//   • CTUN-centred (USE_CTUN_CENTERING=true): enable CTUN and freeze the
//     hardware LO at dial + ~1.4 kHz so the passband sits centred in the panel
//     while the VFO/dial (the decoder's reference) stays put. This is the
//     recommended framing — but it exercises CTUN x DIGU, which is the ONE thing
//     worth verifying on the G2. If it misbehaves at the bench, flip the switch.
//   • Dial-centred fallback (false): leave CTUN alone; just apply a tight zoom.
//     The band fills the right ~75% of the panel — still perfectly usable, zero
//     risk. "dial ±~1.5 kHz".
//
// Either way the overlay/click math reads the LIVE slice geometry + the dial, so
// it is correct regardless of which framing is active.
//
// SAFETY: every mutation is gated on !txActive() (never reconfigure a keyed
// radio) and is display-only — it touches CTUN / LO / zoom, never power,
// PureSignal, mode, or filter. Exit restores the captured snapshot.

import { setCtun, setRadioLo, setZoom, type RadioStateDto } from '../api/client';
import { useConnectionStore } from './connection-store';
import { useTxStore } from './tx-store';

/**
 * CTUN-centred framing is the recommended primary (see header). Default ON per
 * the design; this is the single one-line switch to the zero-risk dial-centred
 * fallback if CTUN x DIGU misbehaves on the G2. Flagged for bench verification.
 */
export const USE_CTUN_CENTERING = true;

/** Where in the audio passband to centre the panel under CTUN framing (Hz).
 *  ~1.4 kHz puts 0 Hz left-of-centre and 3 kHz right-of-centre. */
export const FT8_PASSBAND_CENTER_HZ = 1400;

/** Entry zoom level so the visible span is ~3.5 kHz around the passband.
 *  Bench-tune on the G2; reversible via the entry snapshot. */
export const FT8_FRAMING_ZOOM = 8;

/** The reversible display state framing touches. Captured at entry, restored on
 *  exit. (digital-mode.ts folds these into its RadioModeSnapshot.) */
export interface FramingSnapshot {
  ctunEnabled: boolean;
  radioLoHz: number;
  zoomLevel: number;
}

/** The display targets framing wants for a given dial. PURE — unit tested. */
export interface FramingTargets {
  ctunEnabled: boolean;
  radioLoHz: number;
  zoomLevel: number;
}

/** True while the radio is keyed or tuning — display reconfig is suppressed. */
function txActive(): boolean {
  const tx = useTxStore.getState();
  return tx.moxOn || tx.tunOn;
}

/** The framing display targets for a dial frequency. CTUN-centred freezes the
 *  LO at dial + FT8_PASSBAND_CENTER_HZ; the fallback leaves the LO on the dial.
 *  Both apply the entry zoom. */
export function framingTargets(dialHz: number): FramingTargets {
  if (USE_CTUN_CENTERING) {
    return {
      ctunEnabled: true,
      radioLoHz: Math.round(dialHz + FT8_PASSBAND_CENTER_HZ),
      zoomLevel: FT8_FRAMING_ZOOM,
    };
  }
  return { ctunEnabled: false, radioLoHz: Math.round(dialHz), zoomLevel: FT8_FRAMING_ZOOM };
}

function applyServerState(s: RadioStateDto): void {
  useConnectionStore.getState().applyState(s);
}

/**
 * Apply the FT8 passband framing for `dialHz`. Best-effort: the decoder still
 * runs on whatever audio is present even if a framing POST fails, so a transient
 * radio error must never block workspace entry. No-op while transmitting.
 */
export async function applyFt8Framing(dialHz: number): Promise<void> {
  if (txActive()) return;
  const t = framingTargets(dialHz);
  try {
    if (t.ctunEnabled) {
      applyServerState(await setCtun(true));
      applyServerState(await setRadioLo(t.radioLoHz));
    }
    applyServerState(await setZoom(t.zoomLevel));
  } catch {
    // Best-effort — see header SAFETY note.
  }
}

/**
 * Restore the display state captured before framing was applied (CTUN, LO,
 * zoom). No-op while transmitting / with no snapshot. Order: zoom first, then
 * CTUN — disabling CTUN snaps the LO back to the dial, so when CTUN was off we
 * skip the explicit LO write; when it was on we re-freeze it at the saved LO.
 */
export async function restoreFt8Framing(snap: FramingSnapshot | null): Promise<void> {
  if (!snap || txActive()) return;
  try {
    applyServerState(await setZoom(snap.zoomLevel));
    if (snap.ctunEnabled) {
      applyServerState(await setCtun(true));
      applyServerState(await setRadioLo(snap.radioLoHz));
    } else {
      applyServerState(await setCtun(false));
    }
  } catch {
    // Best-effort restore.
  }
}
