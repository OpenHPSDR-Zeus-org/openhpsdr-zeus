// SPDX-License-Identifier: GPL-2.0-or-later
//
// Ft8TxControl — the TX control cluster for the FT8/FT4 workspace. Renders the
// arm/transmit lamps (from the backend's 0x3A status, NOT local optimism), the
// ENABLE-TX master, HOLD-TX-FREQ, TX EVEN/ODD selector, the message + macro row,
// the TX audio-offset field, and TUNE. All keying flows through the runner
// (sequencer → Ft8TxController → backend keyer); arming is explicit only.
//
// Power (Drive %, Tune %) is owned by the main app's TX controls (Team B) and is
// intentionally not duplicated here — this cluster only arms, sequences, and
// keys. TUNE reuses the same /api/tx/tun carrier the main rig uses.

import { useState } from 'react';
import { setTun } from '../../api/client';
import { genCq, genTx4, genTx5 } from '../../dsp/ft8-sequencer';
import type { Ft8TxRunnerView } from '../../dsp/ft8-tx-runner';
import { useFt8TxStore } from '../../state/ft8-tx-store';

export interface Ft8TxControlProps {
  runner: Ft8TxRunnerView;
  myCall: string;
  myGrid: string;
}

export function Ft8TxControl({ runner, myCall, myGrid }: Ft8TxControlProps) {
  const status = useFt8TxStore((s) => s.status);
  const [custom, setCustom] = useState('');
  const [tunOn, setTunOn] = useState(false);

  const qso = runner.qso;
  const dx = qso.dxCall;
  const armed = qso.enableTx;
  const liveArmed = status?.armed ?? false;
  const transmitting = status?.transmitting ?? false;
  const canCall = myCall.trim().length > 0;

  const toggleTune = () => {
    const next = !tunOn;
    setTunOn(next);
    void setTun(next).catch(() => setTunOn(!next));
  };

  return (
    <div className="ft8-tx" aria-label="TX control">
      {/* Status lamps — driven by the backend keyer, not local state. */}
      <div className="ft8-tx__lamps">
        <span className={`ft8-tx__lamp${liveArmed ? ' is-armed' : ''}`} title="Backend armed">
          ARMED
        </span>
        <span className={`ft8-tx__lamp${transmitting ? ' is-tx' : ''}`} title="On air">
          TX
        </span>
        {liveArmed && status && status.watchdogSecsRemaining > 0 && (
          <span className="ft8-tx__wdt" title="Watchdog auto-disarm">
            WDT {Math.ceil(status.watchdogSecsRemaining)}s
          </span>
        )}
      </div>

      {/* ENABLE-TX master + HOLD TX FREQ. */}
      <div className="ft8-tx__row">
        <button
          type="button"
          className={`ft8-tx__enable${armed ? ' is-on' : ''}`}
          disabled={!canCall}
          title={canCall ? 'Arm the keyer (explicit)' : 'Set your Call to enable TX'}
          onClick={() => (armed ? runner.disableTx() : runner.enableTx())}
        >
          {armed ? 'TX ENABLED · DISABLE' : 'TX ENABLE'}
        </button>
        <button
          type="button"
          className={`ft8-tx__toggle${qso.holdTxFreq ? ' is-on' : ''}`}
          onClick={() => runner.setHoldTxFreq(!qso.holdTxFreq)}
          title="Lock the TX audio offset against waterfall clicks"
        >
          HOLD TX FREQ
        </button>
      </div>

      {/* TX slot (even/odd) + audio offset. */}
      <div className="ft8-tx__row">
        <div className="ft8-tx__seg" role="group" aria-label="TX slot">
          <span className="ft8-tx__seg-label">TX</span>
          {(['even', 'odd'] as const).map((s) => (
            <button
              key={s}
              type="button"
              className={`ft8-tx__seg-btn${qso.txSlot === s ? ' is-on' : ''}`}
              onClick={() => runner.setTxSlot(s)}
            >
              {s === 'even' ? '1ST' : '2ND'}
            </button>
          ))}
        </div>
        <label className="ft8-tx__offset">
          <span>OFFSET</span>
          <input
            type="number"
            min={0}
            max={2500}
            step={1}
            value={Math.round(runner.audioHz)}
            disabled={qso.holdTxFreq}
            onChange={(e) => runner.setTxFreq(Number(e.target.value))}
          />
          <span>Hz</span>
        </label>
      </div>

      {/* Next / staged message preview. */}
      <div className="ft8-tx__next">
        <span className="ft8-tx__next-label">NEXT</span>
        <span className="ft8-tx__next-msg">{runner.outgoing ?? '—'}</span>
      </div>
      {dx && (
        <div className="ft8-tx__dx">
          QSO with <strong>{dx}</strong>
          {qso.dxGrid4 ? ` · ${qso.dxGrid4}` : ''} · {qso.progress}
        </div>
      )}

      {/* Macro row — operator overrides. */}
      <div className="ft8-tx__macros" role="group" aria-label="TX macros">
        <button
          type="button"
          className="ft8-tx__macro"
          disabled={!canCall}
          onClick={() => {
            runner.startCq();
            runner.stageMacro(genCq(myCall, myGrid || null));
          }}
        >
          CQ
        </button>
        <button
          type="button"
          className="ft8-tx__macro"
          disabled={!canCall}
          onClick={() => {
            runner.startCq();
            runner.stageMacro(genCq(myCall, myGrid || null, 'DX'));
          }}
        >
          CQ DX
        </button>
        <button
          type="button"
          className="ft8-tx__macro"
          disabled={!runner.outgoing}
          title="Re-send the standard next message (grid / report)"
          onClick={() => runner.outgoing && runner.stageMacro(runner.outgoing)}
        >
          GRID/RPT
        </button>
        <button
          type="button"
          className="ft8-tx__macro"
          disabled={!dx || !canCall}
          onClick={() => dx && runner.stageMacro(genTx4(dx, myCall, 'RR73'))}
        >
          RR73
        </button>
        <button
          type="button"
          className="ft8-tx__macro"
          disabled={!dx || !canCall}
          onClick={() => dx && runner.stageMacro(genTx5(dx, myCall))}
        >
          73
        </button>
        <button
          type="button"
          className={`ft8-tx__macro${runner.callFirst ? ' is-on' : ''}`}
          title="Auto-answer the first decoded CQ while armed"
          onClick={() => runner.setCallFirst(!runner.callFirst)}
        >
          CALL 1ST
        </button>
      </div>

      {/* Free-form message stage. */}
      <div className="ft8-tx__custom">
        <input
          value={custom}
          onChange={(e) => setCustom(e.target.value.toUpperCase())}
          placeholder="free-form message"
          spellCheck={false}
          maxLength={13}
        />
        <button
          type="button"
          className="ft8-tx__macro"
          disabled={!custom.trim()}
          onClick={() => {
            runner.stageMacro(custom.trim());
            setCustom('');
          }}
        >
          STAGE
        </button>
      </div>

      {/* TUNE + HALT. */}
      <div className="ft8-tx__row">
        <button
          type="button"
          className={`ft8-tx__tune${tunOn ? ' is-on' : ''}`}
          onClick={toggleTune}
          title="Key a single-tone carrier (antenna tune)"
        >
          {tunOn ? 'TUNE ON' : 'TUNE'}
        </button>
        <button
          type="button"
          className="ft8-tx__halt"
          onClick={() => runner.halt()}
          title="Abort: disarm and drop the keyer immediately"
        >
          HALT
        </button>
      </div>
    </div>
  );
}
