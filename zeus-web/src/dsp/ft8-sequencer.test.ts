// SPDX-License-Identifier: GPL-2.0-or-later
import { describe, expect, it } from 'vitest';
import { parseFt8Message } from './ft8-message';
import {
  answerCq,
  currentOutgoing,
  fmtSnr,
  genCq,
  halt,
  opposite,
  slotOf,
  startCq,
  step,
  type QsoState,
} from './ft8-sequencer';

const arm = (s: QsoState): QsoState => ({ ...s, enableTx: true });

describe('message generation', () => {
  it('formats SNR as signed 2-digit', () => {
    expect(fmtSnr(-19)).toBe('-19');
    expect(fmtSnr(3)).toBe('+03');
    expect(fmtSnr(0)).toBe('+00');
    expect(fmtSnr(-1)).toBe('-01');
  });
  it('generates CQ with and without grid/directive', () => {
    expect(genCq('K1ABC', 'FN42')).toBe('CQ K1ABC FN42');
    expect(genCq('K1ABC')).toBe('CQ K1ABC');
    expect(genCq('K1ABC', 'FN42', 'DX')).toBe('CQ DX K1ABC FN42');
  });
});

describe('slot selection', () => {
  it('opposite flips', () => {
    expect(opposite('even')).toBe('odd');
    expect(opposite('odd')).toBe('even');
  });
  it('FT8 even at :00/:30, odd at :15/:45', () => {
    expect(slotOf(0, 'FT8')).toBe('even');
    expect(slotOf(15, 'FT8')).toBe('odd');
    expect(slotOf(30, 'FT8')).toBe('even');
    expect(slotOf(45, 'FT8')).toBe('odd');
  });
  it('FT4 alternates every 7.5 s', () => {
    expect(slotOf(0, 'FT4')).toBe('even');
    expect(slotOf(7.5, 'FT4')).toBe('odd');
    expect(slotOf(15, 'FT4')).toBe('even');
  });
});

describe('answerer bootstrap', () => {
  it('answers a CQ in the opposite slot and queues Tx1', () => {
    const cq = parseFt8Message('CQ K1ABC FN42');
    const s = answerCq({ myCall: 'G0XYZ', myGrid4: 'IO91' }, cq, 'even');
    expect(s).not.toBeNull();
    expect(s!.role).toBe('answerer');
    expect(s!.progress).toBe('replying');
    expect(s!.dxCall).toBe('K1ABC');
    expect(s!.txSlot).toBe('odd');
    expect(currentOutgoing(s!)).toBe('K1ABC G0XYZ IO91');
  });
  it('refuses a non-CQ', () => {
    expect(answerCq({ myCall: 'G0XYZ' }, parseFt8Message('K1ABC G0XYZ -19'), 'even')).toBeNull();
  });
});

describe('full answerer QSO', () => {
  it('walks replying → roger-report → signoff → done, logging once', () => {
    let s = arm(answerCq({ myCall: 'G0XYZ', myGrid4: 'IO91' }, parseFt8Message('CQ K1ABC FN42'), 'even')!);
    expect(currentOutgoing(s)).toBe('K1ABC G0XYZ IO91');

    // Receive his report -> send R + our SNR of him.
    let r = step(s, ['G0XYZ K1ABC -19'], { measuredSnrOfDx: -22 });
    expect(r.next.progress).toBe('roger-report');
    expect(r.outgoing).toBe('K1ABC G0XYZ R-22');
    expect(r.logQso).toBe(false);
    s = r.next;

    // Receive RR73 -> log + send 73.
    r = step(s, ['G0XYZ K1ABC RR73']);
    expect(r.next.progress).toBe('signoff');
    expect(r.logQso).toBe(true);
    expect(r.outgoing).toBe('K1ABC G0XYZ 73');
    s = r.next;

    // Our 73 slot completes -> done, disarmed, no second log.
    r = step(s, []);
    expect(r.next.progress).toBe('done');
    expect(r.disarmTx).toBe(true);
    expect(r.logQso).toBe(false);
  });

  it('skips forward if his bare report was missed and we hear RR73', () => {
    const s = arm(answerCq({ myCall: 'G0XYZ', myGrid4: 'IO91' }, parseFt8Message('CQ K1ABC FN42'), 'even')!);
    const r = step(s, ['G0XYZ K1ABC RR73']);
    expect(r.next.progress).toBe('signoff');
    expect(r.logQso).toBe(true);
  });
});

describe('full caller QSO', () => {
  it('walks calling → report → rogers → done with RR73 logging once', () => {
    let s = arm(startCq({ myCall: 'K1ABC', myGrid4: 'FN42', txAck: 'RR73' }));
    expect(currentOutgoing(s)).toBe('CQ K1ABC FN42');

    // Answerer replies with grid -> we pick him, send report.
    let r = step(s, ['K1ABC G0XYZ IO91'], { measuredSnrOfDx: -19 });
    expect(r.next.progress).toBe('report');
    expect(r.next.dxCall).toBe('G0XYZ');
    expect(r.outgoing).toBe('G0XYZ K1ABC -19');
    s = r.next;

    // R-report -> we send RR73 and log now (confident).
    r = step(s, ['K1ABC G0XYZ R-22']);
    expect(r.next.progress).toBe('rogers');
    expect(r.outgoing).toBe('G0XYZ K1ABC RR73');
    expect(r.logQso).toBe(true);
    s = r.next;

    // His 73 closes; no double log.
    r = step(s, ['K1ABC G0XYZ 73']);
    expect(r.next.progress).toBe('done');
    expect(r.logQso).toBe(false);
    expect(r.disarmTx).toBe(true);
  });

  it('with RRR ack, logs on receiving his 73 (not earlier)', () => {
    let s = arm(startCq({ myCall: 'K1ABC', myGrid4: 'FN42', txAck: 'RRR' }));
    s = step(s, ['K1ABC G0XYZ IO91'], { measuredSnrOfDx: -19 }).next;
    let r = step(s, ['K1ABC G0XYZ R-22']);
    expect(r.outgoing).toBe('G0XYZ K1ABC RRR');
    expect(r.logQso).toBe(false);
    s = r.next;
    r = step(s, ['K1ABC G0XYZ 73']);
    expect(r.logQso).toBe(true);
    expect(r.next.progress).toBe('done');
  });

  it('ignores answers addressed to other stations', () => {
    const s = arm(startCq({ myCall: 'K1ABC', myGrid4: 'FN42' }));
    const r = step(s, ['W1AW G0XYZ IO91']);
    expect(r.next.progress).toBe('calling');
  });
});

describe('repeats, no-reply, halt, arming', () => {
  it('re-queues and counts a missed reply', () => {
    const s = arm(answerCq({ myCall: 'G0XYZ', myGrid4: 'IO91' }, parseFt8Message('CQ K1ABC FN42'), 'even')!);
    const r = step(s, []);
    expect(r.next.progress).toBe('replying');
    expect(r.next.noReplyCount).toBe(1);
    expect(r.outgoing).toBe('K1ABC G0XYZ IO91');
  });

  it('halts after the no-reply limit', () => {
    let s = arm(answerCq({ myCall: 'G0XYZ', myGrid4: 'IO91', noReplyLimit: 2 }, parseFt8Message('CQ K1ABC FN42'), 'even')!);
    s = step(s, []).next;
    const r = step(s, []);
    expect(r.halt).toBe('no-reply-limit');
    expect(r.disarmTx).toBe(true);
    expect(r.next.enableTx).toBe(false);
  });

  it('does nothing when Tx is not armed', () => {
    const s = answerCq({ myCall: 'G0XYZ', myGrid4: 'IO91' }, parseFt8Message('CQ K1ABC FN42'), 'even')!;
    const r = step(s, ['G0XYZ K1ABC -19']);
    expect(r.next.progress).toBe('replying'); // unchanged
  });

  it('operator halt resets to calling and disarms', () => {
    const s = arm(startCq({ myCall: 'K1ABC', myGrid4: 'FN42' }));
    const r = halt(s);
    expect(r.next.progress).toBe('calling');
    expect(r.next.enableTx).toBe(false);
    expect(r.halt).toBe('operator');
  });
});
