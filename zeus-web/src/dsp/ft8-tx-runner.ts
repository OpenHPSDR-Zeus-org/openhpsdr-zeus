// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// ft8-tx-runner — the React glue that turns the pure ft8-sequencer + the
// Ft8TxController into a LIVE keyer: it owns one controller per workspace and,
// once per UTC slot, hands the just-completed slot's decodes to the controller
// so the sequencer can stage the next message for the backend keyer.
//
// The BACKEND owns precise slot-boundary keying (its own UTC slot clock keys
// ~120 ms before the boundary if a fresh matching stage exists). This runner's
// only timing job is to POST a fresh stage every slot while armed — it processes
// the window a few seconds after each boundary, once the decoder has produced
// that slot's decodes, which leaves the stage fresh for the upcoming slot.
//
// SAFETY: the controller gates everything on enableTx, so running the slot timer
// unconditionally is safe — a disarmed controller's onWindow is a no-op and
// never POSTs /tx. Arming is still EXPLICIT ONLY (controller.enableTx()).

import { useEffect, useRef, useState } from 'react';
import { DIGITAL_PLUGIN_BASE } from '../api/digital-plugin';
import { useFt8Store, type Ft8Row } from '../state/ft8-store';
import { Ft8TxController, type Ft8TxBehavior } from './ft8-tx-controller';
import { parseFt8Message } from './ft8-message';
import type { DigitalQsoMode, NewQsoOpts, QsoState, Slot } from './ft8-sequencer';

/** Slot length (ms) for a digital mode. FT8 = 15 s, FT4 = 7.5 s. */
export function slotMsFor(mode: DigitalQsoMode): number {
  return mode === 'FT4' ? 7_500 : 15_000;
}

/**
 * Decoder settle time (ms) after a slot boundary before its decodes are in. Kept
 * inside the backend's late-start window (FT8 ≤2.5 s / FT4 ≤1.0 s into the slot,
 * see Ft8TxService.MaxLateStartSecondsFor) so a decode-driven reply staged here
 * keys in the SAME slot rather than a full cycle later. If a stage lands after the
 * window (decoder/POST jitter) the backend's one-cycle freshness window still
 * keys it at the next matching boundary — graceful degradation, never a stall.
 * G2 bench-tune.
 */
function settleMsFor(mode: DigitalQsoMode): number {
  return mode === 'FT4' ? 800 : 2_000;
}

/** The UTC slot index a given epoch-ms falls in, for a slot length. */
export function slotIndexOf(epochMs: number, slotMs: number): number {
  return Math.floor(epochMs / slotMs);
}

/** Even/odd parity of a slot index (even = the 0th/2nd/… slot of the minute). */
export function slotParity(slotIndex: number): Slot {
  return slotIndex % 2 === 0 ? 'even' : 'odd';
}

/**
 * Texts of the decode rows that belong to a given UTC slot index. Pure — the
 * runner's per-window input. Rows carry the backend's exact slot-start ms, so
 * membership is `floor(slotStartUnixMs / slotMs) === slotIndex`.
 */
export function decodesForSlot(rows: readonly Ft8Row[], slotIndex: number, slotMs: number): string[] {
  return rowsForSlot(rows, slotIndex, slotMs).map((r) => r.text);
}

/**
 * The decode ROWS (text + snrDb + offset) that belong to a given UTC slot index.
 * Sibling of {@link decodesForSlot}; the runner needs the full rows (not just the
 * text) so it can read the measured SNR of the DX station for the outgoing
 * report. Pure.
 */
export function rowsForSlot(rows: readonly Ft8Row[], slotIndex: number, slotMs: number): Ft8Row[] {
  return rows.filter((r) => slotIndexOf(r.slotStartUnixMs, slotMs) === slotIndex);
}

/**
 * The SNR (dB) we measured of the DX station in this window's decodes — the value
 * to report to him (Tx2 / Tx3). When a QSO is already latched we match the
 * station we're working (`state.dxCall`); while still calling CQ we use the SNR
 * of the first station calling us (the prospective answerer the sequencer will
 * pick). Returns undefined when no matching row exists, so the sequencer keeps
 * its own fallback rather than logging a constant.
 */
export function measuredDxSnr(rows: readonly Ft8Row[], state: QsoState): number | undefined {
  const dx = state.dxCall;
  for (const r of rows) {
    const m = parseFt8Message(r.text, state.myCall);
    if (dx) {
      if (m.deCall === dx) return r.snrDb;
    } else if (m.isCallingMe && m.deCall) {
      return r.snrDb;
    }
  }
  return undefined;
}

export interface Ft8TxRunnerView {
  /** Live QSO state snapshot (drives the TX-control UI). */
  qso: QsoState;
  /** Current TX audio offset (Hz). */
  audioHz: number;
  /** CALL 1ST auto-answer enabled. */
  callFirst: boolean;
  /** The message the machine would key next (preview). */
  outgoing: string | null;

  // Operator actions (each mirrors a controller method, then re-syncs the view).
  enableTx: () => void;
  disableTx: () => void;
  halt: () => void;
  setTxSlot: (slot: Slot) => void;
  setHoldTxFreq: (hold: boolean) => void;
  setTxFreq: (hz: number) => void;
  setCallFirst: (on: boolean) => void;
  startCq: (opts?: Partial<NewQsoOpts>) => void;
  answerCq: (decodeText: string, senderSlot: Slot) => boolean;
  callStation: (decodeText: string, senderSlot: Slot) => boolean;
  stageMacro: (message: string) => void;
  /** Latch the live QSO as logged (manual LOG QSO) so the auto-log can't double-fire. */
  markLogged: () => void;
}

export interface UseFt8TxRunnerOpts {
  myCall: string;
  myGrid?: string | null;
  mode: DigitalQsoMode;
  /** Workspace open / decoder live — gates the slot timer. */
  active: boolean;
  /** Current band — a change while armed force-disarms (never TX on a new band). */
  band?: string;
  onLogQso?: (state: QsoState) => void;
  /** Persisted defaults seeded into the controller (FT8 Settings page →
   *  Ft8Settings). These set the operator's starting slot / offset / hold /
   *  call-first; they never arm and never override an in-flight QSO. Applied at
   *  construction and re-applied once `seedReady` flips true while the controller
   *  is still idle (covers the controller being built before the settings store
   *  finished its async module-load hydrate). */
  seed?: {
    audioHz?: number;
    slot?: Slot;
    holdTxFreq?: boolean;
    callFirst?: boolean;
  };
  /** True once the FT8 Settings store has hydrated from the server, so the seed
   *  reflects the operator's saved defaults rather than the in-code fallbacks. */
  seedReady?: boolean;
  /** Live behaviour preferences (auto-sequence / disable-after-73 / no-reply
   *  limit / ack token). Applied live whenever they change. */
  behavior?: Ft8TxBehavior;
}

/**
 * Owns the per-slot RX→TX window timing: polls for each UTC slot boundary and,
 * `settleMs` after one is crossed, hands the just-ended slot's decodes (and its
 * parity) to `onWindow`. Extracted from the hook so the
 * boundary→settle→decodes→stage pipeline is unit-testable with fake timers (no
 * React renderer / extra test dep needed). Returns a disposer that clears every
 * timer it created.
 */
export function startFt8SlotDriver(opts: {
  slotMs: number;
  settleMs: number;
  getRows: () => readonly Ft8Row[];
  onWindow: (rows: Ft8Row[], senderSlot: Slot) => void;
}): () => void {
  const { slotMs, settleMs, getRows, onWindow } = opts;
  let lastSlot = slotIndexOf(Date.now(), slotMs);
  const pending: ReturnType<typeof setTimeout>[] = [];

  const tick = () => {
    const cur = slotIndexOf(Date.now(), slotMs);
    if (cur === lastSlot) return;
    const endedSlot = lastSlot; // the slot that just closed
    lastSlot = cur;
    const id = setTimeout(() => {
      onWindow(rowsForSlot(getRows(), endedSlot, slotMs), slotParity(endedSlot));
    }, settleMs);
    pending.push(id);
  };

  const interval = setInterval(tick, 250);
  return () => {
    clearInterval(interval);
    for (const id of pending) clearTimeout(id);
  };
}

/** Best-effort disarm of the backend keyer. Uses sendBeacon so it survives a tab
 *  close / page unload where an in-flight fetch would be killed. */
export function beaconDisarm(): void {
  try {
    const body = new Blob([JSON.stringify({ enabled: false })], { type: 'application/json' });
    navigator.sendBeacon?.(`${DIGITAL_PLUGIN_BASE}/ft8/tx/arm`, body);
  } catch {
    // sendBeacon unavailable / blocked — the backend watchdog is the backstop.
  }
}

/**
 * Owns a single Ft8TxController for the open FT8/FT4 workspace and drives it once
 * per slot. Returns a reactive view + bound actions for the TX-control cluster.
 */
export function useFt8TxRunner(opts: UseFt8TxRunnerOpts): Ft8TxRunnerView {
  const { myCall, myGrid, mode, active, band, onLogQso, seed, seedReady, behavior } = opts;

  // One controller for the lifetime of the workspace. Identity (call/grid) and
  // mode are pushed in via setters so an in-flight QSO survives a re-render.
  const ctrlRef = useRef<Ft8TxController | null>(null);
  if (ctrlRef.current === null) {
    ctrlRef.current = new Ft8TxController({
      myCall,
      myGrid4: myGrid,
      mode,
      onLogQso,
      audioHz: seed?.audioHz,
      ...behavior,
    });
    // Seed persisted defaults exactly once. Order matters: set the offset (via
    // the constructor above) BEFORE engaging HOLD, since setTxFreq is ignored
    // while hold is on. None of these arm — arming is explicit only.
    if (seed?.slot) ctrlRef.current.setTxSlot(seed.slot);
    if (seed?.callFirst) ctrlRef.current.setCallFirst(true);
    if (seed?.holdTxFreq) ctrlRef.current.setHoldTxFreq(true);
  }
  const ctrl = ctrlRef.current;

  const [view, setView] = useState(() => snapshot(ctrl));
  const sync = () => setView(snapshot(ctrl));

  // Live behaviour prefs (auto-sequence / disable-after-73 / no-reply / ack):
  // re-apply whenever any change, so a Settings edit takes effect mid-session.
  useEffect(() => {
    if (behavior) ctrl.applyBehavior(behavior);
    sync();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [
    behavior?.autoSequence,
    behavior?.disableTxAfter73,
    behavior?.noReplyLimit,
    behavior?.txAck,
  ]);

  // Seed-race recovery: if the controller was built before the settings store
  // hydrated, re-apply the persisted seed once it's ready (no-op unless idle, so
  // an in-flight QSO is never disturbed). Runs once per ready transition.
  const seededRef = useRef(false);
  useEffect(() => {
    if (!seedReady || seededRef.current) return;
    seededRef.current = true;
    if (seed) ctrl.applySeed(seed);
    sync();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [seedReady]);

  // Keep identity + mode current without resetting the live QSO.
  useEffect(() => {
    ctrl.setMode(mode);
    ctrl.setIdentity(myCall, myGrid ?? null);
    sync();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [mode, myCall, myGrid]);

  // Slot timer: detect each UTC slot boundary, then after the decoder settles,
  // feed the just-completed slot's decodes to the controller.
  useEffect(() => {
    if (!active) return;
    return startFt8SlotDriver({
      slotMs: slotMsFor(mode),
      settleMs: settleMsFor(mode),
      getRows: () => useFt8Store.getState().rows,
      onWindow: (rows, senderSlot) => {
        // Thread the measured SNR of the DX station so the report we send (and
        // log) is the real exchange, not a constant fallback.
        const measured = measuredDxSnr(rows, ctrl.getState());
        ctrl.onWindow(rows.map((r) => r.text), measured, senderSlot);
        sync();
      },
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [active, mode]);

  // Safety: force-disarm the backend keyer when the workspace unmounts or the tab
  // closes. Without this, navigating away while armed leaves the radio auto-
  // sequencing until only the watchdog (minutes later) catches it.
  useEffect(() => {
    window.addEventListener('pagehide', beaconDisarm);
    return () => {
      window.removeEventListener('pagehide', beaconDisarm);
      if (ctrl.getState().enableTx) ctrl.disableTx();
      beaconDisarm();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Safety: a band change while armed force-disarms — never keep auto-keying a
  // QSO sequence onto whatever band the operator just switched to.
  const prevBand = useRef(band);
  useEffect(() => {
    if (prevBand.current === band) return;
    prevBand.current = band;
    if (ctrl.getState().enableTx) {
      ctrl.disableTx();
      sync();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [band]);

  return {
    ...view,
    enableTx: () => {
      ctrl.enableTx();
      sync();
    },
    disableTx: () => {
      ctrl.disableTx();
      sync();
    },
    halt: () => {
      ctrl.halt();
      sync();
    },
    setTxSlot: (slot) => {
      ctrl.setTxSlot(slot);
      sync();
    },
    setHoldTxFreq: (hold) => {
      ctrl.setHoldTxFreq(hold);
      sync();
    },
    setTxFreq: (hz) => {
      ctrl.setTxFreq(hz);
      sync();
    },
    setCallFirst: (on) => {
      ctrl.setCallFirst(on);
      sync();
    },
    startCq: (opts) => {
      ctrl.startCq(opts);
      sync();
    },
    answerCq: (text, senderSlot) => {
      const ok = ctrl.answerCq(text, senderSlot);
      sync();
      return ok;
    },
    callStation: (text, senderSlot) => {
      const ok = ctrl.callStation(text, senderSlot);
      sync();
      return ok;
    },
    stageMacro: (message) => {
      ctrl.stageMacro(message);
      sync();
    },
    markLogged: () => {
      ctrl.markLogged();
      sync();
    },
  };
}

function snapshot(ctrl: Ft8TxController): {
  qso: QsoState;
  audioHz: number;
  callFirst: boolean;
  outgoing: string | null;
} {
  return {
    qso: ctrl.getState(),
    audioHz: ctrl.getAudioHz(),
    callFirst: ctrl.getCallFirst(),
    outgoing: ctrl.getOutgoing(),
  };
}
