// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// SpotsSettingsPanel — Settings → Spots. Edits the POTA/SOTA feed toggles,
// poll interval, and click-to-tune behaviour persisted by SpotsSettingsStore.
// Changes apply immediately (POST /api/spots/settings), which also nudges the
// server-side poller to refresh.

import { useEffect } from 'react';
import { useSpotsStore } from '../state/spots-store';
import type { SpotsSettings } from '../api/client';

const labelStyle: React.CSSProperties = {
  fontSize: 12,
  color: 'var(--fg-1)',
};
const hintStyle: React.CSSProperties = {
  fontSize: 10,
  lineHeight: 1.4,
  color: 'var(--fg-3)',
};

function Toggle({
  checked,
  disabled,
  onChange,
  label,
  hint,
}: {
  checked: boolean;
  disabled?: boolean;
  onChange: (v: boolean) => void;
  label: string;
  hint?: string;
}) {
  return (
    <label style={{ display: 'flex', flexDirection: 'column', gap: 2, opacity: disabled ? 0.5 : 1 }}>
      <span style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
        <input
          type="checkbox"
          checked={checked}
          disabled={disabled}
          onChange={(e) => onChange(e.target.checked)}
        />
        <span style={labelStyle}>{label}</span>
      </span>
      {hint && <span style={{ ...hintStyle, marginLeft: 24 }}>{hint}</span>}
    </label>
  );
}

export function SpotsSettingsPanel() {
  const settings = useSpotsStore((s) => s.settings);
  const saving = useSpotsStore((s) => s.savingSettings);
  const loadSettings = useSpotsStore((s) => s.loadSettings);
  const saveSettings = useSpotsStore((s) => s.saveSettings);

  useEffect(() => {
    void loadSettings();
  }, [loadSettings]);

  const patch = (p: Partial<SpotsSettings>) => void saveSettings({ ...settings, ...p });

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
        POTA / SOTA SPOTS {saving && <span style={{ color: 'var(--accent)' }}>· saving…</span>}
      </h3>

      <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
        <Toggle
          checked={settings.enabled}
          onChange={(v) => patch({ enabled: v })}
          label="Enable spot feed"
          hint="Master switch. When off, the server stops polling and the panel shows nothing."
        />

        <section style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
          <div style={{ fontSize: 11, fontWeight: 600, color: 'var(--fg-2)' }}>SOURCES</div>
          <Toggle
            checked={settings.potaEnabled}
            disabled={!settings.enabled}
            onChange={(v) => patch({ potaEnabled: v })}
            label="POTA (Parks on the Air)"
          />
          <Toggle
            checked={settings.sotaEnabled}
            disabled={!settings.enabled}
            onChange={(v) => patch({ sotaEnabled: v })}
            label="SOTA (Summits on the Air)"
          />
        </section>

        <label style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
          <span style={{ fontSize: 11, fontWeight: 600, color: 'var(--fg-2)' }}>POLL INTERVAL</span>
          <span style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
            <input
              type="number"
              min={30}
              max={600}
              step={5}
              value={settings.pollIntervalSeconds}
              disabled={!settings.enabled}
              onChange={(e) => {
                const n = Number(e.target.value);
                if (Number.isFinite(n)) patch({ pollIntervalSeconds: Math.min(600, Math.max(30, Math.round(n))) });
              }}
              style={{
                width: 90,
                padding: '6px 8px',
                fontSize: 12,
                fontFamily: 'monospace',
                background: 'var(--bg-0)',
                border: '1px solid var(--panel-border)',
                borderRadius: 'var(--r-sm)',
                color: 'var(--fg-0)',
              }}
            />
            <span style={hintStyle}>seconds (30–600)</span>
          </span>
        </label>

        <section style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
          <div style={{ fontSize: 11, fontWeight: 600, color: 'var(--fg-2)' }}>CLICK-TO-TUNE</div>
          <Toggle
            checked={settings.setModeOnTune}
            onChange={(v) => patch({ setModeOnTune: v })}
            label="Set mode when tuning"
            hint="Also switch the radio's mode (SSB/CW/DIGU…) to match the spot, not just the frequency."
          />
          <Toggle
            checked={settings.tuneOnlyWhenConnected}
            onChange={(v) => patch({ tuneOnlyWhenConnected: v })}
            label="Tune only when a radio is connected"
            hint="Block click-to-tune unless Zeus has an active radio connection."
          />
          <label style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
            <span style={{ fontSize: 12, color: 'var(--fg-1)' }}>CW sideband</span>
            <select
              value={settings.cwSideband}
              onChange={(e) => patch({ cwSideband: e.target.value === 'CWL' ? 'CWL' : 'CWU' })}
              style={{
                width: 140,
                padding: '6px 8px',
                fontSize: 12,
                background: 'var(--bg-0)',
                border: '1px solid var(--panel-border)',
                borderRadius: 'var(--r-sm)',
                color: 'var(--fg-0)',
              }}
            >
              <option value="CWU">CWU (upper)</option>
              <option value="CWL">CWL (lower)</option>
            </select>
            <span style={hintStyle}>
              Which CW sideband a CW spot tunes to. SSB spots pick LSB below 10 MHz / USB above automatically.
            </span>
          </label>
        </section>
      </div>
    </div>
  );
}
