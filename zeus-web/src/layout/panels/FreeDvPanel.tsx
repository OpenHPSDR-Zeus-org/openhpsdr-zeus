// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

// Native FreeDV digital-voice panel. Reproduces the freedv-gui essentials —
// submode selector, SYNC lamp + SNR readout, SNR squelch, and the RX/TX text
// sidechannel — driven by GET /api/freedv/status (polled ~4 Hz) and
// PUT /api/freedv/config. FreeDV is a normal RxMode ('FREEDV'); selecting it
// from the mode row engages the modem (backend forces USB underneath). This
// panel is telemetry/config only — it does NOT select the mode itself.

import { useCallback, useEffect, useRef, useState } from 'react';
import {
  getFreeDvStatus,
  setFreeDvConfig,
  FREEDV_SUBMODES,
  type FreeDvStatusDto,
  type FreeDvSubmode,
  type FreeDvConfigRequest,
} from '../../api/client';
import { useConnectionStore } from '../../state/connection-store';
import { useQrzStore } from '../../state/qrz-store';

const POLL_MS = 250; // ~4 Hz, matches the brief.
const SNR_SQUELCH_MIN = -2;
const SNR_SQUELCH_MAX = 10;

export function FreeDvPanel() {
  const mode = useConnectionStore((s) => s.mode);
  const connected = useConnectionStore((s) => s.status === 'Connected');
  const inFreeDvMode = mode === 'FREEDV';

  // Operator's own callsign from the QRZ session — used to seed the FreeDV TX
  // text sidechannel (operators conventionally beacon their call there).
  const ownCallsign = useQrzStore((s) => s.home?.callsign ?? '').toUpperCase();

  // null = no successful fetch yet / endpoint unavailable.
  const [status, setStatus] = useState<FreeDvStatusDto | null>(null);
  const [reachable, setReachable] = useState(true);
  // Local TX-text edit buffer so the polled status frame doesn't clobber the
  // operator mid-type; committed to the backend on blur / Enter.
  const [txDraft, setTxDraft] = useState('');
  const txDirty = useRef(false);

  const pollAbort = useRef<AbortController | null>(null);
  const cfgAbort = useRef<AbortController | null>(null);

  useEffect(() => {
    let cancelled = false;

    const tick = () => {
      pollAbort.current?.abort();
      const ac = new AbortController();
      pollAbort.current = ac;
      getFreeDvStatus(ac.signal)
        .then((s) => {
          if (cancelled || ac.signal.aborted) return;
          setReachable(true);
          setStatus(s);
          if (!txDirty.current) setTxDraft(s.txText ?? '');
        })
        .catch(() => {
          if (cancelled || ac.signal.aborted) return;
          setReachable(false);
        });
    };

    tick();
    const id = window.setInterval(tick, POLL_MS);
    return () => {
      cancelled = true;
      window.clearInterval(id);
      pollAbort.current?.abort();
    };
  }, []);

  const sendConfig = useCallback((req: FreeDvConfigRequest) => {
    cfgAbort.current?.abort();
    const ac = new AbortController();
    cfgAbort.current = ac;
    setFreeDvConfig(req, ac.signal)
      .then((s) => {
        if (ac.signal.aborted) return;
        setReachable(true);
        setStatus(s);
        if (!txDirty.current) setTxDraft(s.txText ?? '');
      })
      .catch(() => {
        /* next poll reconciles */
      });
  }, []);

  useEffect(() => () => cfgAbort.current?.abort(), []);

  const commitTxText = useCallback(() => {
    txDirty.current = false;
    sendConfig({ txText: txDraft });
  }, [sendConfig, txDraft]);

  // Seed the TX text with the operator's QRZ callsign the first time we have a
  // status frame whose TX text is empty. Runs once per callsign so it never
  // clobbers text the operator (or backend) already set, and re-seeds if the
  // QRZ login changes. Commits to the backend so the call actually transmits.
  const seededCall = useRef<string | null>(null);
  useEffect(() => {
    if (!ownCallsign) return;
    if (!status || txDirty.current) return;
    if (seededCall.current === ownCallsign) return;
    if ((status.txText ?? '').trim() !== '') return;
    seededCall.current = ownCallsign;
    setTxDraft(ownCallsign);
    sendConfig({ txText: ownCallsign });
  }, [ownCallsign, status, sendConfig]);

  // Library missing — show the clear unavailable state instead of dead controls.
  if (status && !status.nativeAvailable) {
    return (
      <div className="dsp-cfg" style={{ gap: 8 }}>
        <FreeDvHeader status={status} reachable={reachable} inFreeDvMode={inFreeDvMode} />
        <div
          className="label-xs"
          style={{
            padding: '12px',
            border: '1px solid var(--line)',
            borderRadius: 'var(--r-sm)',
            background: 'var(--bg-1)',
            color: 'var(--fg-2)',
            lineHeight: 1.5,
          }}
        >
          FreeDV library not installed / building — telemetry unavailable. The
          codec2 modem isn't present in this build, so digital-voice decode and
          encode can't run yet.
          {status.libraryVersion ? ` (${status.libraryVersion})` : ''}
        </div>
      </div>
    );
  }

  // No reachable backend yet (404 / transient) and no prior frame — keep the
  // panel calm and explanatory rather than crashing or showing dead controls.
  if (!status) {
    return (
      <div className="dsp-cfg" style={{ gap: 8 }}>
        <FreeDvHeader status={null} reachable={reachable} inFreeDvMode={inFreeDvMode} />
        <div
          className="label-xs"
          style={{
            padding: '12px',
            border: '1px solid var(--line)',
            borderRadius: 'var(--r-sm)',
            background: 'var(--bg-1)',
            color: 'var(--fg-2)',
          }}
        >
          {reachable
            ? 'Querying FreeDV modem…'
            : 'FreeDV telemetry unavailable — the modem service is not responding.'}
        </div>
      </div>
    );
  }

  const ctrlsDisabled = !connected || !reachable;
  const snrThreshDisabled = ctrlsDisabled || !status.squelchEnabled;

  return (
    <div className="dsp-cfg" style={{ gap: 8 }}>
      <FreeDvHeader status={status} reachable={reachable} inFreeDvMode={inFreeDvMode} />

      {/* SYNC lamp + SNR readout. */}
      <div className="dsp-cfg-row">
        <span className="dsp-cfg-label">
          Sync
          <span className="dsp-cfg-hint"> modem lock</span>
        </span>
        <span style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
          <span
            aria-label={status.synced ? 'Synced' : 'Not synced'}
            style={{
              width: 12,
              height: 12,
              borderRadius: '50%',
              background: status.synced ? 'var(--accent)' : 'var(--bg-2)',
              border: '1px solid var(--line)',
              boxShadow: status.synced
                ? '0 0 6px var(--accent)'
                : 'none',
              opacity: status.synced ? 1 : 0.6,
            }}
          />
          <span className="mono dsp-cfg-unit">
            {status.synced ? 'LOCK' : '—'}
          </span>
        </span>
      </div>

      <div className="dsp-cfg-row">
        <span className="dsp-cfg-label">
          SNR
          <span className="dsp-cfg-hint"> RX estimate</span>
        </span>
        <span
          className="mono dsp-cfg-unit"
          style={{ color: status.synced ? 'var(--fg-0)' : 'var(--fg-3)' }}
        >
          {status.snrDb.toFixed(1)} dB
        </span>
      </div>

      {/* Submode selector. */}
      <div className="dsp-cfg-row">
        <span className="dsp-cfg-label">Submode</span>
        <div className="dsp-cfg-btns">
          {FREEDV_SUBMODES.map((m) => (
            <button
              key={m.value}
              type="button"
              disabled={ctrlsDisabled}
              onClick={() =>
                m.value !== status.submode && sendConfig({ submode: m.value as FreeDvSubmode })
              }
              className={`btn sm ${status.submode === m.value ? 'active' : ''}`}
              title={`FreeDV ${m.label}`}
            >
              {m.label}
            </button>
          ))}
        </div>
      </div>

      {/* SNR squelch toggle. */}
      <div className="dsp-cfg-row">
        <span className="dsp-cfg-label">
          SNR Squelch
          <span className="dsp-cfg-hint"> gate on lock</span>
        </span>
        <button
          type="button"
          disabled={ctrlsDisabled}
          aria-pressed={status.squelchEnabled}
          onClick={() => sendConfig({ squelchEnabled: !status.squelchEnabled })}
          className={`btn sm ${status.squelchEnabled ? 'active' : ''}`}
          title="Mute audio until the modem SNR clears the threshold"
        >
          {status.squelchEnabled ? 'ON' : 'OFF'}
        </button>
      </div>

      {/* SNR squelch threshold — disabled unless squelch is on. */}
      <label className="dsp-cfg-row">
        <span className="dsp-cfg-label">Threshold</span>
        <input
          type="range"
          min={SNR_SQUELCH_MIN}
          max={SNR_SQUELCH_MAX}
          step={0.5}
          value={status.snrSquelchThreshDb}
          disabled={snrThreshDisabled}
          title="SNR squelch threshold (dB)"
          onChange={(e) =>
            sendConfig({ snrSquelchThreshDb: Number(e.currentTarget.value) })
          }
          style={{
            flex: 1,
            cursor: snrThreshDisabled ? 'not-allowed' : 'pointer',
            accentColor: snrThreshDisabled ? 'var(--fg-3)' : 'var(--accent)',
            opacity: snrThreshDisabled ? 0.55 : 1,
          }}
        />
        <span
          className="mono dsp-cfg-unit"
          style={{ color: snrThreshDisabled ? 'var(--fg-3)' : undefined }}
        >
          {status.snrSquelchThreshDb.toFixed(1)} dB
        </span>
      </label>

      {/* RX text sidechannel (read-only). */}
      <label className="dsp-cfg-row">
        <span className="dsp-cfg-label">
          RX Text
          <span className="dsp-cfg-hint"> decoded</span>
        </span>
        <input
          type="text"
          className="mono"
          readOnly
          value={status.rxText ?? ''}
          placeholder={status.rxText == null ? '—' : ''}
          style={{
            flex: 1,
            minWidth: 0,
            background: 'var(--bg-1)',
            color: 'var(--fg-1)',
            border: '1px solid var(--line)',
            borderRadius: 'var(--r-sm)',
            padding: '3px 6px',
            fontSize: '11px',
          }}
        />
      </label>

      {/* TX text sidechannel — operator-editable, committed on blur/Enter. */}
      <label className="dsp-cfg-row">
        <span className="dsp-cfg-label">
          TX Text
          <span className="dsp-cfg-hint"> outgoing</span>
        </span>
        <input
          type="text"
          className="mono"
          disabled={ctrlsDisabled}
          value={txDraft}
          onChange={(e) => {
            txDirty.current = true;
            setTxDraft(e.currentTarget.value);
          }}
          onBlur={commitTxText}
          onKeyDown={(e) => {
            if (e.key === 'Enter') {
              e.preventDefault();
              commitTxText();
            }
          }}
          placeholder={ownCallsign || 'callsign / text'}
          style={{
            flex: 1,
            minWidth: 0,
            background: 'var(--btn-top)',
            color: 'var(--fg-0)',
            border: '1px solid var(--line)',
            borderRadius: 'var(--r-sm)',
            padding: '3px 6px',
            fontSize: '11px',
            opacity: ctrlsDisabled ? 0.55 : 1,
          }}
        />
      </label>

      <div
        className="label-xs"
        style={{ color: 'var(--fg-3)', lineHeight: 1.4, marginTop: 2 }}
      >
        FreeDV requires the radio in <strong>FreeDV</strong> mode — pick it from
        the mode row. The modem runs USB underneath automatically.
      </div>
    </div>
  );
}

function FreeDvHeader({
  status,
  reachable,
  inFreeDvMode,
}: {
  status: FreeDvStatusDto | null;
  reachable: boolean;
  inFreeDvMode: boolean;
}) {
  const active = status?.active ?? false;
  // Active when the modem is engaged AND the panel agrees the mode is FreeDV.
  const engaged = active && inFreeDvMode;
  const lampColor = engaged ? 'var(--accent)' : 'var(--bg-2)';
  return (
    <div
      className="dsp-cfg-row"
      style={{ alignItems: 'center', borderBottom: '1px solid var(--line)', paddingBottom: 6 }}
    >
      <span
        className="dsp-cfg-label"
        style={{ display: 'flex', alignItems: 'center', gap: 6 }}
      >
        <span
          aria-hidden
          style={{
            width: 8,
            height: 8,
            borderRadius: '50%',
            background: lampColor,
            border: '1px solid var(--line)',
            boxShadow: engaged ? '0 0 5px var(--accent)' : 'none',
          }}
        />
        FreeDV
      </span>
      <span className="mono dsp-cfg-unit" style={{ color: 'var(--fg-3)' }}>
        {!reachable
          ? 'unavailable'
          : engaged
            ? 'active'
            : inFreeDvMode
              ? 'engaging…'
              : 'idle'}
        {status?.libraryVersion ? ` · ${status.libraryVersion}` : ''}
      </span>
    </div>
  );
}
