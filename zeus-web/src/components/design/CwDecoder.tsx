// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

import { useEffect, useRef } from 'react';
import { useCwDecoderStore, type CwDecoderState } from '../../state/cw-decoder-store';

export function CwDecoder() {
  const {
    state,
    text,
    wpm,
    snrDb,
    confidence,
    toggleHold,
    clear,
  } = useCwDecoderStore();

  // Decoding runs server-side (CwDecoderService → hub → cw-decoder-store via
  // ws-client). This component only renders the streamed text + stats.
  const isListening = state === 'listening';
  const isHeld = state === 'held';
  const isIdle = state === 'idle';
  const active = isListening || isHeld;

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

  // Continuous teleprinter: keep the latest text in view as it streams in.
  // CW has no line breaks, so this is one wrapping, scrolling buffer.
  const tapeRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const el = tapeRef.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [text]);

  return (
    <div className="cw cw-console">
      <div className={`cw-stream-hero cw-stream-hero--header ${active ? 'is-active' : 'is-idle'}`} data-state={isHeld ? 'held' : 'listening'}>
        <div className="cw-stream-tag">
          <span className="cw-stream-led" aria-hidden="true" />
          <span className="cw-stream-label">{stateLabel[state]}</span>
        </div>

        <div className="cw-control-strip cw-control-strip--inline">
          <div className={`cw-wpm-readout ${isListening ? 'is-active' : ''}`}>
            <span className="cw-wpm-label">WPM</span>
            <span className="cw-wpm-value mono">{wpm}</span>
          </div>

          <div className="cw-wpm-readout">
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
            title="Hold decoder (pause display)"
            aria-label="HOLD"
          >
            HOLD
          </button>

          <button
            type="button"
            className="cw-clear"
            onClick={clear}
            disabled={text === '' && isIdle}
            title="Clear the decoded buffer"
            aria-label="CLEAR"
          >
            CLEAR
          </button>
        </div>
      </div>

      <div
        ref={tapeRef}
        className="cw-decoded-window mono"
        role="log"
        aria-live="polite"
      >
        {active
          ? (text || <span className="cw-stream-placeholder">… waiting for signal …</span>)
          : <span className="cw-stream-placeholder">—— DECODER OFF ——</span>}
      </div>
    </div>
  );
}
