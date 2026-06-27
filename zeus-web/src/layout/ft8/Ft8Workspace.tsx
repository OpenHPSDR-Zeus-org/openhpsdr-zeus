// SPDX-License-Identifier: GPL-2.0-or-later
//
// Ft8Workspace — the full-screen FT8/FT4 command center (KB2UKA design,
// Option A dark-HUD). A purpose-built locked layout that comes up when a digital
// mode is engaged. Three columns under a header, status bar across the bottom.
//
// Live: decode table, VFO readout, band selector, protocol tabs, operator
// identity, and click-to-call QSO staging. The QSO panel previews the full
// auto-sequence; actual on-air keying is BENCH-GATED and not wired here (see the
// banner in the TX region). The waterfall/band-map reuse existing Zeus seams and
// are tracked as follow-ups.

import { useEffect, useMemo, useState } from 'react';
import { useFt8Store, type Ft8Row } from '../../state/ft8-store';
import { useConnectionStore } from '../../state/connection-store';
import { useOperatorStore } from '../../state/operator-store';
import { DIGITAL_BANDS } from '../../dsp/digital-segments';
import { parseFt8Message, type Ft8Message } from '../../dsp/ft8-message';
import {
  answerCq,
  currentOutgoing,
  genTx2,
  genTx3,
  genTx4,
  genTx5,
  slotOf,
  type QsoState,
} from '../../dsp/ft8-sequencer';
import { Ft8DecodeTable } from './Ft8DecodeTable';
import '../../styles/ft8-theme.css';

export interface Ft8WorkspaceProps {
  /** Called when the operator leaves FT8 (e.g. closes the workspace). */
  onClose?: () => void;
}

function useUtcClock(): string {
  const [now, setNow] = useState(() => new Date());
  useEffect(() => {
    const id = setInterval(() => setNow(new Date()), 1000);
    return () => clearInterval(id);
  }, []);
  const p = (n: number) => n.toString().padStart(2, '0');
  return `${p(now.getUTCHours())}:${p(now.getUTCMinutes())}:${p(now.getUTCSeconds())} UTC`;
}

function fmtMHz(hz?: number): string {
  if (!hz || hz <= 0) return '—.———.———';
  return (hz / 1e6).toFixed(6);
}

export function Ft8Workspace({ onClose }: Ft8WorkspaceProps) {
  const clock = useUtcClock();
  const protocol = useFt8Store((s) => s.protocol);
  const band = useFt8Store((s) => s.band);
  const nativeAvailable = useFt8Store((s) => s.nativeAvailable);
  const enabled = useFt8Store((s) => s.enabled);
  const decodeCount = useFt8Store((s) => s.rows.length);
  const error = useFt8Store((s) => s.error);
  const switchProtocol = useFt8Store((s) => s.switchProtocol);
  const qsyBand = useFt8Store((s) => s.qsyBand);

  const vfoHz = useConnectionStore((s) => s.vfoHz);
  const myCall = useOperatorStore((s) => s.call);
  const myGrid = useOperatorStore((s) => s.grid);
  const setCall = useOperatorStore((s) => s.setCall);
  const setGrid = useOperatorStore((s) => s.setGrid);

  const [staged, setStaged] = useState<QsoState | null>(null);

  // Esc closes the workspace.
  useEffect(() => {
    if (!onClose) return;
    const h = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.preventDefault();
        onClose();
      }
    };
    window.addEventListener('keydown', h);
    return () => window.removeEventListener('keydown', h);
  }, [onClose]);

  // Click a decode → stage a QSO answering that station.
  const onRowClick = (row: Ft8Row) => {
    if (!myCall) return; // need an operator call to generate Tx messages
    const parsed = parseFt8Message(row.text, myCall);
    if (!parsed.deCall) return;
    // The slot the heard station transmitted in (FT8 15 s / FT4 7.5 s) — we
    // answer in the opposite slot.
    const secs = new Date(row.slotStartUnixMs).getUTCSeconds();
    const senderSlot = slotOf(secs, protocol);
    // Treat any clicked station as a CQ to answer (start with our grid reply).
    const asCq: Ft8Message = { ...parsed, kind: 'cq' };
    const next = answerCq({ myCall, myGrid4: myGrid || null, mode: protocol }, asCq, senderSlot);
    if (next) setStaged(next);
  };

  const bandsForProtocol = useMemo(
    () => DIGITAL_BANDS.filter((b) => (protocol === 'FT4' ? b.ft4Hz != null : b.ft8Hz != null)),
    [protocol],
  );

  return (
    <div className="ft8-workspace" role="region" aria-label="FT8 workspace">
      <header className="ft8-ws-header">
        <span className="ft8-ws-title">{protocol} DIGITAL MODE</span>
        <div className="ft8-ws-tabs" role="tablist" aria-label="Digital protocol">
          {(['FT8', 'FT4'] as const).map((p) => (
            <button
              key={p}
              type="button"
              role="tab"
              aria-selected={protocol === p}
              className={`ft8-ws-tab${protocol === p ? ' is-active' : ''}`}
              onClick={() => switchProtocol(p)}
            >
              {p}
            </button>
          ))}
        </div>
        <label className="ft8-ws-id">
          <span>Call</span>
          <input
            value={myCall}
            onChange={(e) => setCall(e.target.value)}
            placeholder="MYCALL"
            spellCheck={false}
            size={8}
          />
        </label>
        <label className="ft8-ws-id">
          <span>Grid</span>
          <input
            value={myGrid}
            onChange={(e) => setGrid(e.target.value)}
            placeholder="FN42"
            spellCheck={false}
            size={6}
          />
        </label>
        <span className="ft8-ws-clock">{clock}</span>
        {onClose && (
          <button type="button" className="ft8-ws-close" onClick={onClose}>
            Exit · Esc
          </button>
        )}
      </header>

      <div className="ft8-ws-body">
        {/* Left — radio / VFO / band */}
        <div className="ft8-ws-col">
          <section className="ft8-region">
            <div className="ft8-region__head">Radio</div>
            <div className="ft8-vfo">
              {fmtMHz(vfoHz)} <small>MHz · USB/DIGU</small>
            </div>
            <div className="ft8-band-grid">
              {bandsForProtocol.map((b) => (
                <button
                  key={b.name}
                  type="button"
                  className={`ft8-band-btn${band === b.name ? ' is-active' : ''}`}
                  onClick={() => qsyBand(b.name)}
                  title={`QSY to ${b.name} ${protocol} dial`}
                >
                  {b.name}
                </button>
              ))}
            </div>
          </section>
          <section className="ft8-region">
            <div className="ft8-region__head">Band activity</div>
            <div className="ft8-placeholder">spectrum — reuses panadapter WebGL (follow-up)</div>
          </section>
          <section className="ft8-region ft8-region--grow">
            <div className="ft8-region__head">Receive · waterfall</div>
            <div className="ft8-placeholder">waterfall + decode markers (follow-up)</div>
          </section>
        </div>

        {/* Center — decode table (live) + activity log */}
        <div className="ft8-ws-col ft8-ws-col--center">
          <section className="ft8-region ft8-region--decodes ft8-region--grow">
            <div className="ft8-region__head">
              Decoded messages
              {myCall ? (
                <small> · click a station to call</small>
              ) : (
                <small> · set your Call to enable calling</small>
              )}
            </div>
            <div className="ft8-region__body">
              <Ft8DecodeTable myCall={myCall || undefined} onRowClick={onRowClick} />
            </div>
          </section>
          <section className="ft8-region">
            <div className="ft8-region__head">Activity log</div>
            <div className="ft8-placeholder">QSO log — feeds existing LogService (ADIF, follow-up)</div>
          </section>
        </div>

        {/* Right — QSO / TX control */}
        <div className="ft8-ws-col">
          <section className="ft8-region ft8-region--grow">
            <div className="ft8-region__head">TX control · QSO</div>
            <div className="ft8-region__body">
              <QsoPanel staged={staged} onClear={() => setStaged(null)} />
            </div>
          </section>
          <section className="ft8-region">
            <div className="ft8-region__head">Band map</div>
            <div className="ft8-placeholder">great-circle map from decoded grids (follow-up)</div>
          </section>
        </div>
      </div>

      <footer className="ft8-ws-status">
        <span className={nativeAvailable ? 'ok' : 'warn'}>
          {nativeAvailable ? (enabled ? 'DECODING' : 'DECODER READY') : 'DECODER UNAVAILABLE'}
        </span>
        <span>{decodeCount} decodes</span>
        {error && <span className="warn">{error}</span>}
        <span style={{ marginLeft: 'auto' }}>
          {protocol} native · {band}
        </span>
      </footer>
    </div>
  );
}

/** Staged-QSO preview: the message ladder we WOULD transmit. Keying is
 *  bench-gated — Enable Tx is intentionally inert until verified on the G2. */
function QsoPanel({ staged, onClear }: { staged: QsoState | null; onClear: () => void }) {
  if (!staged || !staged.dxCall) {
    return (
      <div className="ft8-qso ft8-qso--empty">
        Click a decoded station to stage a QSO. The auto-sequence preview appears here.
      </div>
    );
  }
  const his = staged.dxCall;
  const mine = staged.myCall;
  const ladder: { label: string; msg: string }[] = [
    { label: 'Tx1', msg: currentOutgoing(staged) ?? '' },
    { label: 'Tx2', msg: genTx2(his, mine, -10) },
    { label: 'Tx3', msg: genTx3(his, mine, -10) },
    { label: 'Tx4', msg: genTx4(his, mine, staged.txAck) },
    { label: 'Tx5', msg: genTx5(his, mine) },
  ];
  return (
    <div className="ft8-qso">
      <div className="ft8-qso__dx">
        Calling <strong>{his}</strong>
        {staged.dxGrid4 ? ` · ${staged.dxGrid4}` : ''} · slot {staged.txSlot}
      </div>
      <ol className="ft8-qso__ladder">
        {ladder.map((l) => (
          <li key={l.label}>
            <span className="ft8-qso__slot">{l.label}</span>
            <span className="ft8-qso__msg">{l.msg}</span>
          </li>
        ))}
      </ol>
      <div className="ft8-qso__bench" role="note">
        ⚠ TX keying is bench-gated — Enable Tx is disabled until verified on the G2. This panel
        previews the sequence only.
      </div>
      <div className="ft8-qso__actions">
        <button type="button" className="ft8-qso__btn" disabled title="Bench-gated">
          Enable Tx
        </button>
        <button type="button" className="ft8-qso__btn" onClick={onClear}>
          Clear
        </button>
      </div>
    </div>
  );
}

/**
 * Self-contained mount point: subscribes to ft8-store `open` and renders the
 * workspace overlay or nothing. The overlay is position:fixed and covers the app
 * when open.
 */
export function Ft8WorkspaceMount() {
  const open = useFt8Store((s) => s.open);
  const close = useFt8Store((s) => s.closeWorkspace);
  if (!open) return null;
  return <Ft8Workspace onClose={close} />;
}
