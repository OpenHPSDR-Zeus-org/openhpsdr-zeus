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
import {
  answerCq as seqAnswerCq,
  currentOutgoing,
  halt as seqHalt,
  startCq as seqStartCq,
  slotOf,
  step,
  type DigitalQsoMode,
  type NewQsoOpts,
  type QsoState,
  type Slot,
} from './ft8-sequencer';

export interface Ft8TxControllerOpts {
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
  private readonly doFetch: typeof fetch;
  private readonly onLogQso?: (state: QsoState) => void;

  constructor(opts: Ft8TxControllerOpts) {
    this.state = seqStartCq({
      myCall: opts.myCall,
      myGrid4: opts.myGrid4 ?? null,
      mode: opts.mode ?? 'FT8',
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
      void this.postHalt();
    } else if (res.disarmTx) {
      void this.postArm(false);
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

  /** TX EVEN / ODD. */
  setTxSlot(slot: Slot): void {
    this.state = { ...this.state, txSlot: slot };
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

  /** Waterfall click → TX offset. Ignored while HOLD TX FREQ is engaged. */
  setTxFreq(hz: number): void {
    if (this.state.holdTxFreq) return;
    this.audioHz = hz;
  }

  /** Start calling CQ (CALL 1ST). */
  startCq(opts?: Partial<NewQsoOpts>): void {
    this.state = seqStartCq({
      myCall: this.state.myCall,
      myGrid4: this.state.myGrid4,
      mode: this.state.mode,
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
      { myCall: this.state.myCall, myGrid4: this.state.myGrid4, mode: this.state.mode },
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
