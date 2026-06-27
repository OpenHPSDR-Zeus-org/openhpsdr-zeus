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
import { slotOf } from '../../dsp/ft8-sequencer';
import { useFt8TxRunner } from '../../dsp/ft8-tx-runner';
import { qsoStateToLogEntry } from '../../dsp/ft8-qso-log';
import { useLoggerStore } from '../../state/logger-store';
import { Ft8DecodeTable } from './Ft8DecodeTable';
import { Ft8TxControl } from './Ft8TxControl';
import { Ft8ReceivePanel } from './Ft8ReceivePanel';
import { Ft8ActivityLog } from './Ft8ActivityLog';
import { Ft8Stats } from './Ft8Stats';
import { SpottingIndicator } from './SpottingIndicator';
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
  const rxFocusHz = useFt8Store((s) => s.rxFocusHz);
  const setRxFocusHz = useFt8Store((s) => s.setRxFocusHz);
  const myCall = useOperatorStore((s) => s.call);
  const myGrid = useOperatorStore((s) => s.grid);
  const setCall = useOperatorStore((s) => s.setCall);
  const setGrid = useOperatorStore((s) => s.setGrid);

  const addLogEntry = useLoggerStore((s) => s.addLogEntry);
  const entries = useLoggerStore((s) => s.entries);

  // Worked-before / new-grid sets for decode-table highlighting, memoized from
  // the logbook. NOTE: useLoggerStore.loadEntries caps at 100 entries, so these
  // miss older QSOs — a bulk "worked callsigns/grids" endpoint (or a larger take
  // for the workspace) is a follow-up (#1015).
  const workedCalls = useMemo(
    () => new Set(entries.map((e) => e.callsign.toUpperCase())),
    [entries],
  );
  const workedGrids = useMemo(
    () =>
      new Set(
        entries
          .filter((e) => e.grid)
          .map((e) => e.grid!.slice(0, 4).toUpperCase()),
      ),
    [entries],
  );

  // Live TX runner: owns the QSO sequencer + backend keyer, driven once per slot.
  // onLogQso fires exactly once per completed QSO (the sequencer's `logged`
  // latch). It reads band/dial live from the stores so the captured closure can
  // never log a stale band/frequency.
  const tx = useFt8TxRunner({
    myCall,
    myGrid: myGrid || null,
    mode: protocol,
    active: true,
    band,
    onLogQso: (state) => {
      const dialHz = useConnectionStore.getState().vfoHz ?? 0;
      const req = qsoStateToLogEntry(state, {
        band: useFt8Store.getState().band,
        freqMhz: dialHz / 1e6,
        mode: state.mode,
      });
      if (req) void useLoggerStore.getState().addLogEntry(req);
    },
  });

  // Manual LOG QSO — record the in-progress QSO on demand (same pure mapper).
  // Latches the controller's `logged` flag so the auto-log path can't write a
  // second identical row when the sequencer later completes the QSO. Bails if the
  // QSO has already been logged (manual or auto).
  const logCurrentQso = () => {
    if (tx.qso.logged) return;
    const req = qsoStateToLogEntry(tx.qso, {
      band,
      freqMhz: (vfoHz ?? 0) / 1e6,
      mode: protocol,
    });
    if (req) {
      void addLogEntry(req);
      tx.markLogged();
    }
  };

  // Hydrate the logbook once when the workspace opens. The mode-picker opens
  // this overlay directly, so without this the Activity Log and the
  // worked-before / new-grid highlighting stay empty until SpotsPanel mounts or
  // the first QSO is logged. addLogEntry re-loads after each write, so this only
  // covers the initial fetch.
  useEffect(() => {
    void useLoggerStore.getState().loadEntries();
  }, []);

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

  // Click a decode → start calling that station (we reply in the opposite slot).
  const onRowClick = (row: Ft8Row) => {
    if (!myCall) return; // need an operator call to generate Tx messages
    const secs = new Date(row.slotStartUnixMs).getUTCSeconds();
    const senderSlot = slotOf(secs, protocol);
    tx.callStation(row.text, senderSlot);
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
          <Ft8ReceivePanel
            runner={tx}
            rxFocusHz={rxFocusHz}
            setRxFocusHz={setRxFocusHz}
            myCall={myCall || undefined}
          />
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
              <Ft8DecodeTable
                myCall={myCall || undefined}
                workedCalls={workedCalls}
                workedGrids={workedGrids}
                onRowClick={onRowClick}
              />
            </div>
          </section>
          <section className="ft8-region ft8-region--log">
            <div className="ft8-region__head">Activity log</div>
            <div className="ft8-region__body">
              <Ft8ActivityLog
                onLogQso={logCurrentQso}
                canLog={!!tx.qso.dxCall && !tx.qso.logged}
              />
            </div>
          </section>
        </div>

        {/* Right — QSO / TX control */}
        <div className="ft8-ws-col">
          <section className="ft8-region ft8-region--grow">
            <div className="ft8-region__head">TX control · QSO</div>
            <div className="ft8-region__body">
              <Ft8TxControl runner={tx} myCall={myCall} myGrid={myGrid} />
            </div>
          </section>
          <section className="ft8-region">
            <div className="ft8-region__head">Stats</div>
            <div className="ft8-region__body">
              <Ft8Stats />
            </div>
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
          <SpottingIndicator kind="psk" />
        </span>
        <span>
          {protocol} native · {band}
        </span>
      </footer>
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
