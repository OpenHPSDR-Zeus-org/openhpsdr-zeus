// SPDX-License-Identifier: GPL-2.0-or-later
//
// Ft8TxControl — the TX control cluster for the FT8/FT4 workspace. Renders the
// arm/transmit lamps (from the backend's 0x3A status, NOT local optimism), the
// ENABLE-TX master, HOLD-TX-FREQ, TX EVEN/ODD selector, the message + macro row,
// the TX audio-offset field, the TX POWER / TUNE PWR sliders, and TUNE. All
// keying flows through the runner (sequencer → Ft8TxController → backend keyer);
// arming is explicit only.
//
// Power lives in this cluster per the approved design (docs/designs/ft8-ui.md:
// "TX CONTROL cluster … TX POWER slider, TUNE button"). NO forked power model:
// TX POWER / TUNE PWR are the SAME DriveSlider / TunePowerSlider the main rig
// uses (→ /api/tx/drive, /api/tx/tune-drive), and TUNE reuses /api/tx/tun. TUNE
// reads server-authoritative tunOn from tx-store (not local optimism), and toggles
// via setTunOn so the MOX/TUN mutual-exclusion invariant holds.

import { useState } from 'react';
import { setTun } from '../../api/client';
import { DriveSlider } from '../../components/DriveSlider';
import { TunePowerSlider } from '../../components/TunePowerSlider';
import { FT8_MAX_TX_OFFSET_HZ, FT8_MIN_OFFSET_HZ } from '../../dsp/ft8-passband';
import { genCq, genTx4, genTx5 } from '../../dsp/ft8-sequencer';
import type { Ft8TxRunnerView } from '../../dsp/ft8-tx-runner';
import { useFt8TxStore } from '../../state/ft8-tx-store';
import { useTxStore } from '../../state/tx-store';

export interface Ft8TxControlProps {
  runner: Ft8TxRunnerView;
  myCall: string;
  myGrid: string;
  /** Operator's configured CQ macro text (e.g. "CQ" or "CQ POTA"). */
  cqMessage?: string;
  /** Operator's configured CQ-DX macro text (e.g. "CQ DX"). */
  cqDxMessage?: string;
  /** Operator's reusable 13-char free-text macro. */
  freeTextMacro?: string;
}

/** Extract the CQ directive from a configured CQ macro: "CQ" → null,
 *  "CQ DX" → "DX", "CQ POTA" → "POTA". Drives both the staged message and the
 *  sequencer's calling-state outgoing so the configured CQ survives a restage. */
function cqDirectiveOf(msg: string | undefined): string | null {
  const d = (msg ?? '').trim().replace(/^CQ\b\s*/i, '').trim();
  return d.length > 0 ? d : null;
}

export function Ft8TxControl({
  runner,
  myCall,
  myGrid,
  cqMessage,
  cqDxMessage,
  freeTextMacro,
}: Ft8TxControlProps) {
  const status = useFt8TxStore((s) => s.status);
  const [custom, setCustom] = useState('');
  // Server-authoritative TUN state + the action that keeps MOX/TUN mutually
  // exclusive (setTunOn(true) clears moxOn) — identical to the main TunButton.
  const tunOn = useTxStore((s) => s.tunOn);
  const setTunOn = useTxStore((s) => s.setTunOn);

  const qso = runner.qso;
  const dx = qso.dxCall;
  const armed = qso.enableTx;
  const liveArmed = status?.armed ?? false;
  const transmitting = status?.transmitting ?? false;
  const canCall = myCall.trim().length > 0;

  const toggleTune = () => {
    const next = !tunOn;
    // Optimistic via the store action so the button reacts immediately and the
    // moxOn=false invariant holds; the keyer status frame reconciles.
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
            min={FT8_MIN_OFFSET_HZ}
            max={FT8_MAX_TX_OFFSET_HZ}
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
          title={cqMessage && cqMessage.trim() !== 'CQ' ? `Call ${cqMessage}` : 'Call CQ'}
          onClick={() => {
            const dir = cqDirectiveOf(cqMessage);
            runner.startCq({ cqDirective: dir });
            runner.stageMacro(genCq(myCall, myGrid || null, dir));
          }}
        >
          CQ
        </button>
        <button
          type="button"
          className="ft8-tx__macro"
          disabled={!canCall}
          title={cqDxMessage ? `Call ${cqDxMessage}` : 'Call CQ DX'}
          onClick={() => {
            const dir = cqDirectiveOf(cqDxMessage ?? 'CQ DX');
            runner.startCq({ cqDirective: dir });
            runner.stageMacro(genCq(myCall, myGrid || null, dir));
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
        {freeTextMacro && freeTextMacro.trim() && (
          <button
            type="button"
            className="ft8-tx__macro"
            disabled={!canCall}
            title={`Stage free-text macro: ${freeTextMacro.trim()}`}
            onClick={() => runner.stageMacro(freeTextMacro.trim().toUpperCase())}
          >
            MSG
          </button>
        )}
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

      {/* TX power — the SAME drive/tune sliders the main rig uses (server-
          authoritative, no forked power model). Placed here per the design doc. */}
      <div className="ft8-power" aria-label="TX power">
        <div className="ft8-power__row">
          <span className="ft8-power__label">TX POWER</span>
          <DriveSlider />
        </div>
        <div className="ft8-power__row">
          <span className="ft8-power__label">TUNE PWR</span>
          <TunePowerSlider />
        </div>
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
