// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

import { useState } from 'react';
import type { CwEngineStatus } from '../../state/cw-store';
import type { CwKeyerMode } from '../../api/cw';

export type CwKeyerProps = {
  wpm: number;
  /** Tracks the slider while dragging — local-only, no PUT. */
  setWpmLocal: (v: number) => void;
  /** Schedules the server save after the operator stops moving. */
  setWpmCommit: (v: number) => void;
  /** On-board iambic keyer mode for a paddle on the radio's KEY jack.
   * Straight ignores WPM (operator controls timing manually). */
  keyerMode: CwKeyerMode;
  setKeyerMode: (m: CwKeyerMode) => void;
  /** CW monitor tone pitch (Hz). Backend clamps to 200..1200. */
  sidetoneHz: number;
  setSidetoneHzLocal: (v: number) => void;
  setSidetoneHzCommit: (v: number) => void;
  /** CW monitor tone gain (dB). Backend clamps to -60..0. */
  sidetoneGainDb: number;
  setSidetoneGainDbLocal: (v: number) => void;
  setSidetoneGainDbCommit: (v: number) => void;
  macros: string[];
  onSend: (macro: string) => void;
  onAbort: () => void;
  onMacroEdit: (index: number, value: string) => void;
  onMacroDelete: (index: number) => void;
  onMacroAdd: () => void;
  /** Hard cap from the backend — disables the Add button at the limit. */
  maxMacros: number;
  /** Live engine status from the WS hub. Idle when nothing in flight. */
  status: CwEngineStatus;
};

const WPM_MIN = 5;
const WPM_MAX = 40;
const PITCH_MIN = 200;
const PITCH_MAX = 1200;
const GAIN_MIN = -60;
const GAIN_MAX = 0;

const KEYER_MODE_LABELS: ReadonlyArray<{ mode: CwKeyerMode; label: string; title: string }> = [
  { mode: 'Straight', label: 'STR', title: 'Straight key / bug — you control the timing; WPM is ignored' },
  { mode: 'IambicA', label: 'IAM-A', title: 'Iambic paddle, Mode A' },
  { mode: 'IambicB', label: 'IAM-B', title: 'Iambic paddle, Mode B' },
];

export function CwKeyer({
  wpm,
  setWpmLocal,
  setWpmCommit,
  keyerMode,
  setKeyerMode,
  sidetoneHz,
  setSidetoneHzLocal,
  setSidetoneHzCommit,
  sidetoneGainDb,
  setSidetoneGainDbLocal,
  setSidetoneGainDbCommit,
  macros,
  onSend,
  onAbort,
  onMacroEdit,
  onMacroDelete,
  onMacroAdd,
  maxMacros,
  status,
}: CwKeyerProps) {
  // Index of the macro slot currently in edit mode. null = no inline
  // editor open. The editor is opened by clicking the ✎ icon next to a
  // slot; click elsewhere or press Enter/Esc to close.
  const [editingIndex, setEditingIndex] = useState<number | null>(null);
  const [editDraft, setEditDraft] = useState<string>('');

  const isSending = status.state === 'sending' || status.state === 'stopping';
  const atMacroLimit = macros.length >= maxMacros;

  const startEditing = (index: number, current: string) => {
    setEditDraft(current);
    setEditingIndex(index);
  };

  const commitEdit = (index: number) => {
    if (editDraft !== macros[index]) onMacroEdit(index, editDraft);
    setEditingIndex(null);
  };

  return (
    <div className="cw cw-console">
      <CwStream status={status} />

      <div className="cw-control-strip">
        <div className={`cw-wpm-readout ${isSending ? 'is-active' : ''}`}>
          <span className="cw-wpm-label">WPM</span>
          <span className="cw-wpm-value mono">{wpm}</span>
        </div>
        <div className="cw-wpm-track">
          <input
            type="range"
            min={WPM_MIN}
            max={WPM_MAX}
            step={1}
            value={wpm}
            onChange={(e) => {
              const v = Number(e.currentTarget.value);
              setWpmLocal(v);
              setWpmCommit(v);
            }}
            aria-label="Words per minute"
          />
          <div className="cw-wpm-scale">
            <span>{WPM_MIN}</span>
            <span>{WPM_MAX}</span>
          </div>
        </div>
        <button
          type="button"
          className={`cw-stop ${isSending ? 'is-armed' : ''}`}
          onClick={onAbort}
          disabled={!isSending}
          title="Abort current transmission and drain queue"
          aria-label="STOP"
        >
          STOP
        </button>
      </div>

      {/* On-board keyer mode for a paddle on the radio's KEY jack. Straight
        * MUST be selectable: a straight/bug key driven in iambic mode keys
        * wrong (the gateware treats the single line as a paddle). NOTE: WPM
        * above is NOT mode-dependent — it always drives host-generated macro
        * / cw_msg keying (MorseEncoder), and additionally sets the hardware
        * iambic keyer speed when mode != straight. So never dim/disable WPM
        * based on keyer mode. See zeus-bks. */}
      <div className="cw-keyer-mode-row" role="radiogroup" aria-label="CW keyer mode">
        <span className="cw-keyer-mode-label">KEY</span>
        <div className="cw-keyer-mode-seg">
          {KEYER_MODE_LABELS.map(({ mode, label, title }) => (
            <button
              key={mode}
              type="button"
              role="radio"
              aria-checked={keyerMode === mode}
              className={`cw-keyer-mode-btn ${keyerMode === mode ? 'is-active' : ''}`}
              onClick={() => setKeyerMode(mode)}
              title={title}
            >
              {label}
            </button>
          ))}
        </div>
      </div>

      {/* Sidetone monitor: pitch + gain of the in-browser CW monitor tone.
        * Audible during any local key — macro send, raw key from a logger,
        * or hardware key plugged into the radio. Reuses the WPM control-
        * strip CSS classes so the visual treatment matches without
        * introducing new design tokens. */}
      <div className="cw-control-strip">
        <div className="cw-wpm-readout">
          <span className="cw-wpm-label">PITCH</span>
          <span className="cw-wpm-value mono">{sidetoneHz}Hz</span>
        </div>
        <div className="cw-wpm-track">
          <input
            type="range"
            min={PITCH_MIN}
            max={PITCH_MAX}
            step={10}
            value={sidetoneHz}
            onChange={(e) => {
              const v = Number(e.currentTarget.value);
              setSidetoneHzLocal(v);
              setSidetoneHzCommit(v);
            }}
            aria-label="Sidetone pitch in Hz"
          />
          <div className="cw-wpm-scale">
            <span>{PITCH_MIN}</span>
            <span>{PITCH_MAX}</span>
          </div>
        </div>
        <div className="cw-wpm-readout">
          <span className="cw-wpm-label">SIDE</span>
          <span className="cw-wpm-value mono">{sidetoneGainDb}dB</span>
        </div>
        <div className="cw-wpm-track">
          <input
            type="range"
            min={GAIN_MIN}
            max={GAIN_MAX}
            step={1}
            value={sidetoneGainDb}
            onChange={(e) => {
              const v = Number(e.currentTarget.value);
              setSidetoneGainDbLocal(v);
              setSidetoneGainDbCommit(v);
            }}
            aria-label="Sidetone monitor gain in dB"
          />
          <div className="cw-wpm-scale">
            <span>{GAIN_MIN}</span>
            <span>{GAIN_MAX}</span>
          </div>
        </div>
      </div>

      <div className="cw-macro-list" role="list">
        {macros.map((m, i) => (
          <div key={i} className="cw-macro-row" role="listitem">
            <span className="cw-macro-num mono" aria-hidden="true">
              {String(i + 1).padStart(2, '0')}
            </span>
            {editingIndex === i ? (
              <input
                className="cw-macro-edit"
                autoFocus
                value={editDraft}
                onChange={(e) => setEditDraft(e.target.value)}
                onBlur={() => commitEdit(i)}
                onKeyDown={(e) => {
                  if (e.key === 'Enter') e.currentTarget.blur();
                  else if (e.key === 'Escape') setEditingIndex(null);
                }}
              />
            ) : (
              <button
                type="button"
                className={`cw-macro-send ${m === '' ? 'is-empty' : ''}`}
                onClick={() => m !== '' && onSend(m)}
                disabled={m === ''}
                title={m === '' ? 'Empty slot — edit to set' : `Send "${m}"`}
              >
                {m === '' ? <em>empty — edit me</em> : m}
              </button>
            )}
            <button
              type="button"
              className="cw-macro-icon"
              onClick={() => startEditing(i, m)}
              title="Edit macro"
              aria-label={`Edit macro ${i + 1}`}
            >
              <EditGlyph />
            </button>
            <button
              type="button"
              className="cw-macro-icon is-danger"
              onClick={() => onMacroDelete(i)}
              title="Delete macro"
              aria-label={`Delete macro ${i + 1}`}
            >
              <DeleteGlyph />
            </button>
          </div>
        ))}
        <button
          type="button"
          className="cw-macro-add"
          onClick={onMacroAdd}
          disabled={atMacroLimit}
          title={atMacroLimit ? `Maximum ${maxMacros} macros reached` : 'Add a new macro slot'}
        >
          <span className="cw-macro-add-plus" aria-hidden="true">+</span>
          <span className="cw-macro-add-label">
            {atMacroLimit ? `LIMIT — ${maxMacros} macros` : 'ADD MACRO'}
          </span>
        </button>
      </div>
    </div>
  );
}

/* ---- Stream display (HERO) ----
 * Renders what's being keyed right now as a "telegraph tape" across the
 * top of the panel. Always present (no layout shift on idle ↔ sending);
 * a small LED + state label on the left and a queue-depth chip on the
 * right; the centre carries the live text with a JetBrains-Mono cursor
 * estimated from elapsed-time × WPM. Server only emits status frames on
 * state edges, so per-character animation has to be reconstructed
 * client-side.
 */
function CwStream({ status }: { status: CwEngineStatus }) {
  const [nowMs, setNowMs] = useState<number>(() => Date.now());
  const active = status.state === 'sending' || status.state === 'stopping';
  if (active) {
    requestAnimationFrame(() => setNowMs(Date.now()));
  }

  const unitMs = status.wpm > 0 ? 1200 / status.wpm : 60;
  const elapsedMs = Math.max(0, nowMs - status.receivedAtMs);
  const charIdx = active
    ? Math.min(status.text.length, Math.floor(elapsedMs / (10 * unitMs)))
    : 0;

  const stateLabel: Record<CwEngineStatus['state'], string> = {
    idle: 'READY',
    sending: 'SENDING',
    stopping: 'STOPPING',
    aborting: 'ABORTING',
  };

  return (
    <div
      className={`cw-stream-hero ${active ? 'is-active' : 'is-idle'}`}
      data-state={status.state}
    >
      <div className="cw-stream-tag">
        <span className="cw-stream-led" aria-hidden="true" />
        <span className="cw-stream-label">{stateLabel[status.state]}</span>
      </div>
      <div className="cw-stream-tape mono">
        {active ? (
          <>
            <span className="cw-stream-sent">{status.text.slice(0, charIdx)}</span>
            <span className="cw-stream-cursor">
              {status.text.charAt(charIdx) || '▮'}
            </span>
            <span className="cw-stream-pending">{status.text.slice(charIdx + 1)}</span>
          </>
        ) : (
          <span className="cw-stream-placeholder">—— STAND BY ——</span>
        )}
      </div>
      <div className="cw-stream-queue mono" aria-live="polite">
        {status.queueDepth > 0 ? `+${status.queueDepth}` : '·'}
      </div>
    </div>
  );
}

/* ---- Glyphs ----
 * Inline SVGs so the icon weight matches Inter at the panel's text size
 * (Unicode pencil / ✕ were inconsistent across platforms — heavy on macOS,
 * thin on Linux). 12 px viewport, currentColor stroke so they pick up the
 * button's text colour for free.
 */
function EditGlyph() {
  return (
    <svg viewBox="0 0 12 12" width="12" height="12" aria-hidden="true">
      <path
        d="M1.5 9.5 L8.5 2.5 L10 4 L3 11 L1 11 L1.5 9.5 Z"
        fill="none"
        stroke="currentColor"
        strokeWidth="1"
        strokeLinejoin="round"
      />
      <path d="M7.5 3.5 L9 5" fill="none" stroke="currentColor" strokeWidth="1" />
    </svg>
  );
}

function DeleteGlyph() {
  return (
    <svg viewBox="0 0 12 12" width="12" height="12" aria-hidden="true">
      <path d="M2 2 L10 10 M10 2 L2 10" stroke="currentColor" strokeWidth="1.25" strokeLinecap="round" />
    </svg>
  );
}
