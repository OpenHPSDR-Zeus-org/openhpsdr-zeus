// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// ft8-tx-controller — the QSO runner that bridges the PURE ft8-sequencer state
// machine to the backend keyer (Ft8TxService). ft8-sequencer stays pure (no I/O,
// no keying); this drives it: once per RX→TX window it gathers the just-ended
// slot's decodes, calls step(), and turns the StepResult into the backend POSTs
// (stage / arm / halt). The backend then keys at the next matching slot boundary.
//
// SAFETY: arming is EXPLICIT ONLY — enableTx() is the single path that sets
// state.enableTx and POSTs /arm {enabled:true}. Nothing here auto-arms on mount
// or workspace entry. disarmTx (signoff / no-reply-stop) and halt map straight
// to /arm {enabled:false} and /halt. A message is only staged when enableTx is
// true, so a disarmed controller never POSTs /tx.

import { parseFt8Message } from './ft8-message';
import { FT8_MAX_TX_OFFSET_HZ, FT8_MIN_OFFSET_HZ } from './ft8-passband';
import {
  answerCq as seqAnswerCq,
  currentOutgoing,
  halt as seqHalt,
  startCq as seqStartCq,
  slotOf,
  step,
  type AckToken,
  type DigitalQsoMode,
  type NewQsoOpts,
  type QsoState,
  type Slot,
} from './ft8-sequencer';

/** Behaviour preferences from the FT8 Settings page that change how the runner
 *  drives the sequencer. All live-applicable (no controller rebuild needed). */
export interface Ft8TxBehavior {
  /** Master auto-sequence. When off, the runner keeps the operator's selected
   *  message going while armed but never advances the QSO, auto-answers, logs,
   *  or auto-disarms — the operator drives every step by hand. */
  autoSequence?: boolean;
  /** Auto-disarm once a QSO completes (73 sent). When off, the keyer stays armed
   *  and idle after a completed QSO instead of disarming. */
  disableTxAfter73?: boolean;
  /** Caller no-reply give-up limit (0 = never). Maps to QsoState.noReplyLimit. */
  noReplyLimit?: number;
  /** Final ack token: RR73 (default) or RRR. */
  txAck?: AckToken;
}

export interface Ft8TxControllerOpts extends Ft8TxBehavior {
  myCall: string;
  myGrid4?: string | null;
  mode?: DigitalQsoMode;
  /** Initial TX audio offset (Hz). */
  audioHz?: number;
  /** Override fetch (tests). Defaults to global fetch. */
  fetchFn?: typeof fetch;
  /** Called once when a QSO should be logged (Team C's log endpoint). */
  onLogQso?: (state: QsoState) => void;
}

/** The QSO runner. Owns the live QsoState and the TX audio offset, and issues
 *  the backend stage/arm/halt POSTs as the sequencer advances. */
export class Ft8TxController {
  private state: QsoState;
  private audioHz: number;
  /** CALL 1ST: when armed and idle, auto-answer the first CQ heard. Off by
   *  default — auto-answer is an explicit operator opt-in. */
  private callFirst = false;
  /** Master auto-sequence. When false the runner drives a manual keyer (no
   *  state-machine progression, no auto-log, no auto-disarm). */
  private autoSequence = true;
  /** Auto-disarm after a completed QSO (73). When false the keyer stays armed. */
  private disableTxAfter73 = true;
  /** Caller no-reply give-up limit (0 = never). */
  private noReplyLimit = 0;
  /** Final ack token (RR73 / RRR). */
  private txAck: AckToken = 'RR73';
  private readonly doFetch: typeof fetch;
  private readonly onLogQso?: (state: QsoState) => void;

  constructor(opts: Ft8TxControllerOpts) {
    this.autoSequence = opts.autoSequence ?? true;
    this.disableTxAfter73 = opts.disableTxAfter73 ?? true;
    this.noReplyLimit = Math.max(0, opts.noReplyLimit ?? 0);
    this.txAck = opts.txAck ?? 'RR73';
    this.state = seqStartCq({
      myCall: opts.myCall,
      myGrid4: opts.myGrid4 ?? null,
      mode: opts.mode ?? 'FT8',
      txAck: this.txAck,
      noReplyLimit: this.noReplyLimit,
    });
    this.audioHz = opts.audioHz ?? 1500;
    this.doFetch = opts.fetchFn ?? ((...a: Parameters<typeof fetch>) => fetch(...a));
    this.onLogQso = opts.onLogQso;
  }

  /** Snapshot of the current logical QSO state (read-only view for the UI). */
  getState(): QsoState {
    return this.state;
  }

  getAudioHz(): number {
    return this.audioHz;
  }

  getCallFirst(): boolean {
    return this.callFirst;
  }

  /** The message the machine would transmit next (UI preview). */
  getOutgoing(): string | null {
    return currentOutgoing(this.state);
  }

  // ---- per-window driver --------------------------------------------------

  /**
   * Advance one RX→TX window. `decoded` are the raw decoded message texts heard
   * in the just-ended slot; `senderSlot` is the parity of that slot (so a
   * CALL-1ST auto-answer can reply in the opposite slot). Applies the
   * sequencer's decision: stages the next message (only while armed), logs once,
   * and disarms/halts as the machine dictates. Pure decision in, POSTs out.
   */
  onWindow(decoded: string[], measuredSnrOfDx?: number, senderSlot?: Slot): void {
    // MANUAL MODE (auto-sequence off): keep the operator's currently-selected
    // message going while armed, but never advance the QSO, auto-answer, log, or
    // disarm on our own — every step is an explicit operator action.
    if (!this.autoSequence) {
      if (this.state.enableTx) {
        const msg = currentOutgoing(this.state);
        if (msg != null) void this.postStage(msg);
      }
      return;
    }

    // CALL 1ST: while armed and idle (calling CQ, no DX latched yet), pounce on
    // the first decoded CQ. Requires the just-ended slot's parity so we answer
    // in the opposite slot. Explicit opt-in — never auto-engages.
    if (
      this.callFirst &&
      this.state.enableTx &&
      this.state.progress === 'calling' &&
      !this.state.dxCall &&
      senderSlot
    ) {
      const cqText = decoded.find((t) => {
        const m = parseFt8Message(t, this.state.myCall);
        return m.kind === 'cq' && !!m.deCall;
      });
      if (cqText && this.answerCq(cqText, senderSlot)) {
        const msg = currentOutgoing(this.state);
        if (msg != null) void this.postStage(msg);
        return;
      }
    }

    const res =
      measuredSnrOfDx === undefined
        ? step(this.state, decoded)
        : step(this.state, decoded, { measuredSnrOfDx });
    this.state = res.next;

    if (res.outgoing != null && this.state.enableTx) {
      void this.postStage(res.outgoing);
    }
    if (res.logQso) {
      this.onLogQso?.(this.state);
    }
    if (res.halt) {
      // Halts (operator abort / no-reply limit) ALWAYS disarm, regardless of the
      // disable-after-73 preference.
      void this.postHalt();
    } else if (res.disarmTx) {
      // A completion disarm (no halt). Honour "Disable TX after 73": when the
      // operator turned it off, keep the keyer armed and idle instead of
      // dropping it, so they can immediately work the next station.
      if (this.disableTxAfter73) {
        void this.postArm(false);
      } else {
        this.state = { ...this.state, enableTx: true };
      }
    }
  }

  // ---- operator actions ---------------------------------------------------

  /** ENABLE-TX: the only path that arms. Sets enableTx, POSTs /arm, and stages
   *  the current message so the keyer has something to key at the next matching
   *  slot (CQ when calling, the QSO reply when answering). */
  enableTx(): void {
    this.state = { ...this.state, enableTx: true };
    void this.postArm(true);
    const msg = currentOutgoing(this.state);
    if (msg != null) void this.postStage(msg);
  }

  /** CALL 1ST toggle: auto-answer the first CQ while armed and idle. */
  setCallFirst(on: boolean): void {
    this.callFirst = on;
  }

  /** Apply the live FT8 Settings behaviour preferences. Safe to call any time —
   *  noReplyLimit / txAck also update the in-flight QSO state so a mid-session
   *  edit takes effect on the next decision, not only on the next new QSO. */
  applyBehavior(b: Ft8TxBehavior): void {
    if (b.autoSequence !== undefined) this.autoSequence = b.autoSequence;
    if (b.disableTxAfter73 !== undefined) this.disableTxAfter73 = b.disableTxAfter73;
    if (b.noReplyLimit !== undefined) {
      this.noReplyLimit = Math.max(0, b.noReplyLimit);
      this.state = { ...this.state, noReplyLimit: this.noReplyLimit };
    }
    if (b.txAck !== undefined) {
      this.txAck = b.txAck;
      this.state = { ...this.state, txAck: this.txAck };
    }
  }

  /** Apply the persisted seed (slot / offset / hold / call-first) — used to
   *  recover when the controller was constructed before the FT8 Settings store
   *  finished hydrating. Only applied while idle (calling, no DX, disarmed) so it
   *  can never disturb an in-flight QSO. */
  applySeed(seed: { audioHz?: number; slot?: Slot; holdTxFreq?: boolean; callFirst?: boolean }): void {
    const idle =
      this.state.progress === 'calling' && !this.state.dxCall && !this.state.enableTx;
    if (!idle) return;
    // Offset BEFORE hold (setTxFreq is ignored while hold is engaged); set hold
    // directly here since it isn't gated.
    if (seed.audioHz !== undefined && Number.isFinite(seed.audioHz)) {
      this.audioHz = Math.min(FT8_MAX_TX_OFFSET_HZ, Math.max(FT8_MIN_OFFSET_HZ, seed.audioHz));
    }
    if (seed.slot) this.state = { ...this.state, txSlot: seed.slot };
    if (seed.holdTxFreq !== undefined) this.state = { ...this.state, holdTxFreq: seed.holdTxFreq };
    if (seed.callFirst !== undefined) this.callFirst = seed.callFirst;
  }

  /** Latch the live QSO as logged (the manual LOG QSO path). Once set, the
   *  sequencer's auto-log guard (`!state.logged`) can never fire onLogQso for the
   *  same QSO, so a manual log followed by sequence completion logs exactly once. */
  markLogged(): void {
    if (!this.state.logged) this.state = { ...this.state, logged: true };
  }

  /** Operator disable (disable-after-73 also routes here). */
  disableTx(): void {
    this.state = { ...this.state, enableTx: false };
    void this.postArm(false);
  }

  /** Operator Halt: abort, disarm, reset the machine, POST /halt. */
  halt(): void {
    const res = seqHalt(this.state);
    this.state = res.next;
    void this.postHalt();
  }

  /** TX EVEN / ODD. While armed, re-stage the current message immediately so the
   *  slot flip reaches the keyer this slot instead of waiting for the next
   *  per-window restage. */
  setTxSlot(slot: Slot): void {
    this.state = { ...this.state, txSlot: slot };
    this.restageIfArmed();
  }

  /** FT8 ↔ FT4 (slot timing differs; sequencer text is identical). */
  setMode(mode: DigitalQsoMode): void {
    this.state = { ...this.state, mode };
  }

  /** Operator identity (call / grid) — kept current so message generation uses
   *  the latest values even if the operator fills them in after opening. */
  setIdentity(myCall: string, myGrid4: string | null): void {
    this.state = {
      ...this.state,
      myCall: myCall.toUpperCase().trim(),
      myGrid4: myGrid4 ? myGrid4.toUpperCase().trim() : null,
    };
  }

  /** HOLD TX FREQ toggle. */
  setHoldTxFreq(hold: boolean): void {
    this.state = { ...this.state, holdTxFreq: hold };
  }

  /** Waterfall click / OFFSET input → TX offset. Ignored while HOLD TX FREQ is
   *  engaged. Clamped to the TX-offset range (the single source of truth shared
   *  with the waterfall click and the OFFSET input) so click, type, and the
   *  value POSTed to the keyer always agree. While armed, re-stage immediately so
   *  the new offset reaches the keyer this slot. */
  setTxFreq(hz: number): void {
    if (this.state.holdTxFreq) return;
    const clamped = Number.isFinite(hz)
      ? Math.min(FT8_MAX_TX_OFFSET_HZ, Math.max(FT8_MIN_OFFSET_HZ, hz))
      : FT8_MIN_OFFSET_HZ;
    this.audioHz = clamped;
    this.restageIfArmed();
  }

  /** Re-POST the currently-staged message when armed, so an operator adjustment
   *  (offset / slot) propagates within the same slot instead of waiting for the
   *  next per-window restage. No-op when disarmed (a disarmed controller never
   *  POSTs /tx). */
  private restageIfArmed(): void {
    if (!this.state.enableTx) return;
    const msg = currentOutgoing(this.state);
    if (msg != null) void this.postStage(msg);
  }

  /** Start calling CQ. `opts.cqDirective` sets the CQ directive (CQ vs CQ DX). */
  startCq(opts?: Partial<NewQsoOpts>): void {
    this.state = seqStartCq({
      myCall: this.state.myCall,
      myGrid4: this.state.myGrid4,
      mode: this.state.mode,
      txAck: this.txAck,
      noReplyLimit: this.noReplyLimit,
      ...opts,
    });
  }

  /** Call an arbitrary decoded station (click a decode row). Treats any decode
   *  with an identifiable callsign as a station to call — we open with our grid
   *  reply (Tx1) in the slot opposite the one it was heard in. Returns false if
   *  no callsign could be parsed. */
  callStation(decodeText: string, senderSlot: Slot): boolean {
    const parsed = parseFt8Message(decodeText, this.state.myCall);
    if (!parsed.deCall) return false;
    const next = seqAnswerCq(
      {
        myCall: this.state.myCall,
        myGrid4: this.state.myGrid4,
        mode: this.state.mode,
        txAck: this.txAck,
        noReplyLimit: this.noReplyLimit,
      },
      { ...parsed, kind: 'cq' },
      senderSlot,
    );
    if (!next) return false;
    this.state = next;
    return true;
  }

  /** Answer a decoded CQ (double-click a decode). Returns true if it was a CQ. */
  answerCq(decodeText: string, cqSenderSlot: Slot): boolean {
    const msg = parseFt8Message(decodeText, this.state.myCall);
    const next = seqAnswerCq(
      {
        myCall: this.state.myCall,
        myGrid4: this.state.myGrid4,
        mode: this.state.mode,
        txAck: this.txAck,
        noReplyLimit: this.noReplyLimit,
      },
      msg,
      cqSenderSlot,
    );
    if (!next) return false;
    this.state = next;
    return true;
  }

  /** Stage an arbitrary macro message (CQ / CQ DX / grid / RR73 / 73 buttons).
   *  The backend will only key it if armed. */
  stageMacro(message: string): void {
    void this.postStage(message);
  }

  /** Slot helper for the caller's window ticker (UTC seconds-into-minute). */
  slotForSecond(secondsIntoMinute: number): Slot {
    return slotOf(secondsIntoMinute, this.state.mode);
  }

  // ---- backend POSTs ------------------------------------------------------

  private postStage(message: string): Promise<unknown> {
    return this.post('/api/ft8/tx', {
      message,
      audioHz: this.audioHz,
      slot: this.state.txSlot,
      mode: this.state.mode,
    });
  }

  private postArm(enabled: boolean): Promise<unknown> {
    return this.post('/api/ft8/tx/arm', { enabled });
  }

  private postHalt(): Promise<unknown> {
    return this.post('/api/ft8/tx/halt', {});
  }

  private async post(url: string, body: unknown): Promise<unknown> {
    try {
      await this.doFetch(url, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
      });
    } catch {
      // The keyer's own watchdog + arm-state is authoritative; a dropped POST
      // just means this window doesn't change the backend. Swallow.
    }
    return undefined;
  }
}
