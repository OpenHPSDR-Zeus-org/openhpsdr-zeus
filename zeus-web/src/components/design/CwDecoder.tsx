// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

import { useCwDecoderStore, type CwDecoderState } from '../../state/cw-decoder-store';

export function CwDecoder() {
  const {
    state,
    currentText,
    history,
    wpm,
    snrDb,
    confidence,
    toggleHold,
    clear,
  } = useCwDecoderStore();

  const isListening = state === 'listening';
  const isHeld = state === 'held';
  const isIdle = state === 'idle';

  const stateLabel: Record<CwDecoderState, string> = {
    idle: 'OFF',
    listening: 'DECODE',
    held: 'HELD',
  };

  const confidenceColor = confidence > 0.8 ? 'var(--accent-bright)' :
                          confidence > 0.5 ? 'var(--power)' :
                          'var(--tx)';

  const confidenceLabel = confidence > 0.8 ? 'HI' :
                          confidence > 0.5 ? 'MED' :
                          'LO';

  return (
    <div className="cw cw-console">
      <CwDecoderStream
        isListening={isListening}
        isHeld={isHeld}
        stateLabel={stateLabel[state]}
        text={currentText}
      />

      <div className="cw-control-strip">
        <div className={`cw-wpm-readout ${isListening ? 'is-active' : ''}`}>
          <span className="cw-wpm-label">WPM</span>
          <span className="cw-wpm-value mono">{wpm}</span>
        </div>

        <div className={`cw-wpm-readout`}>
          <span className="cw-wpm-label">SNR</span>
          <span className="cw-wpm-value mono">{snrDb.toFixed(0)}dB</span>
        </div>

        <div className="cw-confidence-readout">
          <span
            className="cw-confidence-dot"
            style={{ background: confidenceColor }}
            aria-label={`Confidence: ${confidenceLabel}`}
          />
          <span className="cw-wpm-label">{confidenceLabel}</span>
        </div>

        <button
          type="button"
          className={`cw-hold ${isHeld ? 'is-armed' : ''}`}
          onClick={toggleHold}
          disabled={isIdle}
          title="Hold decoder (pause decoding)"
          aria-label="HOLD"
        >
          HOLD
        </button>

        <button
          type="button"
          className="cw-clear"
          onClick={clear}
          disabled={currentText === '' && isIdle}
          title="Clear current buffer"
          aria-label="CLEAR"
        >
          CLEAR
        </button>
      </div>

      <div className="cw-history" role="log" aria-live="polite">
        {history.map((entry, i) => (
          <div key={i} className="cw-history-entry">
            <span className="cw-history-time mono">{entry.timestamp}</span>
            <span className="cw-history-text mono">{entry.text}</span>
          </div>
        ))}
        {history.length === 0 && (
          <div className="cw-history-empty">— NO HISTORY —</div>
        )}
      </div>
    </div>
  );
}

interface CwDecoderStreamProps {
  isListening: boolean;
  isHeld: boolean;
  stateLabel: string;
  text: string;
}

function CwDecoderStream({ isListening, isHeld, stateLabel, text }: CwDecoderStreamProps) {
  const active = isListening || isHeld;

  return (
    <div className={`cw-stream-hero ${active ? 'is-active' : 'is-idle'}`} data-state={isHeld ? 'held' : 'listening'}>
      <div className="cw-stream-tag">
        <span className="cw-stream-led" aria-hidden="true" />
        <span className="cw-stream-label">{stateLabel}</span>
      </div>
      <div className="cw-stream-tape mono">
        {active ? (
          <span className="cw-stream-decoded">{text || '…'}</span>
        ) : (
          <span className="cw-stream-placeholder">—— DECODER OFF ——</span>
        )}
      </div>
      <div className="cw-stream-queue mono" aria-live="polite">
        {isHeld ? '⏸' : '·'}
      </div>
    </div>
  );
}