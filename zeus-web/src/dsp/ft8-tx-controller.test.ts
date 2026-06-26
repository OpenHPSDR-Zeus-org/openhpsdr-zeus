// SPDX-License-Identifier: GPL-2.0-or-later
import { describe, expect, it, vi } from 'vitest';
import { Ft8TxController } from './ft8-tx-controller';

interface PostCall {
  url: string;
  body: Record<string, unknown>;
}

function makeFetch(): { fn: typeof fetch; calls: PostCall[] } {
  const calls: PostCall[] = [];
  const fn = vi.fn(async (url: string | URL | Request, init?: RequestInit) => {
    const body = init?.body ? (JSON.parse(String(init.body)) as Record<string, unknown>) : {};
    calls.push({ url: String(url), body });
    return {} as Response;
  }) as unknown as typeof fetch;
  return { fn, calls };
}

const tx = (calls: PostCall[]) => calls.filter((c) => c.url === '/api/ft8/tx');
const arm = (calls: PostCall[]) => calls.filter((c) => c.url === '/api/ft8/tx/arm');
const halt = (calls: PostCall[]) => calls.filter((c) => c.url === '/api/ft8/tx/halt');

describe('Ft8TxController', () => {
  it('never POSTs /tx while disarmed', () => {
    const { fn, calls } = makeFetch();
    const ctrl = new Ft8TxController({ myCall: 'KB2UKA', myGrid4: 'FN12', fetchFn: fn });
    // Not enabled: a decode window must not stage anything.
    ctrl.onWindow(['CQ K1ABC FN42']);
    ctrl.onWindow([]);
    expect(tx(calls)).toHaveLength(0);
  });

  it('arms on enableTx and stages the answerer sequence', () => {
    const { fn, calls } = makeFetch();
    const logged: string[] = [];
    const ctrl = new Ft8TxController({
      myCall: 'KB2UKA',
      myGrid4: 'FN12',
      fetchFn: fn,
      onLogQso: (s) => logged.push(s.dxCall ?? ''),
    });

    // Double-click a CQ heard in an even slot → we answer in the odd slot.
    expect(ctrl.answerCq('CQ K1ABC FN42', 'even')).toBe(true);
    expect(ctrl.getState().txSlot).toBe('odd');

    ctrl.enableTx();
    expect(arm(calls).at(-1)?.body).toEqual({ enabled: true });

    // DX sends us a signal report → we stage R-report.
    ctrl.onWindow(['KB2UKA K1ABC -12']);
    const stage1 = tx(calls).at(-1);
    expect(stage1).toBeDefined();
    expect(String(stage1?.body.message)).toContain('K1ABC KB2UKA R');
    expect(stage1?.body.slot).toBe('odd');
    expect(stage1?.body.mode).toBe('FT8');

    // DX rogers with RR73 → we stage 73 and the QSO logs exactly once.
    ctrl.onWindow(['KB2UKA K1ABC RR73']);
    expect(String(tx(calls).at(-1)?.body.message)).toContain('73');
    expect(logged).toEqual(['K1ABC']);

    // Next window: signoff complete → disarm (disable-after-73).
    ctrl.onWindow([]);
    expect(arm(calls).at(-1)?.body).toEqual({ enabled: false });
  });

  it('stages the current message immediately on enableTx (CQ caller)', () => {
    const { fn, calls } = makeFetch();
    const ctrl = new Ft8TxController({ myCall: 'KB2UKA', myGrid4: 'FN12', fetchFn: fn });
    // CQ caller in 'calling': arming should both arm AND stage the CQ so the
    // keyer has something to key at the next slot.
    ctrl.enableTx();
    expect(arm(calls).at(-1)?.body).toEqual({ enabled: true });
    const stage = tx(calls).at(-1);
    expect(stage).toBeDefined();
    expect(String(stage?.body.message)).toBe('CQ KB2UKA FN12');
  });

  it('callStation() opens a QSO against any clicked decode', () => {
    const { fn } = makeFetch();
    const ctrl = new Ft8TxController({ myCall: 'KB2UKA', myGrid4: 'FN12', fetchFn: fn });
    // Click a station heard in an even slot → call the SENDER (W9ZZZ), reply in
    // the odd slot with our grid.
    expect(ctrl.callStation('K1ABC W9ZZZ -05', 'even')).toBe(true);
    expect(ctrl.getState().dxCall).toBe('W9ZZZ');
    expect(ctrl.getState().txSlot).toBe('odd');
    expect(ctrl.getOutgoing()).toBe('W9ZZZ KB2UKA FN12');
    // A decode with no parseable callsign is rejected.
    expect(ctrl.callStation('   ', 'even')).toBe(false);
  });

  it('CALL 1ST auto-answers the first decoded CQ while armed', () => {
    const { fn, calls } = makeFetch();
    const ctrl = new Ft8TxController({ myCall: 'KB2UKA', myGrid4: 'FN12', fetchFn: fn });
    ctrl.setCallFirst(true);
    ctrl.enableTx(); // arm + stage CQ
    // A window with a CQ heard in an even slot → auto-answer in odd with grid.
    ctrl.onWindow(['CQ DL1XYZ JO31'], undefined, 'even');
    expect(ctrl.getState().dxCall).toBe('DL1XYZ');
    expect(ctrl.getState().txSlot).toBe('odd');
    const stage = tx(calls).at(-1);
    expect(String(stage?.body.message)).toBe('DL1XYZ KB2UKA FN12');
  });

  it('CALL 1ST is inert when disarmed', () => {
    const { fn, calls } = makeFetch();
    const ctrl = new Ft8TxController({ myCall: 'KB2UKA', myGrid4: 'FN12', fetchFn: fn });
    ctrl.setCallFirst(true);
    // Not armed: a heard CQ must not stage anything.
    ctrl.onWindow(['CQ DL1XYZ JO31'], undefined, 'even');
    expect(tx(calls)).toHaveLength(0);
    expect(ctrl.getState().dxCall).toBeNull();
  });

  it('markLogged() latches so a manual log blocks the sequencer auto-log (exactly once)', () => {
    const { fn } = makeFetch();
    const logged: string[] = [];
    const ctrl = new Ft8TxController({
      myCall: 'KB2UKA',
      myGrid4: 'FN12',
      fetchFn: fn,
      onLogQso: (s) => logged.push(s.dxCall ?? ''),
    });

    // CQ caller picks up an answerer, advances to the report stage.
    ctrl.enableTx();
    ctrl.onWindow(['KB2UKA K1ABC FN31']);
    expect(ctrl.getState().dxCall).toBe('K1ABC');

    // Operator hits LOG QSO mid-QSO — latch it as logged.
    ctrl.markLogged();
    expect(ctrl.getState().logged).toBe(true);

    // The QSO then completes on the wire (R-report → 73). The auto-log guard is
    // `!state.logged`, so onLogQso must NOT fire a second time.
    ctrl.onWindow(['KB2UKA K1ABC R-12']);
    ctrl.onWindow(['KB2UKA K1ABC 73']);
    expect(logged).toEqual([]); // manual log already recorded it; no duplicate
  });

  it('posts /halt on operator Halt', () => {
    const { fn, calls } = makeFetch();
    const ctrl = new Ft8TxController({ myCall: 'KB2UKA', fetchFn: fn });
    ctrl.enableTx();
    ctrl.halt();
    expect(halt(calls)).toHaveLength(1);
    expect(ctrl.getState().enableTx).toBe(false);
  });

  it('respects HOLD TX FREQ for waterfall clicks', () => {
    const { fn } = makeFetch();
    const ctrl = new Ft8TxController({ myCall: 'KB2UKA', audioHz: 1500, fetchFn: fn });
    ctrl.setHoldTxFreq(true);
    ctrl.setTxFreq(2000);
    expect(ctrl.getAudioHz()).toBe(1500); // held — click ignored
    ctrl.setHoldTxFreq(false);
    ctrl.setTxFreq(2000);
    expect(ctrl.getAudioHz()).toBe(2000);
  });
});
