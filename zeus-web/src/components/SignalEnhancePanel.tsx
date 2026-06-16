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
// Display-tab surface for the signal-enhance engine (Christian Suarez N9WAR's
// pop / snap / peak-marker work, commit 8d949e2e). Only **Signal Pop** is
// exposed: it toggles `popEnabled` on the same store the GL pop-remap reads.
//
// Snap-to-signal (and its coupled peak markers — PeakMarkerOverlay gates on
// `snapEnabled`) was pulled from the UI per maintainer (KB2UKA) direction. The
// engine / snap-lock gesture / overlay code stays in place but dormant, and
// this panel forces `snapEnabled` off so it can't linger enabled with no toggle.

import { useEffect } from 'react';
import { useSignalEnhanceStore } from '../dsp/signal-estimator';

type ToggleRow = {
  label: string;
  help: string;
  enabled: boolean;
  onToggle: (v: boolean) => void;
};

export function SignalEnhancePanel() {
  const popEnabled = useSignalEnhanceStore((s) => s.popEnabled);
  const setPopEnabled = useSignalEnhanceStore((s) => s.setPopEnabled);
  const setSnapEnabled = useSignalEnhanceStore((s) => s.setSnapEnabled);

  // Snap-to-signal is no longer exposed in the UI — force it off so a
  // previously-enabled state (and its peak markers) can't linger with no toggle.
  useEffect(() => {
    setSnapEnabled(false);
  }, [setSnapEnabled]);

  const rows: ToggleRow[] = [
    {
      label: 'Signal Pop',
      help: 'Per-bin noise-floor subtraction that lifts real carriers out of the noise on the panadapter and waterfall. Pauses automatically while you transmit.',
      enabled: popEnabled,
      onToggle: setPopEnabled,
    },
  ];

  return (
    <section>
      <div style={sectionHead}>
        <h3 style={sectionH3}>Signal Enhance</h3>
        <p style={sectionP}>
          A display-only aid that lifts weak signals out of the noise on the panadapter and waterfall. It never touches transmit and pauses during TX.
        </p>
      </div>

      <div style={rowList}>
        {rows.map((row) => (
          <button
            key={row.label}
            type="button"
            role="switch"
            aria-checked={row.enabled}
            aria-label={row.label}
            onClick={() => row.onToggle(!row.enabled)}
            style={rowCard(row.enabled)}
          >
            <div style={rowText}>
              <span style={rowLabel}>{row.label}</span>
              <span style={rowHelp}>{row.help}</span>
            </div>
            <span style={switchTrack(row.enabled)} aria-hidden>
              <span style={switchThumb(row.enabled)} />
            </span>
          </button>
        ))}
      </div>
    </section>
  );
}

const sectionHead: React.CSSProperties = {
  display: 'flex',
  alignItems: 'baseline',
  flexWrap: 'wrap',
  gap: 10,
  marginBottom: 10,
};
const sectionH3: React.CSSProperties = {
  margin: 0,
  fontSize: 11,
  fontWeight: 700,
  letterSpacing: '0.18em',
  textTransform: 'uppercase',
  color: 'var(--fg-0)',
};
const sectionP: React.CSSProperties = {
  margin: 0,
  fontSize: 12,
  lineHeight: 1.5,
  color: 'var(--fg-2)',
};

const rowList: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 10,
};

function rowCard(active: boolean): React.CSSProperties {
  return {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: 14,
    width: '100%',
    padding: '12px 14px',
    textAlign: 'left',
    border: '1px solid',
    borderColor: active ? 'var(--accent)' : 'var(--line)',
    background: active ? 'var(--accent-soft)' : 'var(--bg-1)',
    boxShadow: active ? 'inset 0 0 0 1px var(--accent)' : 'none',
    borderRadius: 'var(--r-md)',
    cursor: 'pointer',
    color: 'var(--fg-1)',
    transition: 'background var(--dur-fast), border-color var(--dur-fast)',
  };
}

const rowText: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 4,
};
const rowLabel: React.CSSProperties = {
  fontSize: 13,
  fontWeight: 700,
  color: 'var(--fg-0)',
  letterSpacing: '0.02em',
};
const rowHelp: React.CSSProperties = {
  fontSize: 11.5,
  color: 'var(--fg-2)',
  lineHeight: 1.45,
};

function switchTrack(active: boolean): React.CSSProperties {
  return {
    position: 'relative',
    flexShrink: 0,
    width: 38,
    height: 22,
    borderRadius: 999,
    background: active ? 'var(--accent)' : 'var(--bg-3)',
    border: active ? '1px solid var(--accent)' : '1px solid var(--line)',
    transition: 'background var(--dur-fast), border-color var(--dur-fast)',
  };
}

function switchThumb(active: boolean): React.CSSProperties {
  return {
    position: 'absolute',
    top: 2,
    left: active ? 18 : 2,
    width: 16,
    height: 16,
    borderRadius: '50%',
    background: active ? 'var(--bg-0)' : 'var(--fg-1)',
    transition: 'left var(--dur-fast)',
  };
}
