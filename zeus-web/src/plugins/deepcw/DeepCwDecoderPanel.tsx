// SPDX-License-Identifier: GPL-2.0-or-later
//
// CW Decoder panel — neural (DeepCW) receive decoder. Taps the RX audio bus
// and runs the e04/deepcw-engine ONNX model in a worker (see
// use-deepcw-decoder). Layout, top→bottom: status + controls, a live
// spectrogram of the CW band (so you can SEE the Morse), then the rolling
// decoded transcript. Telegraph-console visual language, shared with the
// keyer; status reflects the model (loading / decoding) since the neural
// model doesn't emit the classic decoder's WPM/SNR readouts.

import { useEffect, useRef } from 'react';
import { TileChrome } from '../../layout/TileChrome';
import type { PanelComponentProps } from '../../layout/panels';
import { CwSpectrogram } from './CwSpectrogram';
import {
  DECODE_WINDOW_OPTIONS,
  useDeepCwStore,
  type DeepCwState,
} from './deepcw-store';
import { useDeepCwDecoder } from './use-deepcw-decoder';

const STATE_LABEL: Record<DeepCwState, string> = {
  idle: 'OFF',
  listening: 'LISTENING',
  held: 'HELD',
};

export function DeepCwDecoderPanel({ onRemove }: PanelComponentProps) {
  const {
    state,
    text,
    modelLoaded,
    loadError,
    isDecoding,
    windowSeconds,
    setEnabled,
    toggleHold,
    clear,
    setWindowSeconds,
  } = useDeepCwStore();

  // Owns the audio tap + worker decode loop while the panel is enabled.
  useDeepCwDecoder();

  const isEnabled = state !== 'idle';
  const isListening = state === 'listening';
  const isHeld = state === 'held';
  const isIdle = state === 'idle';
  const active = isListening || isHeld;
  const handleRemove = onRemove ?? (() => {});

  // Keep the latest decode in view as the rolling transcript refreshes.
  const tapeRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const el = tapeRef.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [text]);

  const statusText = loadError
    ? 'MODEL ERROR'
    : active && !modelLoaded
      ? 'LOADING MODEL'
      : isDecoding
        ? 'DECODING'
        : STATE_LABEL[state];

  return (
    <>
      <TileChrome
        title="CW Decoder · DeepCW"
        onRemove={handleRemove}
        rightSlot={
          <button
            type="button"
            className={`btn ${isEnabled ? 'accent' : ''}`}
            onClick={() => setEnabled(!isEnabled)}
            aria-label={isEnabled ? 'Disable decoder' : 'Enable decoder'}
          >
            {isEnabled ? 'ON' : 'OFF'}
          </button>
        }
      />
      <div className="workspace-tile-body">
        <div className="cw cw-console deepcw">
          {/* Status + controls */}
          <div
            className={`cw-stream-hero cw-stream-hero--header ${active ? 'is-active' : 'is-idle'}`}
            data-state={isHeld ? 'held' : 'listening'}
          >
            <div className="cw-stream-tag">
              <span
                className={`cw-stream-led ${isDecoding ? 'deepcw-led--busy' : ''}`}
                aria-hidden="true"
              />
              <span className="cw-stream-label">{statusText}</span>
            </div>

            <div className="cw-control-strip cw-control-strip--inline">
              <div className="deepcw-win" role="group" aria-label="Decode window length">
                {DECODE_WINDOW_OPTIONS.map((s) => (
                  <button
                    key={s}
                    type="button"
                    className={`deepcw-win-chip ${windowSeconds === s ? 'is-active' : ''}`}
                    onClick={() => setWindowSeconds(s)}
                    title={`Decode the last ${s} seconds of audio`}
                    aria-pressed={windowSeconds === s}
                  >
                    {s}s
                  </button>
                ))}
              </div>

              <button
                type="button"
                className={`cw-hold ${isHeld ? 'is-armed' : ''}`}
                onClick={toggleHold}
                disabled={isIdle}
                title="Hold decoder (freeze the transcript)"
                aria-label="HOLD"
              >
                HOLD
              </button>

              <button
                type="button"
                className="cw-clear"
                onClick={clear}
                disabled={text === '' && isIdle}
                title="Clear the decoded transcript"
                aria-label="CLEAR"
              >
                CLEAR
              </button>
            </div>
          </div>

          {/* Live CW-band spectrogram */}
          <CwSpectrogram active={active} />

          {/* Rolling decoded transcript */}
          <div ref={tapeRef} className="cw-decoded-window deepcw-tape mono" role="log" aria-live="polite">
            {loadError ? (
              <span className="cw-stream-placeholder">model failed to load: {loadError}</span>
            ) : active ? (
              text ? (
                <>
                  {text}
                  <span className="deepcw-cursor" aria-hidden="true">
                    ▋
                  </span>
                </>
              ) : (
                <span className="cw-stream-placeholder">
                  {modelLoaded ? '… waiting for signal …' : '… loading neural model …'}
                </span>
              )
            ) : (
              <span className="cw-stream-placeholder">—— DECODER OFF ——</span>
            )}
          </div>
        </div>
      </div>
    </>
  );
}
