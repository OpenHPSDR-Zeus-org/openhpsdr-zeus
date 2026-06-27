// SPDX-License-Identifier: GPL-2.0-or-later
//
// Ft8PopBody — the FT8/FT4 operating body hosted inside the floating
// DigitalWindow pop-out. This is a RE-HOUSING of the old full-screen
// Ft8Workspace: the live TX runner (sequencer → controller → backend keyer),
// the decode table, the TX-control cluster, click-to-call, and auto-log are
// moved here UNCHANGED — only the chrome around them changed from a full-screen
// shell to a draggable pop-out. The operator keeps their normal Zeus console
// (panadapter / waterfall / VFO / QRZ / logbook) underneath.
//
// REUSE WIRING: clicking a station and the live QSO partner both feed the
// operator's EXISTING QRZ panel via the workspace context's runQrzLookup — no
// new QRZ panel is built. QSOs auto-log to the existing logbook via the runner.

import { useEffect, useMemo, useRef, useState } from 'react';
import { useFt8Store, type Ft8Row } from '../../state/ft8-store';
import { useFt8TxStore } from '../../state/ft8-tx-store';
import { useConnectionStore } from '../../state/connection-store';
import { useOperatorStore } from '../../state/operator-store';
import { useFt8SettingsStore } from '../../state/ft8-settings-store';
import { DIGITAL_BANDS, nearestDigitalBand } from '../../dsp/digital-segments';
import { slotOf, type Slot } from '../../dsp/ft8-sequencer';
import { useFt8TxRunner } from '../../dsp/ft8-tx-runner';
import { qsoStateToLogEntry } from '../../dsp/ft8-qso-log';
import { parseFt8Message } from '../../dsp/ft8-message';
import { useLoggerStore } from '../../state/logger-store';
import { useWorkspace } from '../WorkspaceContext';
import { Ft8DecodeTable } from './Ft8DecodeTable';
import { Ft8TxControl } from './Ft8TxControl';
import { Ft8SettingsView } from './Ft8SettingsView';
import { Ft8DecodeLegend } from './Ft8DecodeLegend';

/** "Prompt before logging" gate. window.confirm is unavailable in headless/test
 *  contexts — default to logging there so the auto-log path never silently
 *  drops a QSO. */
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

export function Ft8PopBody() {
  const protocol = useFt8Store((s) => s.protocol);
  const band = useFt8Store((s) => s.band);
  const switchProtocol = useFt8Store((s) => s.switchProtocol);
  const qsyBand = useFt8Store((s) => s.qsyBand);

  // Live keyer status (0x3A) + our own TX echoes for the decode-flow interleave.
  const txEchoes = useFt8TxStore((s) => s.txEcho);

  // DECODE (the live operating view) vs SETTINGS (behind the gear).
  const [view, setView] = useState<'decode' | 'settings'>('decode');

  const vfoHz = useConnectionStore((s) => s.vfoHz);
  // Operator identity is server-authoritative — TX/gate on the RESOLVED value
  // (override else QRZ home) so a QRZ-home operator transmits without retyping.
  const myCall = useOperatorStore((s) => s.resolvedCall);
  const myGrid = useOperatorStore((s) => s.resolvedGrid);
  const hydrateOperator = useOperatorStore((s) => s.hydrate);

  // Persisted FT8 prefs — seed the TX controller defaults + drive the decode
  // filters and the auto-log gate.
  const settings = useFt8SettingsStore((s) => s.settings);
  const settingsHydrated = useFt8SettingsStore((s) => s.hydrated);

  const addLogEntry = useLoggerStore((s) => s.addLogEntry);
  const entries = useLoggerStore((s) => s.entries);

  // Click a station / live QSO partner → the operator's EXISTING QRZ panel.
  const { runQrzLookup } = useWorkspace();

  // Worked-before / new-grid sets for decode-table highlighting, memoized from
  // the logbook. NOTE: useLoggerStore.loadEntries caps at 100 entries (#1015).
  const workedCalls = useMemo(
    () => new Set(entries.map((e) => e.callsign.toUpperCase())),
    [entries],
  );
  const workedGrids = useMemo(
    () =>
      new Set(
        entries.filter((e) => e.grid).map((e) => e.grid!.slice(0, 4).toUpperCase()),
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
    seed: {
      audioHz: settings.defaultTxOffsetHz,
      slot: (settings.defaultTxSlot === 0 ? 'even' : 'odd') as Slot,
      holdTxFreq: settings.holdTxFreq,
      callFirst: settings.callFirst,
    },
    seedReady: settingsHydrated,
    behavior: {
      autoSequence: settings.autoSequence,
      disableTxAfter73: settings.disableTxAfter73,
      noReplyLimit: settings.callerMaxRetries,
      txAck: settings.rr73InsteadOfRrr ? 'RR73' : 'RRR',
    },
    onLogQso: (state) => {
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
  const logCurrentQso = () => {
    if (tx.qso.logged) return;
    const req = qsoStateToLogEntry(tx.qso, {
      band,
      freqMhz: (vfoHz ?? 0) / 1e6,
      mode: protocol,
    });
    if (req) {
      if (settings.reportToComment) req.comment = reportComment(req);
      void addLogEntry(req);
      tx.markLogged();
    }
  };

  // Hydrate the logbook + operator identity once when the pop-out opens.
  useEffect(() => {
    void useLoggerStore.getState().loadEntries();
    void hydrateOperator();
  }, [hydrateOperator]);

  // REUSE WIRING: when the live QSO partner changes (we answered a station, or a
  // station answered our CQ), populate the operator's existing QRZ panel.
  const dxCall = tx.qso.dxCall;
  const lastQrzCall = useRef<string | null>(null);
  useEffect(() => {
    if (dxCall && dxCall !== lastQrzCall.current) {
      lastQrzCall.current = dxCall;
      runQrzLookup(dxCall);
    }
    if (!dxCall) lastQrzCall.current = null;
  }, [dxCall, runQrzLookup]);

  // Band-follow while engaged: if the operator changes the MAIN band (BandButtons
  // retune the VFO out of this digital sub-band), re-QSY to the new band's
  // digital dial. nearestDigitalBand of a dial returns that same band, so the
  // entry/QSY does not loop.
  useEffect(() => {
    if (vfoHz <= 0) return;
    const near = nearestDigitalBand(vfoHz).name;
    if (near !== band) qsyBand(near);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [vfoHz]);

  // Click a decode → start calling that station (reply in the opposite slot) AND
  // populate the existing QRZ panel with the sender. With no operator call we
  // can't generate Tx messages, so jump to Settings to set the call.
  const onRowClick = (row: Ft8Row) => {
    if (!myCall) {
      setView('settings');
      return;
    }
    const secs = new Date(row.slotStartUnixMs).getUTCSeconds();
    const senderSlot = slotOf(secs, protocol);
    tx.callStation(row.text, senderSlot);
    const sender = parseFt8Message(row.text).deCall;
    if (sender) runQrzLookup(sender);
  };

  const bandsForProtocol = useMemo(
    () => DIGITAL_BANDS.filter((b) => (protocol === 'FT4' ? b.ft4Hz != null : b.ft8Hz != null)),
    [protocol],
  );

  if (view === 'settings') {
    return (
      <div className="dw-body dw-body--settings">
        <div className="dw-subhead">
          <button
            type="button"
            className="ft8-ws-tab"
            onClick={() => setView('decode')}
            title="Back to the decode view"
          >
            ← DECODE
          </button>
          <span className="dw-subhead__title">{protocol} SETTINGS</span>
        </div>
        <div className="dw-settings-scroll">
          <Ft8SettingsView />
        </div>
      </div>
    );
  }

  return (
    <div className="dw-body">
      {/* Sub-header: protocol tabs + identity + gear. */}
      <div className="dw-subhead">
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
        <button
          type="button"
          className={`ft8-ws-call${myCall ? '' : ' is-empty'}`}
          onClick={() => setView('settings')}
          title="Edit your callsign / grid in Settings"
        >
          {myCall ? `${myCall}${myGrid ? ` · ${myGrid}` : ''}` : 'SET CALL'}
        </button>
        <button
          type="button"
          className="dw-gear"
          onClick={() => setView('settings')}
          title="FT8 settings (decode depth, macros, logging)"
          aria-label="FT8 settings"
        >
          ⚙
        </button>
      </div>

      {/* Empty-call prompt — TX is gated on an operator callsign. */}
      {!myCall && (
        <div className="dw-banner" role="alert">
          <strong>Set your callsign to transmit.</strong> TX, macros and click-to-call stay
          disabled until your station call is set.
          <button type="button" className="dw-banner__cta" onClick={() => setView('settings')}>
            Settings →
          </button>
        </div>
      )}

      {/* Band row — click a band to re-QSY the MAIN radio's digital dial. */}
      <div className="ft8-band-grid dw-bands">
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

      <Ft8DecodeLegend />

      {/* Decode table — the live operating list (scrolls). */}
      <section className="dw-section dw-section--grow">
        <div className="ft8-region__head">
          Decoded messages
          {myCall ? <small> · click a station to call</small> : <small> · set your Call to enable calling</small>}
        </div>
        <div className="dw-section__body dw-section__body--flush">
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

      {/* TX control cluster — the on-air engine (re-housed, unchanged). */}
      <section className="dw-section">
        <div className="ft8-region__head">
          TX control · QSO
          {!!tx.qso.dxCall && !tx.qso.logged && (
            <button type="button" className="ft8-log__btn dw-logbtn" onClick={logCurrentQso}>
              LOG QSO
            </button>
          )}
        </div>
        <div className="dw-section__body">
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
    </div>
  );
}
