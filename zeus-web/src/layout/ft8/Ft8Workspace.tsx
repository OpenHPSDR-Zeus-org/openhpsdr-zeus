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
import { useFt8TxStore } from '../../state/ft8-tx-store';
import { useConnectionStore } from '../../state/connection-store';
import { useOperatorStore } from '../../state/operator-store';
import { useFt8SettingsStore } from '../../state/ft8-settings-store';
import { DIGITAL_BANDS } from '../../dsp/digital-segments';
import { slotOf, type Slot } from '../../dsp/ft8-sequencer';
import { useFt8TxRunner } from '../../dsp/ft8-tx-runner';
import { qsoStateToLogEntry } from '../../dsp/ft8-qso-log';
import { useLoggerStore } from '../../state/logger-store';
import { Ft8DecodeTable } from './Ft8DecodeTable';
import { Ft8TxControl } from './Ft8TxControl';
import { Ft8ReceivePanel } from './Ft8ReceivePanel';
import { Ft8ActivityLog } from './Ft8ActivityLog';
import { Ft8Stats } from './Ft8Stats';
import { Ft8SettingsView } from './Ft8SettingsView';
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

/** "Prompt before logging" gate. Returns true when logging should proceed.
 *  window.confirm is unavailable in headless/test contexts — default to logging
 *  there so the auto-log path never silently drops a QSO. */
function confirmLog(dxCall: string | null): boolean {
  if (typeof window === 'undefined' || typeof window.confirm !== 'function') return true;
  return window.confirm(`Log QSO with ${dxCall ?? 'this station'}?`);
}

/** "dB report → comment": fold the exchanged reports into the QSO comment. */
function reportComment(req: { rstSent: string; rstRcvd: string }): string {
  const parts: string[] = [];
  if (req.rstSent) parts.push(`Sent ${req.rstSent}`);
  if (req.rstRcvd) parts.push(`Rcvd ${req.rstRcvd}`);
  return parts.join(' ');
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

  // Live keyer status (0x3A) + our own TX echoes for the decode-flow interleave.
  const txStatus = useFt8TxStore((s) => s.status);
  const txEchoes = useFt8TxStore((s) => s.txEcho);

  // DECODE (the live operating view) vs SETTINGS (the configuration page). The
  // protocol tabs (FT8/FT4) stay a separate switch; this is the view switch.
  const [view, setView] = useState<'decode' | 'settings'>('decode');

  const vfoHz = useConnectionStore((s) => s.vfoHz);
  const rxFocusHz = useFt8Store((s) => s.rxFocusHz);
  const setRxFocusHz = useFt8Store((s) => s.setRxFocusHz);
  // Operator identity is server-authoritative. We TX/gate on the RESOLVED value
  // (override else QRZ home) so a QRZ-home operator transmits without retyping;
  // the editable override fields live on the Settings page, not the header.
  const myCall = useOperatorStore((s) => s.resolvedCall);
  const myGrid = useOperatorStore((s) => s.resolvedGrid);
  const hydrateOperator = useOperatorStore((s) => s.hydrate);

  // Persisted FT8 prefs — seed the TX controller defaults + drive the decode
  // filters and the auto-log gate.
  const settings = useFt8SettingsStore((s) => s.settings);
  const settingsHydrated = useFt8SettingsStore((s) => s.hydrated);

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
    // Seed the controller's starting slot / offset / hold / call-first from the
    // operator's persisted FT8 Settings (applied at construction, re-applied once
    // the settings store reports hydrated to cover the construct-before-hydrate
    // race).
    seed: {
      audioHz: settings.defaultTxOffsetHz,
      slot: (settings.defaultTxSlot === 0 ? 'even' : 'odd') as Slot,
      holdTxFreq: settings.holdTxFreq,
      callFirst: settings.callFirst,
    },
    seedReady: settingsHydrated,
    // Live behaviour prefs — auto-sequence, disable-after-73, no-reply limit and
    // the RR73/RRR ack — applied live so a Settings edit takes effect mid-session.
    behavior: {
      autoSequence: settings.autoSequence,
      disableTxAfter73: settings.disableTxAfter73,
      noReplyLimit: settings.callerMaxRetries,
      txAck: settings.rr73InsteadOfRrr ? 'RR73' : 'RRR',
    },
    onLogQso: (state) => {
      // Respect the operator's logging preferences (FT8 Settings → Logging).
      const s = useFt8SettingsStore.getState().settings;
      if (!s.autoLog) return;
      if (s.promptBeforeLog && !confirmLog(state.dxCall)) return;
      const dialHz = useConnectionStore.getState().vfoHz ?? 0;
      const req = qsoStateToLogEntry(state, {
        band: useFt8Store.getState().band,
        freqMhz: dialHz / 1e6,
        mode: state.mode,
      });
      if (req) {
        if (s.reportToComment) req.comment = reportComment(req);
        void useLoggerStore.getState().addLogEntry(req);
      }
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
      // Manual log is an explicit action, so no prompt — but still honour the
      // "dB report → comment" preference for parity with the auto-log path.
      if (settings.reportToComment) req.comment = reportComment(req);
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
    // Refresh the shared operator identity on open — QRZ home may resolve after
    // the initial connect, and the override may have been edited elsewhere.
    void hydrateOperator();
  }, [hydrateOperator]);

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
  // With no operator call we can't generate Tx messages, so instead of silently
  // doing nothing, send the operator to Settings to set their call.
  const onRowClick = (row: Ft8Row) => {
    if (!myCall) {
      setView('settings');
      return;
    }
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
        <div className="ft8-ws-tabs" role="tablist" aria-label="Workspace view">
          {(['decode', 'settings'] as const).map((v) => (
            <button
              key={v}
              type="button"
              role="tab"
              aria-selected={view === v}
              className={`ft8-ws-tab${view === v ? ' is-active' : ''}`}
              onClick={() => setView(v)}
            >
              {v === 'decode' ? 'DECODE' : 'SETTINGS'}
            </button>
          ))}
        </div>
        {/* Compact, read-only identity chip — editing lives on the Settings page
            (out of this 44px clipping header). Click to jump to it. */}
        <button
          type="button"
          className={`ft8-ws-call${myCall ? '' : ' is-empty'}`}
          onClick={() => setView('settings')}
          title="Edit your callsign / grid in Settings"
        >
          {myCall ? `${myCall}${myGrid ? ` · ${myGrid}` : ''}` : 'SET CALL'}
        </button>
        <span className="ft8-ws-clock">{clock}</span>
        {/* Always-visible view toggle + Exit, pinned top-right OUT of the 44px
            header clip (the in-header DECODE/SETTINGS tabs can scroll under the
            clip on a narrow window; this guarantees both directions stay
            clickable at any width). */}
        <div className="ft8-ws-actions">
          <button
            type="button"
            className="ft8-ws-viewtoggle"
            onClick={() => setView(view === 'decode' ? 'settings' : 'decode')}
            title={view === 'decode' ? 'Open FT8 settings' : 'Back to the decode view'}
          >
            {view === 'decode' ? '⚙ SETTINGS' : '← DECODE'}
          </button>
          {onClose && (
            <button type="button" className="ft8-ws-close" onClick={onClose}>
              Exit · Esc
            </button>
          )}
        </div>
      </header>

      {view === 'settings' ? (
        <div className="ft8-ws-body ft8-ws-body--settings">
          <Ft8SettingsView />
        </div>
      ) : (
      <div className="ft8-ws-body">
        {/* Live-TX banner — surface what is going out THIS cycle (or staged next
            while armed) so it is unmistakable at a glance. Driven by the backend
            keyer status (0x3A), not local optimism. */}
        {txStatus && (txStatus.transmitting || txStatus.armed) && txStatus.message && (
          <div
            className={`ft8-ws-txbanner${txStatus.transmitting ? ' is-tx' : ''}`}
            role="status"
          >
            <span className="ft8-ws-txbanner__tag">
              {txStatus.transmitting ? '▶ TX' : 'TX ARMED'}
            </span>
            <span className="ft8-ws-txbanner__msg">{txStatus.message}</span>
            {txStatus.slot && (
              <span className="ft8-ws-txbanner__slot">
                {txStatus.slot === 'even' ? '1ST' : '2ND'} · {Math.round(txStatus.audioHz)} Hz
              </span>
            )}
          </div>
        )}
        {/* Empty-call prompt — TX is gated on an operator callsign, so make the
            reason visible instead of leaving ENABLE a silent dead button. */}
        {!myCall && (
          <div className="ft8-ws-banner" role="alert">
            <span className="ft8-ws-banner__text">
              <strong>Set your callsign to transmit.</strong> TX, macros and click-to-call stay
              disabled until your station call is set.
            </span>
            <button
              type="button"
              className="ft8-ws-banner__cta"
              onClick={() => setView('settings')}
            >
              Open Settings →
            </button>
          </div>
        )}
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
                showOnlyCq={settings.showOnlyCq}
                hideWorkedBefore={settings.hideWorkedBefore}
                txEchoes={txEchoes}
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
              <Ft8TxControl
                runner={tx}
                myCall={myCall}
                myGrid={myGrid}
                cqMessage={settings.cqMessage}
                cqDxMessage={settings.cqDxMessage}
                freeTextMacro={settings.freeTextMacro}
              />
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
      )}

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
