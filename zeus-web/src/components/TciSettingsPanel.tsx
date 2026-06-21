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
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

import { useEffect, useState } from 'react';
import { useTciStore } from '../state/tci-store';

export function TciSettingsPanel() {
  const config = useTciStore((s) => s.config);
  const status = useTciStore((s) => s.status);
  const testInFlight = useTciStore((s) => s.testInFlight);
  const lastTestResult = useTciStore((s) => s.lastTestResult);
  const saveConfig = useTciStore((s) => s.saveConfig);
  const test = useTciStore((s) => s.test);

  const [bindAddress, setBindAddress] = useState(config.bindAddress);
  const [port, setPort] = useState(String(config.port));
  const [enabled, setEnabled] = useState(config.enabled);
  const [saving, setSaving] = useState(false);

  // Rehydrate the form when the store finishes reading localStorage after mount.
  useEffect(() => {
    setBindAddress(config.bindAddress);
    setPort(String(config.port));
    setEnabled(config.enabled);
  }, [config.bindAddress, config.port, config.enabled]);

  const currentlyEnabled = status?.currentlyEnabled ?? false;
  const clientCount = status?.clientCount ?? 0;
  const requiresRestart = status?.requiresRestart ?? false;
  const portAvailable = status?.portAvailable ?? true;

  async function onSave(e: React.FormEvent) {
    e.preventDefault();
    setSaving(true);
    try {
      const portNum = Number(port);
      if (!Number.isFinite(portNum) || portNum <= 0 || portNum >= 65536) return;
      await saveConfig({
        enabled,
        bindAddress: bindAddress.trim() || '127.0.0.1',
        port: portNum,
      });
    } finally {
      setSaving(false);
    }
  }

  async function onTest() {
    const portNum = Number(port);
    if (!Number.isFinite(portNum) || portNum <= 0 || portNum >= 65536) return;
    await test(bindAddress.trim() || '127.0.0.1', portNum);
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
        TCI (TRANSCEIVER CONTROL INTERFACE)
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
        TCI is an ExpertSDR3-compatible WebSocket protocol for remote control by logging software
        (Log4OM, N1MM+), digital mode apps (JTDX, WSJT-X), and other SDR display tools.
        Port changes require restarting Zeus to take effect.
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
          <span style={{ fontSize: 11, fontWeight: 600, color: 'var(--fg-2)' }}>Bind Address</span>
          <input
            type="text"
            value={bindAddress}
            onChange={(e) => setBindAddress(e.target.value)}
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
            Use 127.0.0.1 for localhost only, or 0.0.0.0 to allow LAN clients (no authentication)
          </span>
        </label>

        <label style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
          <span style={{ fontSize: 11, fontWeight: 600, color: 'var(--fg-2)' }}>Port</span>
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
            Default: 40001 (ExpertSDR3 standard). Changing requires restart.
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

        {lastTestResult && (
          <div
            style={{
              padding: 10,
              fontSize: 12,
              color: lastTestResult.ok ? 'var(--accent)' : 'var(--tx)',
              background: lastTestResult.ok
                ? 'rgba(74, 158, 255, 0.1)'
                : 'rgba(230, 58, 43, 0.1)',
              border: `1px solid ${lastTestResult.ok ? 'var(--accent)' : 'var(--tx)'}`,
              borderRadius: 'var(--r-sm)',
            }}
          >
            {lastTestResult.ok
              ? `✓ Port ${port} is available on ${bindAddress}`
              : `✗ Test failed: ${lastTestResult.error ?? 'unknown error'}`}
          </div>
        )}

        {requiresRestart && (
          <div
            style={{
              padding: 10,
              fontSize: 12,
              color: 'var(--power)',
              background: 'rgba(255, 201, 58, 0.1)',
              border: '1px solid var(--power)',
              borderRadius: 'var(--r-sm)',
              fontWeight: 600,
            }}
          >
            ⚠ Configuration changed — restart Zeus to apply
          </div>
        )}

        {currentlyEnabled && !requiresRestart && (
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
                background: portAvailable ? 'var(--accent)' : 'var(--tx)',
                flexShrink: 0,
              }}
            />
            <span style={{ color: 'var(--fg-2)' }}>Status:</span>
            <span style={{ fontWeight: 600, color: 'var(--accent)' }}>
              {portAvailable ? 'Running' : 'Port unavailable'}
            </span>
            <span style={{ color: 'var(--fg-2)' }}>·</span>
            <span style={{ color: 'var(--fg-2)' }}>Port:</span>
            <span style={{ fontFamily: 'monospace', fontWeight: 600 }}>
              {status?.currentPort ?? 40001}
            </span>
            <span style={{ color: 'var(--fg-2)' }}>·</span>
            <span style={{ color: 'var(--fg-2)' }}>Clients:</span>
            <span style={{ fontFamily: 'monospace', fontWeight: 600, color: clientCount > 0 ? 'var(--accent)' : 'var(--fg-2)' }}>
              {clientCount}
            </span>
          </div>
        )}

        {!currentlyEnabled && !requiresRestart && (
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
                background: 'var(--fg-3)',
                flexShrink: 0,
              }}
            />
            TCI is currently disabled
          </div>
        )}

        <div style={{ display: 'flex', gap: 6 }}>
          <button
            type="button"
            onClick={onTest}
            disabled={testInFlight}
            className="btn sm"
          >
            {testInFlight ? 'TESTING…' : 'TEST PORT'}
          </button>
          <span style={{ flex: 1 }} />
          <button type="submit" disabled={saving} className="btn sm active">
            {saving ? 'SAVING…' : 'SAVE'}
          </button>
        </div>

        <div
          style={{
            fontSize: 10,
            lineHeight: 1.4,
            color: 'var(--fg-3)',
          }}
        >
          TCI clients connect via <span style={{ fontFamily: 'monospace' }}>ws://host:port/</span>.
          Example: Log4OM TCI settings → enable TCI, host = Zeus IP, port = {port || '40001'}.
          Settings are saved locally; changing the port or bind address requires restarting Zeus.
        </div>
      </form>
    </div>
  );
}
