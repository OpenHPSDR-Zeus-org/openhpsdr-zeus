// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import { useEffect, useState } from 'react';
import { useWsjtxStore } from '../state/wsjtx-store';

// Outbound QSO-record push: when Zeus's native FT8/FT4 completes a QSO it can
// emit a WSJT-X type-12 LoggedADIF datagram to a logger (JTAlert / Log4OM /
// GridTracker / N1MM). New network egress — DISABLED by default. (Rig control —
// reading freq/mode, keying PTT — already works today via CAT or TCI; this is
// the missing other direction.)
export function WsjtxSettingsPanel() {
  const config = useWsjtxStore((s) => s.config);
  const status = useWsjtxStore((s) => s.status);
  const saveConfig = useWsjtxStore((s) => s.saveConfig);

  const [host, setHost] = useState(config.host);
  const [port, setPort] = useState(String(config.port));
  const [enabled, setEnabled] = useState(config.enabled);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    setHost(config.host);
    setPort(String(config.port));
    setEnabled(config.enabled);
  }, [config.host, config.port, config.enabled]);

  async function onSave(e: React.FormEvent) {
    e.preventDefault();
    setSaving(true);
    try {
      const portNum = Number(port);
      if (!Number.isFinite(portNum) || portNum <= 0 || portNum >= 65536) return;
      await saveConfig({ enabled, host: host.trim() || '127.0.0.1', port: portNum });
    } finally {
      setSaving(false);
    }
  }

  const currentlyEnabled = status?.enabled ?? false;

  return (
    <div style={{ maxWidth: 700 }}>
      <h3
        style={{
          margin: '0 0 14px',
          fontSize: 11,
          fontWeight: 700,
          letterSpacing: '0.12em',
          textTransform: 'uppercase',
          color: 'var(--fg-2)',
        }}
      >
        WSJT-X LOGGED-QSO BROADCAST
      </h3>

      <div
        style={{
          padding: 12,
          marginBottom: 16,
          fontSize: 11,
          lineHeight: 1.5,
          color: 'var(--fg-2)',
          background: 'var(--bg-0)',
          border: '1px solid var(--panel-border)',
          borderRadius: 'var(--r-sm)',
        }}
      >
        When enabled, each QSO logged in Zeus's native FT8/FT4 (auto-logged or via
        LOG QSO) is sent as a WSJT-X "logged ADIF" UDP datagram so{' '}
        <strong>JTAlert, Log4OM, GridTracker, and N1MM+</strong> pick it up. Point your
        logger's WSJT-X / UDP input at the host:port below (the WSJT-X default is{' '}
        <span style={{ fontFamily: 'monospace' }}>127.0.0.1:2237</span>). This sends QSO
        records OUT only — it does not control the radio. Rig control (freq/mode/PTT) is
        already available via CAT or TCI above. ADIF file imports are never broadcast.
      </div>

      <form onSubmit={onSave} style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
        <label style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
          <input
            type="checkbox"
            checked={enabled}
            onChange={(e) => setEnabled(e.target.checked)}
            style={{ accentColor: 'var(--accent)' }}
          />
          <span style={{ fontSize: 12, fontWeight: 600, color: 'var(--fg-1)' }}>Enabled</span>
        </label>

        <label style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
          <span style={{ fontSize: 11, fontWeight: 600, color: 'var(--fg-2)' }}>Target Host</span>
          <input
            type="text"
            value={host}
            onChange={(e) => setHost(e.target.value)}
            spellCheck={false}
            placeholder="127.0.0.1"
            style={{
              padding: '6px 8px',
              fontSize: 12,
              fontFamily: 'monospace',
              background: 'var(--bg-0)',
              border: '1px solid var(--panel-border)',
              borderRadius: 'var(--r-sm)',
              color: 'var(--fg-0)',
            }}
          />
          <span style={{ fontSize: 10, color: 'var(--fg-3)' }}>
            127.0.0.1 sends to a logger on this machine; use the logger's LAN IP otherwise.
            Datagrams are sent unauthenticated — keep this on a trusted network.
          </span>
        </label>

        <label style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
          <span style={{ fontSize: 11, fontWeight: 600, color: 'var(--fg-2)' }}>Target Port</span>
          <input
            type="number"
            value={port}
            onChange={(e) => setPort(e.target.value)}
            min={1}
            max={65535}
            style={{
              padding: '6px 8px',
              fontSize: 12,
              fontFamily: 'monospace',
              background: 'var(--bg-0)',
              border: '1px solid var(--panel-border)',
              borderRadius: 'var(--r-sm)',
              color: 'var(--fg-0)',
            }}
          />
          <span style={{ fontSize: 10, color: 'var(--fg-3)' }}>
            Default: 2237 (the WSJT-X UDP port most loggers listen on). Applies immediately —
            no restart required.
          </span>
        </label>

        <div
          style={{
            padding: 10,
            background: 'var(--bg-0)',
            border: '1px solid var(--panel-border)',
            borderRadius: 'var(--r-sm)',
            display: 'flex',
            alignItems: 'center',
            gap: 10,
            fontSize: 12,
            color: 'var(--fg-2)',
          }}
        >
          <span
            style={{
              width: 8,
              height: 8,
              borderRadius: '50%',
              background: currentlyEnabled ? 'var(--accent)' : 'var(--fg-3)',
              flexShrink: 0,
            }}
          />
          {currentlyEnabled
            ? `Broadcasting logged QSOs to ${status?.host}:${status?.port}`
            : 'WSJT-X broadcast is currently disabled'}
        </div>

        <div style={{ display: 'flex', gap: 6 }}>
          <span style={{ flex: 1 }} />
          <button type="submit" disabled={saving} className="btn sm active">
            {saving ? 'SAVING…' : 'SAVE'}
          </button>
        </div>
      </form>
    </div>
  );
}
