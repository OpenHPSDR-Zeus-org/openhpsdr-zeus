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
  getFreeDvInstallStatus,
  startFreeDvInstall,
  FREEDV_SUBMODES,
  type FreeDvStatusDto,
  type FreeDvSubmode,
  type FreeDvConfigRequest,
  type FreeDvInstallStatusDto,
} from '../../api/client';
import { useConnectionStore } from '../../state/connection-store';
import { useQrzStore } from '../../state/qrz-store';

const POLL_MS = 250; // ~4 Hz, matches the brief.
const SNR_SQUELCH_MIN = -2;
const SNR_SQUELCH_MAX = 10;

export function FreeDvPanel() {
  const mode = useConnectionStore((s) => s.mode);
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

  // Library missing — offer the one-click install instead of dead controls.
  if (status && !status.nativeAvailable) {
    return (
      <div className="dsp-cfg" style={{ gap: 8, padding: '10px 12px', overflowY: 'auto' }}>
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
          The codec2 modem isn't installed yet, so FreeDV decode/encode can't run.
          Install it once and FreeDV works every time you pick the mode.
        </div>
        <FreeDvInstallSection />
      </div>
    );
  }

  // No reachable backend yet (404 / transient) and no prior frame — keep the
  // panel calm and explanatory rather than crashing or showing dead controls.
  if (!status) {
    return (
      <div className="dsp-cfg" style={{ gap: 8, padding: '10px 12px', overflowY: 'auto' }}>
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

  // This panel is entirely REST-driven (getFreeDvStatus / setFreeDvConfig), so
  // the controls only need the backend to be reachable — NOT a live SignalR hub
  // connection to a radio. Gating on the hub status locked AUTO/submode/squelch
  // whenever the hub wasn't 'Connected' (e.g. just after a backend restart) even
  // though the modem service was fully reachable. The FreeDV modem config is
  // independent of the radio link, so reachability is the correct gate.
  const ctrlsDisabled = !reachable;
  const snrThreshDisabled = ctrlsDisabled || !status.squelchEnabled;

  return (
    <div className="dsp-cfg" style={{ gap: 8, padding: '10px 12px', overflowY: 'auto' }}>
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

      {/* Submode selector + auto-detect. AUTO scans submodes until one locks;
          picking a specific mode asserts it and turns AUTO off (backend does the
          same implicitly). While scanning, the active button is the mode the
          scanner is currently trying. */}
      <div className="dsp-cfg-row">
        <span className="dsp-cfg-label">
          Submode
          {status.autoDetect && (
            <span className="dsp-cfg-hint">
              {' '}
              {status.synced ? 'auto · locked' : 'auto · scanning…'}
            </span>
          )}
        </span>
        <div className="dsp-cfg-btns">
          <button
            type="button"
            disabled={ctrlsDisabled}
            aria-pressed={status.autoDetect}
            onClick={() => sendConfig({ autoDetect: !status.autoDetect })}
            className={`btn sm ${status.autoDetect ? 'active' : ''}`}
            title="Auto-detect: cycle submodes until the modem locks onto the received signal"
          >
            AUTO
          </button>
          {FREEDV_SUBMODES.map((m) => {
            const isCurrent = status.submode === m.value;
            // RADEV1 needs the native RADE library, which isn't integrated yet —
            // mark it as not-ready but still selectable so the operator sees the
            // explanatory notice rather than a silently dead button.
            const radeUnavailable = m.rade === true && !status.radeAvailable;
            // When scanning, dim the 'active' look on the tried mode so AUTO is
            // visually the engaged control, not the transient submode.
            const cls =
              isCurrent && (!status.autoDetect || status.synced) ? 'active' : '';
            return (
              <button
                key={m.value}
                type="button"
                disabled={ctrlsDisabled}
                onClick={() =>
                  sendConfig({ submode: m.value as FreeDvSubmode, autoDetect: false })
                }
                className={`btn sm ${cls}`}
                title={
                  radeUnavailable
                    ? 'RADEV1 — neural Radio Autoencoder (decoder not installed yet)'
                    : `FreeDV ${m.label}${isCurrent && status.autoDetect && !status.synced ? ' (scanning)' : ''}`
                }
                style={
                  isCurrent && status.autoDetect && !status.synced
                    ? { outline: '1px dashed var(--accent)', outlineOffset: -1 }
                    : radeUnavailable
                      ? { opacity: 0.7 }
                      : undefined
                }
              >
                {m.label}
                {radeUnavailable ? ' •' : ''}
              </button>
            );
          })}
        </div>
      </div>

      {/* RADEV1 selected but the native RADE library isn't integrated yet — be
          explicit so the operator understands why it won't decode, instead of
          chasing a "silent" mode the way a missing codec2 would look. */}
      {status.submode === 'RadeV1' && !status.radeAvailable && (
        <div
          className="label-xs"
          style={{
            padding: '8px 10px',
            border: '1px solid var(--line)',
            borderRadius: 'var(--r-sm)',
            background: 'var(--bg-1)',
            color: 'var(--fg-2)',
            lineHeight: 1.45,
          }}
        >
          <strong>RADEV1</strong> is FreeDV's neural (Radio Autoencoder) mode. Its
          decoder isn't built into Zeus yet, so this mode won't produce audio.
          Use <strong>700D / 700E / 1600</strong> for now — RADE support is in
          progress.
        </div>
      )}

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

// One-click codec2 install. The backend downloads the prebuilt modem Zeus
// committed for this platform and reloads it live (see FreeDvNativeInstaller);
// once it reports installed, the panel's status poll flips nativeAvailable and
// this whole branch is replaced by the live controls — so the "done" state here
// is only ever momentary.
function FreeDvInstallSection() {
  const [install, setInstall] = useState<FreeDvInstallStatusDto | null>(null);
  const timerRef = useRef<number | null>(null);
  const pollAbort = useRef<AbortController | null>(null);

  const phase = install?.phase ?? 'idle';
  const busy = phase === 'downloading' || phase === 'staging';
  const failed = phase === 'failed';
  const done = phase === 'done';

  const stopTimer = useCallback(() => {
    if (timerRef.current != null) {
      window.clearInterval(timerRef.current);
      timerRef.current = null;
    }
  }, []);

  const poll = useCallback(() => {
    pollAbort.current?.abort();
    const ac = new AbortController();
    pollAbort.current = ac;
    getFreeDvInstallStatus(ac.signal)
      .then((s) => {
        if (ac.signal.aborted) return;
        setInstall(s);
        // Terminal phase (or already installed) — stop polling.
        if (s.installed || (s.phase !== 'downloading' && s.phase !== 'staging')) stopTimer();
      })
      .catch(() => {
        /* next tick retries */
      });
  }, [stopTimer]);

  const start = useCallback(() => {
    startFreeDvInstall()
      .then((s) => {
        setInstall(s);
        if (s.phase === 'downloading' || s.phase === 'staging') {
          stopTimer();
          timerRef.current = window.setInterval(poll, 500);
        }
      })
      .catch(() => setInstall({ phase: 'failed', percent: 0, message: 'Could not reach the install service.', installed: false }));
  }, [poll, stopTimer]);

  useEffect(
    () => () => {
      stopTimer();
      pollAbort.current?.abort();
    },
    [stopTimer],
  );

  const label = busy
    ? `Installing… ${install?.percent ?? 0}%`
    : failed
      ? 'Retry install'
      : done
        ? 'Starting modem…'
        : 'Install FreeDV';

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
      <button
        type="button"
        className="btn sm active"
        disabled={busy || done}
        onClick={() => {
          if (!busy && !done) start();
        }}
        title="Download the FreeDV (codec2) modem for this platform and enable it"
        style={{ whiteSpace: 'nowrap', alignSelf: 'flex-start' }}
      >
        {label}
      </button>

      {(busy || failed) && (
        <div
          role="status"
          aria-live="polite"
          style={{
            display: 'flex',
            flexDirection: 'column',
            gap: 6,
            padding: '8px 10px',
            background: 'var(--bg-1)',
            border: '1px solid var(--line)',
            borderRadius: 'var(--r-sm)',
          }}
        >
          {busy && (
            <div
              aria-hidden
              style={{
                height: 4,
                borderRadius: 2,
                background: 'var(--bg-2)',
                overflow: 'hidden',
              }}
            >
              <div
                style={{
                  height: '100%',
                  width: `${install?.percent ?? 0}%`,
                  background: 'var(--accent)',
                  transition: 'width 0.2s ease',
                }}
              />
            </div>
          )}
          {install?.message && (
            <div
              className="label-xs"
              style={{ color: failed ? 'var(--tx)' : 'var(--fg-2)', lineHeight: 1.4 }}
            >
              {install.message}
            </div>
          )}
        </div>
      )}
    </div>
  );
}
