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
import { useSpottingStore } from '../state/spotting-store';
import { useOperatorStore } from '../state/operator-store';

// Digital-mode spotting: upload RX decodes to the community spotting networks —
// FT8/FT4 to PSK Reporter and WSPR to WSPRnet. New network egress — both
// DISABLED by default. Uploads are spot reports OUT only; nothing controls the
// radio or transmits. A callsign + Maidenhead grid are required (PSK Reporter /
// WSPRnet attribute every spot to your station).
export function SpottingSettingsPanel() {
  const config = useSpottingStore((s) => s.config);
  const status = useSpottingStore((s) => s.status);
  const saveConfig = useSpottingStore((s) => s.saveConfig);

  // Seed call/grid from the client operator identity so the operator usually
  // doesn't retype — but the authoritative value is what's persisted server-side.
  const opCall = useOperatorStore((s) => s.call);
  const opGrid = useOperatorStore((s) => s.grid);

  const [psk, setPsk] = useState(config.pskReporterEnabled);
  const [wsprnet, setWsprnet] = useState(config.wsprnetEnabled);
  const [call, setCall] = useState(config.callsign || opCall);
  const [grid, setGrid] = useState(config.grid || opGrid);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    setPsk(config.pskReporterEnabled);
    setWsprnet(config.wsprnetEnabled);
    setCall(config.callsign || opCall);
    setGrid(config.grid || opGrid);
  }, [config.pskReporterEnabled, config.wsprnetEnabled, config.callsign, config.grid, opCall, opGrid]);

  async function onSave(e: React.FormEvent) {
    e.preventDefault();
    setSaving(true);
    try {
      await saveConfig({
        pskReporterEnabled: psk,
        wsprnetEnabled: wsprnet,
        callsign: call.trim().toUpperCase(),
        grid: grid.trim().toUpperCase(),
      });
    } finally {
      setSaving(false);
    }
  }

  const anyEnabled = status ? status.pskReporterEnabled || status.wsprnetEnabled : false;
  const identityResolved = status?.identityResolved ?? false;
  const identityMissing = anyEnabled && !identityResolved;

  const inputStyle: React.CSSProperties = {
    padding: '6px 8px',
    fontSize: 12,
    fontFamily: 'monospace',
    background: 'var(--bg-0)',
    border: '1px solid var(--panel-border)',
    borderRadius: 'var(--r-sm)',
    color: 'var(--fg-0)',
  };

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
        DIGITAL-MODE SPOTTING
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
        When enabled, Zeus uploads what it RECEIVES to the community spotting
        networks: FT8/FT4 decodes go to{' '}
        <strong>PSK Reporter</strong> (report.pskreporter.info) and WSPR spots to{' '}
        <strong>WSPRnet</strong> (wsprnet.org). This is spot-report egress OUT only — it
        never controls the radio or transmits. Your <strong>callsign and grid</strong> are
        required (every spot is attributed to your station). Both networks are public.
      </div>

      <form onSubmit={onSave} style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
        <label style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
          <input
            type="checkbox"
            checked={psk}
            onChange={(e) => setPsk(e.target.checked)}
            style={{ accentColor: 'var(--accent)' }}
          />
          <span style={{ fontSize: 12, fontWeight: 600, color: 'var(--fg-1)' }}>
            Upload FT8/FT4 decodes to PSK Reporter
          </span>
        </label>

        <label style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
          <input
            type="checkbox"
            checked={wsprnet}
            onChange={(e) => setWsprnet(e.target.checked)}
            style={{ accentColor: 'var(--accent)' }}
          />
          <span style={{ fontSize: 12, fontWeight: 600, color: 'var(--fg-1)' }}>
            Upload WSPR spots to WSPRnet
          </span>
        </label>

        <label style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
          <span style={{ fontSize: 11, fontWeight: 600, color: 'var(--fg-2)' }}>Callsign</span>
          <input
            type="text"
            value={call}
            onChange={(e) => setCall(e.target.value)}
            spellCheck={false}
            placeholder="K1ABC"
            style={inputStyle}
          />
        </label>

        <label style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
          <span style={{ fontSize: 11, fontWeight: 600, color: 'var(--fg-2)' }}>
            Maidenhead Grid
          </span>
          <input
            type="text"
            value={grid}
            onChange={(e) => setGrid(e.target.value)}
            spellCheck={false}
            placeholder="FN42"
            maxLength={6}
            style={inputStyle}
          />
          <span style={{ fontSize: 10, color: 'var(--fg-3)' }}>
            4 or 6 characters. Leave callsign/grid blank to fall back to your QRZ home
            station. Applies immediately — no restart required.
          </span>
        </label>

        {identityMissing && (
          <div
            style={{
              padding: 10,
              fontSize: 11,
              color: 'var(--tx)',
              background: 'var(--bg-0)',
              border: '1px solid var(--tx)',
              borderRadius: 'var(--r-sm)',
            }}
          >
            Spotting is enabled but no callsign/grid is available — uploads are paused
            until you set them (here or via QRZ login).
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
            color: 'var(--fg-2)',
          }}
        >
          <span
            style={{
              width: 8,
              height: 8,
              borderRadius: '50%',
              background: anyEnabled && identityResolved ? 'var(--accent)' : 'var(--fg-3)',
              flexShrink: 0,
            }}
          />
          {!anyEnabled
            ? 'Spotting is currently disabled'
            : status?.pskReporterEnabled && status?.wsprnetEnabled
              ? 'Uploading to PSK Reporter + WSPRnet'
              : status?.pskReporterEnabled
                ? 'Uploading FT8/FT4 to PSK Reporter'
                : 'Uploading WSPR to WSPRnet'}
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
