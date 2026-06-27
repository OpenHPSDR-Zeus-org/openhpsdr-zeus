// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// FT8/FT4 QSO auto-sequence engine — the WSJT-X/JTDX handshake as a PURE,
// deterministic state machine. `step(state, decodedMessages, myCall)` returns
// the next state, the message to queue, and whether to log — no timers, no I/O,
// no radio keying. The caller supplies slot boundaries and decoded text; this
// just decides what the operator's station should say next.
//
// SAFETY / SCOPE: this module never transmits. It drives the workspace QSO
// panel (showing the staged messages and live sequence progress) and click-to-
// call staging. The actual on-air keyer — synthesizing TX IQ and keying the PA
// through TxService — is a separate, BENCH-GATED backend port of this same
// table and is intentionally NOT wired here.
//
// Canonical 6-message QSO (WSJT-X User Guide), alternating stations:
//   CQ K1ABC FN42        (caller, Tx6)
//   K1ABC G0XYZ IO91     (answerer, Tx1 — grid)
//   G0XYZ K1ABC -19      (caller, Tx2 — report = SNR caller measured of answerer)
//   K1ABC G0XYZ R-22     (answerer, Tx3 — roger + answerer's SNR of caller)
//   G0XYZ K1ABC RR73     (caller, Tx4 — ack, RRR or RR73)
//   K1ABC G0XYZ 73       (answerer, Tx5 — signoff)
// Each side uses only HALF the slots: caller = Tx6/Tx2/Tx4, answerer = Tx1/Tx3/Tx5.

import { parseFt8Message, type Ft8Message } from './ft8-message';

export type Role = 'cq-caller' | 'answerer';
export type QsoProgress =
  | 'calling'
  | 'replying'
  | 'report'
  | 'roger-report'
  | 'rogers'
  | 'signoff'
  | 'done';
export type Slot = 'even' | 'odd';
export type DigitalQsoMode = 'FT8' | 'FT4';
export type AckToken = 'RRR' | 'RR73';
export type HaltReason = 'operator' | 'no-reply-limit';

export interface QsoState {
  progress: QsoProgress;
  role: Role;
  mode: DigitalQsoMode;
  myCall: string;
  myGrid4: string | null;
  dxCall: string | null;
  dxGrid4: string | null;
  /** Last numeric report HE sent us (logging only). */
  rcvdReportFromHim: number | null;
  /** SNR WE measured of HIM, latched at first send so re-sends are stable. */
  sentReportToHim: number | null;
  txAck: AckToken;
  holdTxFreq: boolean;
  txSlot: Slot;
  enableTx: boolean;
  noReplyCount: number;
  /** 0 = never auto-give-up (WSJT-X default); >0 = JTDX-style cap. */
  noReplyLimit: number;
  singleShot: boolean;
  /** Latches once logQso has fired, so a QSO is logged exactly once. */
  logged: boolean;
}

export interface StepResult {
  next: QsoState;
  /** Message to queue for the next own slot (null = nothing to send). */
  outgoing: string | null;
  /** Fire the Log-QSO action exactly when true. */
  logQso: boolean;
  /** Clear enableTx after this slot. */
  disarmTx: boolean;
  halt: HaltReason | null;
}

// ---- message generation ---------------------------------------------------

/** Format an SNR as a signed, 2-digit report (e.g. -19, +03). */
export function fmtSnr(snr: number): string {
  const sign = snr < 0 ? '-' : '+';
  return sign + Math.abs(Math.trunc(snr)).toString().padStart(2, '0');
}

export function genCq(myCall: string, myGrid4?: string | null, directive?: string | null): string {
  const head = directive ? `CQ ${directive} ${myCall}` : `CQ ${myCall}`;
  return myGrid4 ? `${head} ${myGrid4}` : head;
}
export const genTx1 = (his: string, mine: string, myGrid4: string) => `${his} ${mine} ${myGrid4}`;
export const genTx2 = (his: string, mine: string, snr: number) => `${his} ${mine} ${fmtSnr(snr)}`;
export const genTx3 = (his: string, mine: string, snr: number) => `${his} ${mine} R${fmtSnr(snr)}`;
export const genTx4 = (his: string, mine: string, ack: AckToken) => `${his} ${mine} ${ack}`;
export const genTx5 = (his: string, mine: string) => `${his} ${mine} 73`;

export const opposite = (slot: Slot): Slot => (slot === 'even' ? 'odd' : 'even');

/**
 * Which slot a transmission starting `secondsIntoMinute` into the minute
 * occupies. FT8 periods start at :00/:15/:30/:45 (even = :00/:30); FT4 at 7.5 s
 * boundaries (even = the 0th, 2nd, 4th, 6th sub-slots).
 */
export function slotOf(secondsIntoMinute: number, mode: DigitalQsoMode): Slot {
  if (mode === 'FT4') {
    const idx = Math.floor((secondsIntoMinute % 60) / 7.5);
    return idx % 2 === 0 ? 'even' : 'odd';
  }
  const idx = Math.floor((secondsIntoMinute % 60) / 15);
  return idx % 2 === 0 ? 'even' : 'odd';
}

// ---- state construction ---------------------------------------------------

export interface NewQsoOpts {
  myCall: string;
  myGrid4?: string | null;
  mode?: DigitalQsoMode;
  txAck?: AckToken;
  holdTxFreq?: boolean;
  noReplyLimit?: number;
  singleShot?: boolean;
}

function baseState(opts: NewQsoOpts): QsoState {
  return {
    progress: 'calling',
    role: 'cq-caller',
    mode: opts.mode ?? 'FT8',
    myCall: opts.myCall.toUpperCase(),
    myGrid4: opts.myGrid4 ?? null,
    dxCall: null,
    dxGrid4: null,
    rcvdReportFromHim: null,
    sentReportToHim: null,
    txAck: opts.txAck ?? 'RR73',
    holdTxFreq: opts.holdTxFreq ?? true,
    txSlot: 'even',
    enableTx: false,
    noReplyCount: 0,
    noReplyLimit: opts.noReplyLimit ?? 0,
    singleShot: opts.singleShot ?? false,
    logged: false,
  };
}

/** Begin calling CQ. Queues Tx6; arming Tx is a separate explicit action. */
export function startCq(opts: NewQsoOpts): QsoState {
  return { ...baseState(opts), role: 'cq-caller', progress: 'calling' };
}

/**
 * Answer a decoded CQ (the click-to-call bootstrap). Copies his call/grid, sets
 * our slot opposite his, and queues Tx1. `cqSenderSlot` is the slot the CQ was
 * heard in. Returns null if the message isn't a CQ with an identifiable call.
 */
export function answerCq(
  opts: NewQsoOpts,
  cq: Ft8Message,
  cqSenderSlot: Slot,
): QsoState | null {
  if (cq.kind !== 'cq' || !cq.deCall) return null;
  return {
    ...baseState(opts),
    role: 'answerer',
    progress: 'replying',
    dxCall: cq.deCall,
    dxGrid4: cq.grid,
    txSlot: opposite(cqSenderSlot),
  };
}

/** The message this state should be (re-)transmitting right now. */
export function currentOutgoing(s: QsoState): string | null {
  const his = s.dxCall ?? '';
  switch (s.progress) {
    case 'calling':
      return genCq(s.myCall, s.myGrid4);
    case 'replying':
      return genTx1(his, s.myCall, s.myGrid4 ?? '');
    case 'report':
      return s.sentReportToHim != null ? genTx2(his, s.myCall, s.sentReportToHim) : null;
    case 'roger-report':
      return s.sentReportToHim != null ? genTx3(his, s.myCall, s.sentReportToHim) : null;
    case 'rogers':
      return genTx4(his, s.myCall, s.txAck);
    case 'signoff':
      return genTx5(his, s.myCall);
    case 'done':
      return null;
  }
}

// ---- the step function ----------------------------------------------------

const ANSWERER_RANK: Record<string, number> = { 'roger-report': 2, signoff: 3 };
const CALLER_RANK: Record<string, number> = { report: 2, rogers: 3, done: 4 };

/** Map a decoded message addressed to us to the progress it implies, per role. */
function impliedProgress(role: Role, m: Ft8Message): QsoProgress | null {
  if (role === 'answerer') {
    if (m.kind === 'report' || m.kind === 'rreport') return 'roger-report';
    if (m.kind === 'rr73' || m.kind === 'rrr') return 'signoff';
    return null;
  }
  // cq-caller
  if (m.kind === 'grid' || m.kind === 'report' || m.kind === 'rreport') {
    // grid/report answers our CQ; rreport advances from report -> rogers
    return m.kind === 'rreport' ? 'rogers' : 'report';
  }
  if (m.kind === '73' || m.kind === 'rr73') return 'done';
  return null;
}

function rankOf(role: Role, p: QsoProgress): number {
  return (role === 'answerer' ? ANSWERER_RANK[p] : CALLER_RANK[p]) ?? 0;
}

export interface StepOpts {
  /** SNR (dB) we measured of the DX station this window, for report messages. */
  measuredSnrOfDx?: number;
}

/**
 * Advance the QSO by one Rx→Tx window. `decoded` are the raw decoded message
 * texts heard in the just-ended window. Pure: same inputs → same output.
 */
export function step(state: QsoState, decoded: string[], opts: StepOpts = {}): StepResult {
  const noChange = (): StepResult => ({
    next: state,
    outgoing: currentOutgoing(state),
    logQso: false,
    disarmTx: false,
    halt: null,
  });

  if (state.progress === 'done') return { ...noChange(), disarmTx: true };
  if (!state.enableTx) return noChange();

  // Signoff is a one-shot: once we've queued/sent our 73, the QSO is over.
  // (Re-CQ after a caller's Done is left to an explicit operator action — the
  // engine never auto-re-arms transmit.)
  if (state.progress === 'signoff') {
    return {
      next: { ...state, progress: 'done', enableTx: false },
      outgoing: null,
      logQso: false,
      disarmTx: true,
      halt: null,
    };
  }

  // Messages addressed to us, from the DX station if we already know it.
  const mine = decoded
    .map((t) => parseFt8Message(t, state.myCall))
    .filter((m) => m.isCallingMe && (state.dxCall == null || m.deCall === state.dxCall));

  // Pick the most-advanced valid event for our role.
  let bestMsg: Ft8Message | null = null;
  let bestRank = 0;
  for (const m of mine) {
    const p = impliedProgress(state.role, m);
    if (!p) continue;
    const r = rankOf(state.role, p);
    if (r > bestRank) {
      bestRank = r;
      bestMsg = m;
    }
  }

  // Caller in 'calling' selects the first answerer (WSJT-X "CQ: First").
  if (state.role === 'cq-caller' && state.progress === 'calling') {
    const answer = mine.find(
      (m) => m.kind === 'grid' || m.kind === 'report' || m.kind === 'rreport',
    );
    if (answer && answer.deCall) {
      const snr = opts.measuredSnrOfDx ?? answer.reportDb ?? -10;
      const next: QsoState = {
        ...state,
        dxCall: answer.deCall,
        dxGrid4: answer.grid ?? state.dxGrid4,
        progress: 'report',
        sentReportToHim: snr,
        noReplyCount: 0,
      };
      return { next, outgoing: currentOutgoing(next), logQso: false, disarmTx: false, halt: null };
    }
    // Still calling; just re-queue CQ (no noReply penalty while calling).
    return noChange();
  }

  if (!bestMsg) {
    // No advancing reply — re-queue current message, count the miss.
    const noReplyCount = state.noReplyCount + 1;
    if (state.noReplyLimit > 0 && noReplyCount >= state.noReplyLimit) {
      return {
        next: { ...state, noReplyCount, enableTx: false },
        outgoing: null,
        logQso: false,
        disarmTx: true,
        halt: 'no-reply-limit',
      };
    }
    return {
      next: { ...state, noReplyCount },
      outgoing: currentOutgoing(state),
      logQso: false,
      disarmTx: false,
      halt: null,
    };
  }

  return applyEvent(state, bestMsg, opts);
}

function applyEvent(state: QsoState, m: Ft8Message, opts: StepOpts): StepResult {
  const snr = opts.measuredSnrOfDx ?? -10;

  if (state.role === 'answerer') {
    // Replying -> RogerReport on receiving a report; -> Signoff on ack.
    if (state.progress === 'replying') {
      if (m.kind === 'report' || m.kind === 'rreport') {
        const next: QsoState = {
          ...state,
          progress: 'roger-report',
          rcvdReportFromHim: m.reportDb ?? state.rcvdReportFromHim,
          sentReportToHim: state.sentReportToHim ?? snr,
          noReplyCount: 0,
        };
        return { next, outgoing: currentOutgoing(next), logQso: false, disarmTx: false, halt: null };
      }
      if (m.kind === 'rr73' || m.kind === 'rrr') {
        return toSignoff(state); // skip-forward
      }
    }
    if (state.progress === 'roger-report' && (m.kind === 'rr73' || m.kind === 'rrr')) {
      return toSignoff(state);
    }
  } else {
    // cq-caller. Report -> Rogers on R+report; Rogers -> Done on 73/RR73.
    if (state.progress === 'report' && m.kind === 'rreport') {
      const next: QsoState = {
        ...state,
        progress: 'rogers',
        rcvdReportFromHim: m.reportDb ?? state.rcvdReportFromHim,
        noReplyCount: 0,
      };
      // With RR73 we are confident the QSO is complete the moment we send it.
      const logNow = state.txAck === 'RR73' && !state.logged;
      return {
        next: { ...next, logged: next.logged || logNow },
        outgoing: currentOutgoing(next),
        logQso: logNow,
        disarmTx: false,
        halt: null,
      };
    }
    if (state.progress === 'rogers' && (m.kind === '73' || m.kind === 'rr73')) {
      const logNow = !state.logged;
      const next: QsoState = {
        ...state,
        progress: 'done',
        logged: true,
        enableTx: state.singleShot ? false : state.enableTx,
      };
      return { next, outgoing: null, logQso: logNow, disarmTx: true, halt: null };
    }
  }

  // Event didn't advance us (e.g. he repeated an earlier slot): re-send current.
  return {
    next: { ...state, noReplyCount: state.noReplyCount + 1 },
    outgoing: currentOutgoing(state),
    logQso: false,
    disarmTx: false,
    halt: null,
  };
}

function toSignoff(state: QsoState): StepResult {
  // Answerer logs the moment it receives RRR/RR73.
  const logNow = !state.logged;
  const next: QsoState = { ...state, progress: 'signoff', logged: true, noReplyCount: 0 };
  return { next, outgoing: currentOutgoing(next), logQso: logNow, disarmTx: false, halt: null };
}

/** Operator Halt: abort, disarm, reset the logical machine to calling. */
export function halt(state: QsoState): StepResult {
  return {
    next: { ...state, progress: 'calling', enableTx: false, noReplyCount: 0 },
    outgoing: null,
    logQso: false,
    disarmTx: true,
    halt: 'operator',
  };
}
