// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// SpotsSettingsPanel — Settings → Spots. Full operator control over the
// POTA/SOTA Spots feature: feed sources + poll interval (honoured server-side),
// display filters (band / mode / QRT / age / dedup, applied in the panel), and
// click-to-tune behaviour (set-mode, connection gate, CW sideband, per-mode
// dial offsets). Every change applies immediately (POST /api/spots/settings),
// which also nudges the server-side poller to refresh.

import { useEffect, useState } from 'react';
import { useSpotsStore, requestSpotNotificationPermission } from '../state/spots-store';
import { SPOT_BANDS, type SpotModeGroup } from '../state/spots-store';
import { SPOTS_SETTINGS_DEFAULTS, type SpotsSettings } from '../api/client';

const MODE_GROUPS: ReadonlyArray<{ key: SpotModeGroup; label: string }> = [
  { key: 'CW', label: 'CW' },
  { key: 'PHONE', label: 'Phone' },
  { key: 'DIGITAL', label: 'Digital' },
  { key: 'FM', label: 'FM' },
  { key: 'AM', label: 'AM' },
];

const labelStyle: React.CSSProperties = {
  fontSize: 12,
  color: 'var(--fg-1)',
};
const hintStyle: React.CSSProperties = {
  fontSize: 10,
  lineHeight: 1.4,
  color: 'var(--fg-3)',
};
const sectionTitleStyle: React.CSSProperties = {
  fontSize: 11,
  fontWeight: 600,
  letterSpacing: '0.06em',
  color: 'var(--fg-2)',
};
const numInputStyle: React.CSSProperties = {
  width: 90,
  padding: '6px 8px',
  fontSize: 12,
  fontFamily: 'monospace',
  background: 'var(--bg-0)',
  border: '1px solid var(--panel-border)',
  borderRadius: 'var(--r-sm)',
  color: 'var(--fg-0)',
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

const urlInputStyle: React.CSSProperties = {
  width: '100%',
  padding: '6px 8px',
  fontSize: 11,
  fontFamily: 'monospace',
  background: 'var(--bg-0)',
  border: '1px solid var(--panel-border)',
  borderRadius: 'var(--r-sm)',
  color: 'var(--fg-0)',
};

/** A feed-URL text field. Commits on blur / Enter (not per keystroke) so a
 *  half-typed URL isn't POSTed and bounced back to the default mid-edit. */
function UrlField({
  label,
  value,
  defaultValue,
  disabled,
  onCommit,
}: {
  label: string;
  value: string;
  defaultValue: string;
  disabled?: boolean;
  onCommit: (v: string) => void;
}) {
  const [draft, setDraft] = useState(value);
  useEffect(() => setDraft(value), [value]);
  const commit = () => {
    if (draft !== value) onCommit(draft);
  };
  return (
    <label style={{ display: 'flex', flexDirection: 'column', gap: 4, opacity: disabled ? 0.5 : 1 }}>
      <span style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
        <span style={labelStyle}>{label}</span>
        {value !== defaultValue && (
          <button
            type="button"
            className="btn sm"
            disabled={disabled}
            onClick={() => {
              setDraft(defaultValue);
              onCommit(defaultValue);
            }}
          >
            RESET
          </button>
        )}
      </span>
      <input
        type="text"
        spellCheck={false}
        autoComplete="off"
        value={draft}
        disabled={disabled}
        onChange={(e) => setDraft(e.target.value)}
        onBlur={commit}
        onKeyDown={(e) => {
          if (e.key === 'Enter') e.currentTarget.blur();
        }}
        style={urlInputStyle}
      />
    </label>
  );
}

/** A toggleable filter chip — used by the band and mode multi-selects. */
function Chip({
  active,
  disabled,
  onClick,
  children,
}: {
  active: boolean;
  disabled?: boolean;
  onClick: () => void;
  children: React.ReactNode;
}) {
  return (
    <button
      type="button"
      disabled={disabled}
      onClick={onClick}
      className={`btn sm${active ? ' active' : ''}`}
      style={{ minWidth: 44 }}
    >
      {children}
    </button>
  );
}

/** Add/remove callsigns on the watchlist. Adds on Enter or the + button;
 *  callsigns are upper-cased and the server de-dupes. */
function WatchlistEditor({
  watchlist,
  disabled,
  onChange,
}: {
  watchlist: string[];
  disabled?: boolean;
  onChange: (next: string[]) => void;
}) {
  const [draft, setDraft] = useState('');
  const add = () => {
    const c = draft.trim().toUpperCase();
    if (!c) return;
    if (!watchlist.includes(c)) onChange([...watchlist, c]);
    setDraft('');
  };
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 6, opacity: disabled ? 0.5 : 1 }}>
      <span style={{ display: 'flex', gap: 6 }}>
        <input
          type="text"
          spellCheck={false}
          autoComplete="off"
          value={draft}
          disabled={disabled}
          placeholder="Add callsign…"
          onChange={(e) => setDraft(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter') {
              e.preventDefault();
              add();
            }
          }}
          style={{ ...urlInputStyle, flex: 1, textTransform: 'uppercase' }}
        />
        <button type="button" className="btn sm" disabled={disabled || draft.trim().length === 0} onClick={add}>
          +
        </button>
      </span>
      {watchlist.length > 0 && (
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4 }}>
          {watchlist.map((c) => (
            <button
              key={c}
              type="button"
              className="btn sm active"
              disabled={disabled}
              title="Remove from watchlist"
              onClick={() => onChange(watchlist.filter((x) => x !== c))}
              style={{ fontFamily: 'monospace' }}
            >
              ★ {c} ✕
            </button>
          ))}
        </div>
      )}
    </div>
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

  const toggleInList = (list: string[], key: string): string[] =>
    list.includes(key) ? list.filter((k) => k !== key) : [...list, key];

  const disabled = !settings.enabled;

  return (
    <div style={{ maxWidth: 640 }}>
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
        POTA / SOTA / DX SPOTS {saving && <span style={{ color: 'var(--accent)' }}>· saving…</span>}
      </h3>

      <div style={{ display: 'flex', flexDirection: 'column', gap: 18 }}>
        <Toggle
          checked={settings.enabled}
          onChange={(v) => patch({ enabled: v })}
          label="Enable spot feed"
          hint="Master switch. When off, the server stops polling and the panel shows nothing."
        />

        {/* ----- Sources + poll interval (server-side) ----- */}
        <section style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
          <div style={sectionTitleStyle}>SOURCES</div>
          <Toggle
            checked={settings.potaEnabled}
            disabled={disabled}
            onChange={(v) => patch({ potaEnabled: v })}
            label="POTA (Parks on the Air)"
          />
          <Toggle
            checked={settings.sotaEnabled}
            disabled={disabled}
            onChange={(v) => patch({ sotaEnabled: v })}
            label="SOTA (Summits on the Air)"
          />
          <Toggle
            checked={settings.dxEnabled}
            disabled={disabled}
            onChange={(v) => patch({ dxEnabled: v })}
            label="DX cluster (DXSummit feed)"
            hint="General HF/VHF DX spots from the cluster feed. Off by default — it can be high-volume."
          />
          <label style={{ display: 'flex', flexDirection: 'column', gap: 4, marginTop: 4 }}>
            <span style={labelStyle}>Poll interval</span>
            <span style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
              <input
                type="number"
                min={30}
                max={600}
                step={5}
                value={settings.pollIntervalSeconds}
                disabled={disabled}
                onChange={(e) => {
                  const n = Number(e.target.value);
                  if (Number.isFinite(n)) {
                    patch({ pollIntervalSeconds: Math.min(600, Math.max(30, Math.round(n))) });
                  }
                }}
                style={numInputStyle}
              />
              <span style={hintStyle}>seconds (30–600) — how often the server re-fetches.</span>
            </span>
          </label>
        </section>

        {/* ----- Per-source feed URLs (server-side) ----- */}
        <section style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
          <div style={sectionTitleStyle}>FEED URLS</div>
          <UrlField
            label="POTA URL"
            value={settings.potaUrl}
            defaultValue={SPOTS_SETTINGS_DEFAULTS.potaUrl}
            disabled={disabled || !settings.potaEnabled}
            onCommit={(v) => patch({ potaUrl: v })}
          />
          <UrlField
            label="SOTA URL"
            value={settings.sotaUrl}
            defaultValue={SPOTS_SETTINGS_DEFAULTS.sotaUrl}
            disabled={disabled || !settings.sotaEnabled}
            onCommit={(v) => patch({ sotaUrl: v })}
          />
          <UrlField
            label="DX URL"
            value={settings.dxUrl}
            defaultValue={SPOTS_SETTINGS_DEFAULTS.dxUrl}
            disabled={disabled || !settings.dxEnabled}
            onCommit={(v) => patch({ dxUrl: v })}
          />
          <span style={hintStyle}>
            Point a source at a mirror or alternative endpoint that serves the same JSON shape.
            Blank (or an invalid URL) resets to the default. POTA &amp; DX report kHz, SOTA reports MHz.
          </span>
        </section>

        {/* ----- Display filters (panel-side) ----- */}
        <section style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
          <div style={sectionTitleStyle}>FILTERS</div>

          <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
            <span style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
              <span style={labelStyle}>Bands</span>
              {settings.bands.length > 0 && (
                <button type="button" className="btn sm" onClick={() => patch({ bands: [] })}>
                  ALL
                </button>
              )}
            </span>
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4 }}>
              {SPOT_BANDS.map((b) => (
                <Chip
                  key={b.key}
                  active={settings.bands.includes(b.key)}
                  disabled={disabled}
                  onClick={() => patch({ bands: toggleInList(settings.bands, b.key) })}
                >
                  {b.key}
                </Chip>
              ))}
            </div>
            <span style={hintStyle}>
              {settings.bands.length === 0
                ? 'All bands shown. Select bands to show only those.'
                : `Showing only: ${settings.bands.join(', ')}`}
            </span>
          </div>

          <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
            <span style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
              <span style={labelStyle}>Modes</span>
              {settings.modes.length > 0 && (
                <button type="button" className="btn sm" onClick={() => patch({ modes: [] })}>
                  ALL
                </button>
              )}
            </span>
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4 }}>
              {MODE_GROUPS.map((m) => (
                <Chip
                  key={m.key}
                  active={settings.modes.includes(m.key)}
                  disabled={disabled}
                  onClick={() => patch({ modes: toggleInList(settings.modes, m.key) })}
                >
                  {m.label}
                </Chip>
              ))}
            </div>
            <span style={hintStyle}>
              {settings.modes.length === 0
                ? 'All modes shown. Unrecognised modes (FT8, JS8, RTTY…) count as Digital.'
                : `Showing only: ${settings.modes.join(', ')}`}
            </span>
          </div>

          <Toggle
            checked={settings.hideQrt}
            disabled={disabled}
            onChange={(v) => patch({ hideQrt: v })}
            label="Hide QRT spots"
            hint="Drop spots whose comment says QRT / QSY / closing / packing up."
          />
          <Toggle
            checked={settings.hideWorked}
            disabled={disabled}
            onChange={(v) => patch({ hideWorked: v })}
            label="Hide worked stations"
            hint="Drop spots whose activator is already in your Zeus logbook. Worked stations still show a ✓ when this is off."
          />
          <Toggle
            checked={settings.latestPerActivator}
            disabled={disabled}
            onChange={(v) => patch({ latestPerActivator: v })}
            label="Latest spot per activator only"
            hint="Collapse repeats so each operator appears once (their newest spot)."
          />
          <label style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
            <span style={labelStyle}>Maximum age</span>
            <span style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
              <input
                type="number"
                min={0}
                max={1440}
                step={5}
                value={settings.maxAgeMinutes}
                disabled={disabled}
                onChange={(e) => {
                  const n = Number(e.target.value);
                  if (Number.isFinite(n)) {
                    patch({ maxAgeMinutes: Math.min(1440, Math.max(0, Math.round(n))) });
                  }
                }}
                style={numInputStyle}
              />
              <span style={hintStyle}>minutes (0 = no limit). Hide spots older than this.</span>
            </span>
          </label>
        </section>

        {/* ----- Watchlist + alerts (panel-side) ----- */}
        <section style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
          <div style={sectionTitleStyle}>WATCHLIST &amp; ALERTS</div>
          <WatchlistEditor
            watchlist={settings.watchlist}
            disabled={disabled}
            onChange={(next) => patch({ watchlist: next })}
          />
          <span style={hintStyle}>
            Watched calls show a ★ in the table. With alerts on, Zeus notifies you when one is spotted.
          </span>
          <Toggle
            checked={settings.alertsEnabled}
            disabled={disabled || settings.watchlist.length === 0}
            onChange={async (v) => {
              if (v) await requestSpotNotificationPermission();
              patch({ alertsEnabled: v });
            }}
            label="Alert on watched calls"
            hint="Raise a desktop notification when a watchlist callsign appears. Needs browser notification permission."
          />
          <Toggle
            checked={settings.alertSound}
            disabled={disabled || !settings.alertsEnabled}
            onChange={(v) => patch({ alertSound: v })}
            label="Play a sound with alerts"
            hint="A short audio cue alongside the desktop notification."
          />
        </section>

        {/* ----- QRZ enrichment (panel-side; uses the QRZ session) ----- */}
        <section style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
          <div style={sectionTitleStyle}>QRZ ENRICHMENT</div>
          <Toggle
            checked={settings.enrichQrz}
            disabled={disabled}
            onChange={(v) => patch({ enrichQrz: v })}
            label="Show operator names from QRZ"
            hint="Resolve the operator's name for each spot via your QRZ session (cached per callsign). Requires QRZ to be logged in; off by default to respect the XML-API quota."
          />
        </section>

        {/* ----- Click-to-tune (panel-side, drives the Zeus radio) ----- */}
        <section style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
          <div style={sectionTitleStyle}>CLICK-TO-TUNE</div>
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
            <span style={labelStyle}>CW sideband</span>
            <select
              value={settings.cwSideband}
              onChange={(e) => patch({ cwSideband: e.target.value === 'CWL' ? 'CWL' : 'CWU' })}
              style={{ ...numInputStyle, width: 150, fontFamily: 'inherit' }}
            >
              <option value="CWU">CWU (upper)</option>
              <option value="CWL">CWL (lower)</option>
            </select>
            <span style={hintStyle}>
              Which CW sideband a CW spot tunes to. SSB spots pick LSB below 10 MHz / USB above automatically.
            </span>
          </label>
          <div style={{ display: 'flex', gap: 18, flexWrap: 'wrap' }}>
            <label style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
              <span style={labelStyle}>CW dial offset</span>
              <span style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                <input
                  type="number"
                  min={-5000}
                  max={5000}
                  step={10}
                  value={settings.cwTuneOffsetHz}
                  onChange={(e) => {
                    const n = Number(e.target.value);
                    if (Number.isFinite(n)) {
                      patch({ cwTuneOffsetHz: Math.min(5000, Math.max(-5000, Math.round(n))) });
                    }
                  }}
                  style={numInputStyle}
                />
                <span style={hintStyle}>Hz</span>
              </span>
            </label>
            <label style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
              <span style={labelStyle}>Digital dial offset</span>
              <span style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                <input
                  type="number"
                  min={-5000}
                  max={5000}
                  step={10}
                  value={settings.digiTuneOffsetHz}
                  onChange={(e) => {
                    const n = Number(e.target.value);
                    if (Number.isFinite(n)) {
                      patch({ digiTuneOffsetHz: Math.min(5000, Math.max(-5000, Math.round(n))) });
                    }
                  }}
                  style={numInputStyle}
                />
                <span style={hintStyle}>Hz</span>
              </span>
            </label>
          </div>
          <span style={hintStyle}>
            Dial offsets are added to the spot frequency when you click a CW or digital spot — handy if
            you prefer to land slightly off the published frequency. Leave at 0 to tune exactly.
          </span>
          <label style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
            <span style={labelStyle}>Scan dwell</span>
            <span style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
              <input
                type="number"
                min={2}
                max={120}
                step={1}
                value={settings.scanDwellSeconds}
                disabled={disabled}
                onChange={(e) => {
                  const n = Number(e.target.value);
                  if (Number.isFinite(n)) {
                    patch({ scanDwellSeconds: Math.min(120, Math.max(2, Math.round(n))) });
                  }
                }}
                style={numInputStyle}
              />
              <span style={hintStyle}>
                seconds (2–120) — how long the ▶ Scan button dwells on each spot before stepping.
              </span>
            </span>
          </label>
        </section>
      </div>
    </div>
  );
}
