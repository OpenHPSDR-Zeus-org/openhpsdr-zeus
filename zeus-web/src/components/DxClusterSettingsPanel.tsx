// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

import { useEffect, useState } from 'react';
import { useDxClusterStore } from '../state/dxcluster-store';
import type { DxClusterConnectionState } from '../api/dxcluster';

const inputStyle: React.CSSProperties = {
  padding: '6px 8px',
  fontSize: 12,
  fontFamily: 'monospace',
  background: 'var(--bg-0)',
  border: '1px solid var(--panel-border)',
  borderRadius: 'var(--r-sm)',
  color: 'var(--fg-0)',
};

function stateColor(state: DxClusterConnectionState): string {
  switch (state) {
    case 'Connected':
      return 'var(--accent)';
    case 'Connecting':
    case 'Reconnecting':
      return 'var(--power)';
    default:
      return 'var(--fg-3)';
  }
}

export function DxClusterSettingsPanel() {
  const config = useDxClusterStore((s) => s.config);
  const status = useDxClusterStore((s) => s.status);
  const saveConfig = useDxClusterStore((s) => s.saveConfig);
  const connect = useDxClusterStore((s) => s.connect);
  const disconnect = useDxClusterStore((s) => s.disconnect);

  const [enabled, setEnabled] = useState(config.enabled);
  const [host, setHost] = useState(config.host);
  const [port, setPort] = useState(String(config.port));
  const [callsign, setCallsign] = useState(config.callsign);
  const [password, setPassword] = useState(config.password);
  const [loginCommands, setLoginCommands] = useState(config.loginCommands);
  const [autoConnect, setAutoConnect] = useState(config.autoConnect);
  const [saving, setSaving] = useState(false);
  const [busy, setBusy] = useState(false);
  const [portError, setPortError] = useState<string | null>(null);

  // Rehydrate the form when the store syncs from the backend.
  useEffect(() => {
    setEnabled(config.enabled);
    setHost(config.host);
    setPort(String(config.port));
    setCallsign(config.callsign);
    setLoginCommands(config.loginCommands);
    setAutoConnect(config.autoConnect);
  }, [config.enabled, config.host, config.port, config.callsign, config.loginCommands, config.autoConnect]);

  const state: DxClusterConnectionState = status?.state ?? 'Disconnected';
  const spotsReceived = status?.spotsReceived ?? 0;
  const connected = state === 'Connected';

  async function onSave(e: React.FormEvent) {
    e.preventDefault();
    const portNum = Number(port);
    if (!Number.isInteger(portNum) || portNum <= 0 || portNum >= 65536) {
      // Don't silently swallow an out-of-range / pasted value — tell the operator
      // why SAVE did nothing instead of leaving a dead button.
      setPortError('Port must be a whole number between 1 and 65535.');
      return;
    }
    setPortError(null);
    setSaving(true);
    try {
      await saveConfig({
        enabled,
        host: host.trim(),
        port: portNum,
        callsign: callsign.trim().toUpperCase(),
        password,
        loginCommands,
        autoConnect,
      });
    } finally {
      setSaving(false);
    }
  }

  async function onConnect() {
    setBusy(true);
    try {
      await connect();
    } finally {
      setBusy(false);
    }
  }

  async function onDisconnect() {
    setBusy(true);
    try {
      await disconnect();
    } finally {
      setBusy(false);
    }
  }

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
        DX CLUSTER (TELNET)
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
        Connect directly to a Telnet DX cluster (DXSpider / AR-Cluster / CC-Cluster). Spots land on
        the panadapter automatically — no Cluster-TCI bridge needed. Enter the cluster host and your
        callsign; add a password only if your cluster requires one.
      </div>

      {/* noValidate: bypass the browser's native constraint-validation bubble for
          the number field (its max would silently block submit and the operator
          never learns why). We validate the port in onSave and surface an
          in-app, in-palette message instead. */}
      <form noValidate onSubmit={onSave} style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
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
          <span style={{ fontSize: 11, fontWeight: 600, color: 'var(--fg-2)' }}>Host</span>
          <input
            type="text"
            value={host}
            onChange={(e) => setHost(e.target.value)}
            spellCheck={false}
            placeholder="dxc.example.org"
            style={inputStyle}
          />
        </label>

        <label style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
          <span style={{ fontSize: 11, fontWeight: 600, color: 'var(--fg-2)' }}>Port</span>
          <input
            type="number"
            value={port}
            onChange={(e) => {
              setPort(e.target.value);
              if (portError) setPortError(null);
            }}
            min={1}
            max={65535}
            style={portError ? { ...inputStyle, borderColor: 'var(--tx)' } : inputStyle}
          />
          {portError ? (
            <span style={{ fontSize: 10, color: 'var(--tx)' }}>{portError}</span>
          ) : (
            <span style={{ fontSize: 10, color: 'var(--fg-3)' }}>
              Default: 7373. Common cluster telnet ports are 7300, 7373, and 8000.
            </span>
          )}
        </label>

        <label style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
          <span style={{ fontSize: 11, fontWeight: 600, color: 'var(--fg-2)' }}>Callsign</span>
          <input
            type="text"
            value={callsign}
            onChange={(e) => setCallsign(e.target.value)}
            spellCheck={false}
            placeholder="K1ABC"
            style={{ ...inputStyle, textTransform: 'uppercase' }}
          />
          <span style={{ fontSize: 10, color: 'var(--fg-3)' }}>
            Sent automatically when the cluster asks for your call.
          </span>
        </label>

        <label style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
          <span style={{ fontSize: 11, fontWeight: 600, color: 'var(--fg-2)' }}>
            Password <span style={{ color: 'var(--fg-3)', fontWeight: 400 }}>(optional)</span>
          </span>
          <input
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            spellCheck={false}
            placeholder={status?.hasPassword ? '•••••••• (saved)' : 'leave blank if not required'}
            autoComplete="off"
            style={inputStyle}
          />
        </label>

        <label style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
          <span style={{ fontSize: 11, fontWeight: 600, color: 'var(--fg-2)' }}>
            Login commands <span style={{ color: 'var(--fg-3)', fontWeight: 400 }}>(optional, one per line)</span>
          </span>
          <textarea
            value={loginCommands}
            onChange={(e) => setLoginCommands(e.target.value)}
            spellCheck={false}
            rows={3}
            placeholder={'set/filter on\nset/dx/extension'}
            style={{ ...inputStyle, resize: 'vertical' }}
          />
          <span style={{ fontSize: 10, color: 'var(--fg-3)' }}>
            Sent once after login (e.g. spot filters).
          </span>
        </label>

        <label style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
          <input
            type="checkbox"
            checked={autoConnect}
            onChange={(e) => setAutoConnect(e.target.checked)}
            style={{ accentColor: 'var(--accent)' }}
          />
          <span style={{ fontSize: 12, fontWeight: 600, color: 'var(--fg-1)' }}>
            Connect automatically on startup
          </span>
        </label>

        {status?.error && (
          <div
            style={{
              padding: 10,
              fontSize: 12,
              color: 'var(--tx)',
              background: 'rgba(230, 58, 43, 0.1)',
              border: '1px solid var(--tx)',
              borderRadius: 'var(--r-sm)',
            }}
          >
            {status.error}
          </div>
        )}

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
            color: 'var(--fg-1)',
          }}
        >
          <span
            style={{
              width: 8,
              height: 8,
              borderRadius: '50%',
              background: stateColor(state),
              flexShrink: 0,
            }}
          />
          <span style={{ color: 'var(--fg-2)' }}>Status:</span>
          <span style={{ fontWeight: 600, color: stateColor(state) }}>{state}</span>
          <span style={{ color: 'var(--fg-2)' }}>·</span>
          <span style={{ color: 'var(--fg-2)' }}>Spots:</span>
          <span style={{ fontFamily: 'monospace', fontWeight: 600 }}>{spotsReceived}</span>
          {status?.lastSpotCallsign && (
            <>
              <span style={{ color: 'var(--fg-2)' }}>·</span>
              <span style={{ color: 'var(--fg-2)' }}>Last:</span>
              <span style={{ fontFamily: 'monospace', fontWeight: 600 }}>{status.lastSpotCallsign}</span>
            </>
          )}
        </div>

        <div style={{ display: 'flex', gap: 6 }}>
          <button
            type="button"
            onClick={onConnect}
            disabled={busy || connected || !enabled}
            className="btn sm"
          >
            CONNECT
          </button>
          <button
            type="button"
            onClick={onDisconnect}
            disabled={busy || state === 'Disconnected'}
            className="btn sm"
          >
            DISCONNECT
          </button>
          <span style={{ flex: 1 }} />
          <button type="submit" disabled={saving} className="btn sm active">
            {saving ? 'SAVING…' : 'SAVE'}
          </button>
        </div>

        <div style={{ fontSize: 10, lineHeight: 1.4, color: 'var(--fg-3)' }}>
          Spots appear on the panadapter through the same overlay as TCI spots. Save your settings,
          then Connect. Enabling “Connect automatically on startup” reconnects on the next launch.
        </div>
      </form>
    </div>
  );
}
