// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.

import { useEffect, useState } from 'react';
import { useRotatorStore } from '../state/rotator-store';

export function RotatorSettingsPanel() {
  const config = useRotatorStore((s) => s.config);
  const status = useRotatorStore((s) => s.status);
  const testInFlight = useRotatorStore((s) => s.testInFlight);
  const lastTestResult = useRotatorStore((s) => s.lastTestResult);
  const saveConfig = useRotatorStore((s) => s.saveConfig);
  const stop = useRotatorStore((s) => s.stop);
  const test = useRotatorStore((s) => s.test);

  const [host, setHost] = useState(config.host);
  const [port, setPort] = useState(String(config.port));
  const [enabled, setEnabled] = useState(config.enabled);
  const [saving, setSaving] = useState(false);

  // Rehydrate the form when the store finishes reading localStorage after mount.
  useEffect(() => {
    setHost(config.host);
    setPort(String(config.port));
    setEnabled(config.enabled);
  }, [config.host, config.port, config.enabled]);

  const connected = !!status?.connected;
  const moving = !!status?.moving;
  const currentAz = status?.currentAz;
  const targetAz = status?.targetAz;

  async function onSave(e: React.FormEvent) {
    e.preventDefault();
    setSaving(true);
    try {
      const portNum = Number(port);
      if (!Number.isFinite(portNum) || portNum <= 0 || portNum >= 65536) return;
      await saveConfig({
        enabled,
        host: host.trim() || '127.0.0.1',
        port: portNum,
        pollingIntervalMs: config.pollingIntervalMs,
      });
    } finally {
      setSaving(false);
    }
  }

  async function onTest() {
    const portNum = Number(port);
    if (!Number.isFinite(portNum) || portNum <= 0 || portNum >= 65536) return;
    await test(host.trim() || '127.0.0.1', portNum);
  }

  return (
    <div style={{ maxWidth: 600 }}>
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
        ROTCTLD (HAMLIB ROTATOR)
      </h3>

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
          <span style={{ fontSize: 11, fontWeight: 600, color: 'var(--fg-2)' }}>Host</span>
          <input
            type="text"
            value={host}
            onChange={(e) => setHost(e.target.value)}
            spellCheck={false}
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
              ? `✓ Test OK — rotctld is reachable at ${host}:${port}`
              : `✗ Test failed: ${lastTestResult.error ?? 'unknown error'}`}
          </div>
        )}

        {connected && (
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
            <span style={{ color: 'var(--fg-2)' }}>Current:</span>
            <span style={{ fontFamily: 'monospace', fontWeight: 600, color: 'var(--accent)' }}>
              {formatAz(currentAz)}
            </span>
            {targetAz != null && (
              <>
                <span style={{ color: 'var(--fg-2)' }}>· Target:</span>
                <span style={{ fontFamily: 'monospace', fontWeight: 600, color: 'var(--power)' }}>
                  {formatAz(targetAz)}
                </span>
              </>
            )}
            {moving && (
              <span style={{ color: 'var(--power)', fontWeight: 600 }}>moving</span>
            )}
          </div>
        )}

        <div style={{ display: 'flex', gap: 6 }}>
          <button
            type="button"
            onClick={onTest}
            disabled={testInFlight}
            className="btn sm"
          >
            {testInFlight ? 'TESTING…' : 'TEST CONNECTION'}
          </button>
          {connected && (
            <button
              type="button"
              onClick={() => stop()}
              className="btn sm"
              style={{
                borderColor: 'var(--tx)',
                color: 'var(--tx)',
              }}
            >
              STOP
            </button>
          )}
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
          rotctld is hamlib's rotator daemon. Start it with e.g.{' '}
          <span style={{ fontFamily: 'monospace' }}>
            rotctld -m 2 -r /dev/ttyUSB0 -s 9600 -t 4533
          </span>{' '}
          (model 2 = dummy rotor for testing). Settings are saved server-side in zeus-prefs.db and
          shared across browsers and sessions; Zeus only auto-connects on startup if Enabled was
          checked at last clean exit.
        </div>
      </form>
    </div>
  );
}

function formatAz(az: number | null | undefined): string {
  if (az == null || !Number.isFinite(az)) return '—';
  // hamlib can report signed azimuths when the rotator crosses its zero
  // point (e.g. -79° on a rotor that can swing past 0°). For display we
  // want the equivalent 0..359 heading so the compass-style reading is
  // unambiguous (−79° → 281°).
  const normalized = ((az % 360) + 360) % 360;
  return `${normalized.toFixed(0).padStart(3, '0')}°`;
}
