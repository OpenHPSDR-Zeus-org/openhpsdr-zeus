// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect, useMemo, useState } from 'react';
import {
  useRfFilterStore,
  type RfFilterProfile,
  type RfFilterRange,
  type RfFilterSettings,
} from '../state/rf-filter-store';

type Bank = 'rxFilters' | 'txFilters';

const gridStyle = {
  display: 'grid',
  gridTemplateColumns: 'minmax(8rem, 1.1fr) repeat(2, minmax(5.5rem, 0.7fr)) minmax(5rem, 0.5fr)',
  gap: '0.45rem',
  alignItems: 'center',
} as const;

function mhz(hz: number): string {
  return (hz / 1_000_000).toFixed(3);
}

function parseMhz(value: string): number | null {
  const n = Number.parseFloat(value);
  if (!Number.isFinite(n)) return null;
  return Math.round(Math.max(0, Math.min(65, n)) * 1_000_000);
}

function formatActiveHz(hz: number): string {
  if (hz <= 0) return '-';
  return `${(hz / 1_000_000).toFixed(3)} MHz`;
}

function patchOption(
  settings: RfFilterSettings,
  patch: Partial<Pick<
    RfFilterSettings,
    'customMatrixEnabled' | 'rxBypassAll' | 'rxBypassOnTx' | 'rxBypassOnPureSignal'
  >>,
): RfFilterSettings {
  return { ...settings, ...patch };
}

function patchRange(
  settings: RfFilterSettings,
  profileKey: string,
  bank: Bank,
  rangeKey: string,
  patch: Partial<RfFilterRange>,
): RfFilterSettings {
  return {
    ...settings,
    profiles: settings.profiles.map((profile) =>
      profile.key !== profileKey
        ? profile
        : {
            ...profile,
            [bank]: profile[bank].map((range) =>
              range.key === rangeKey ? { ...range, ...patch } : range,
            ),
          },
    ),
  };
}

function ActiveChip({ label, value, hz }: { label: string; value: string; hz: number }) {
  return (
    <div
      style={{
        display: 'grid',
        gap: '0.15rem',
        minWidth: '7.5rem',
        padding: '0.45rem 0.55rem',
        border: '1px solid var(--border)',
        borderRadius: 6,
        background: 'var(--bg-1)',
      }}
    >
      <span style={{ color: 'var(--fg-3)', fontSize: '0.75rem' }}>{label}</span>
      <strong style={{ color: 'var(--fg)' }}>{value}</strong>
      <span style={{ color: 'var(--fg-2)', fontSize: '0.75rem' }}>{formatActiveHz(hz)}</span>
    </div>
  );
}

function FilterRows({
  title,
  rows,
  disabled,
  onRange,
}: {
  title: string;
  rows: RfFilterRange[];
  disabled: boolean;
  onRange: (key: string, patch: Partial<RfFilterRange>) => void;
}) {
  return (
    <div style={{ display: 'grid', gap: '0.4rem' }}>
      <div style={{ color: 'var(--fg-2)', fontSize: '0.78rem', textTransform: 'uppercase' }}>
        {title}
      </div>
      <div style={{ ...gridStyle, color: 'var(--fg-3)', fontSize: '0.72rem' }}>
        <span>Filter</span>
        <span>Start MHz</span>
        <span>End MHz</span>
        <span>Bypass</span>
      </div>
      {rows.map((row) => (
        <div key={row.key} style={gridStyle}>
          <span style={{ color: 'var(--fg)' }}>{row.label}</span>
          <input
            className="ps-select-mini"
            type="number"
            min={0}
            max={65}
            step={0.001}
            value={mhz(row.startHz)}
            disabled={disabled}
            onChange={(e) => {
              const hz = parseMhz(e.currentTarget.value);
              if (hz !== null) onRange(row.key, { startHz: hz });
            }}
          />
          <input
            className="ps-select-mini"
            type="number"
            min={0}
            max={65}
            step={0.001}
            value={mhz(row.endHz)}
            disabled={disabled}
            onChange={(e) => {
              const hz = parseMhz(e.currentTarget.value);
              if (hz !== null) onRange(row.key, { endHz: hz });
            }}
          />
          <label className="ps-check">
            <input
              type="checkbox"
              checked={row.forceBypass}
              disabled={disabled}
              onChange={(e) => onRange(row.key, { forceBypass: e.currentTarget.checked })}
            />
            <span className="ps-check-box" />
          </label>
        </div>
      ))}
    </div>
  );
}

export function RfFiltersSettingsCard() {
  const settings = useRfFilterStore((s) => s.settings);
  const loaded = useRfFilterStore((s) => s.loaded);
  const inflight = useRfFilterStore((s) => s.inflight);
  const error = useRfFilterStore((s) => s.error);
  const load = useRfFilterStore((s) => s.load);
  const save = useRfFilterStore((s) => s.save);
  const reset = useRfFilterStore((s) => s.reset);
  const [selectedProfileKey, setSelectedProfileKey] = useState(settings.activeProfileKey);

  useEffect(() => {
    void load();
  }, [load]);

  useEffect(() => {
    if (!settings.profiles.some((p) => p.key === selectedProfileKey)) {
      setSelectedProfileKey(settings.activeProfileKey);
    }
  }, [settings.activeProfileKey, settings.profiles, selectedProfileKey]);

  const selectedProfile = useMemo<RfFilterProfile | undefined>(
    () => settings.profiles.find((p) => p.key === selectedProfileKey) ?? settings.profiles[0],
    [settings.profiles, selectedProfileKey],
  );

  const disabled = inflight || !loaded || settings.profiles.length === 0;
  const active = settings.active;

  const saveOption = (patch: Partial<RfFilterSettings>) =>
    void save(patchOption(settings, patch as Parameters<typeof patchOption>[1]));

  const saveRange = (bank: Bank, key: string, patch: Partial<RfFilterRange>) => {
    if (!selectedProfile) return;
    void save(patchRange(settings, selectedProfile.key, bank, key, patch));
  };

  return (
    <div className="ps-card">
      <h4>
        <svg className="ps-ic-sm" viewBox="0 0 12 12">
          <path d="M1.5 3h9M3 6h6M4.5 9h3M3 3v6M9 3v6" fill="none" />
        </svg>
        RF Filters
        <span className="ps-card-hint">{settings.boardFamily || 'Alex BPF / LPF'}</span>
      </h4>

      <div className="ps-field">
        <div className="ps-name">
          Active
          <em>{active.reason || (loaded ? 'No active matrix state' : 'Loading')}</em>
        </div>
        <div style={{ display: 'flex', gap: '0.45rem', flexWrap: 'wrap' }}>
          <ActiveChip label="RX1" value={active.rx1Label} hz={active.rx1Hz} />
          <ActiveChip label="RX2" value={active.rx2Label} hz={active.rx2Hz} />
          <ActiveChip label="TX LPF" value={active.txLabel} hz={active.txHz} />
        </div>
      </div>

      <div className="ps-field">
        <div className="ps-name">
          Mode
          <em>{settings.customMatrixEnabled ? 'manual windows' : 'stock auto windows'}</em>
        </div>
        <div className="btn-row wrap">
          <button
            type="button"
            className={`btn sm ${settings.customMatrixEnabled ? 'active' : ''}`}
            disabled={disabled}
            onClick={() => saveOption({ customMatrixEnabled: true })}
          >
            Manual
          </button>
          <button
            type="button"
            className={`btn sm ${!settings.customMatrixEnabled ? 'active' : ''}`}
            disabled={disabled}
            onClick={() => saveOption({ customMatrixEnabled: false })}
          >
            Auto
          </button>
          <button type="button" className="btn sm" disabled={inflight} onClick={() => void reset()}>
            Reset
          </button>
        </div>
      </div>

      <div className="ps-field">
        <div className="ps-name">
          Bypass Policy
          <em>RX preselector bypass conditions.</em>
        </div>
        <div style={{ display: 'flex', gap: '0.8rem', flexWrap: 'wrap' }}>
          <label className="ps-check">
            <input
              type="checkbox"
              checked={settings.rxBypassAll}
              disabled={disabled}
              onChange={(e) => saveOption({ rxBypassAll: e.currentTarget.checked })}
            />
            <span className="ps-check-box" />
            <span>All RX</span>
          </label>
          <label className="ps-check">
            <input
              type="checkbox"
              checked={settings.rxBypassOnTx}
              disabled={disabled}
              onChange={(e) => saveOption({ rxBypassOnTx: e.currentTarget.checked })}
            />
            <span className="ps-check-box" />
            <span>TX</span>
          </label>
          <label className="ps-check">
            <input
              type="checkbox"
              checked={settings.rxBypassOnPureSignal}
              disabled={disabled}
              onChange={(e) => saveOption({ rxBypassOnPureSignal: e.currentTarget.checked })}
            />
            <span className="ps-check-box" />
            <span>PureSignal</span>
          </label>
        </div>
      </div>

      {settings.warnings.length > 0 || error ? (
        <div className="ps-field">
          <div className="ps-name">
            Validation
            <em>{error ?? settings.warnings.join(' ')}</em>
          </div>
        </div>
      ) : null}

      {selectedProfile ? (
        <>
          <div className="ps-field">
            <div className="ps-name">
              Profile
              <em>{selectedProfile.label}</em>
            </div>
            <div className="btn-row wrap">
              {settings.profiles.map((profile) => (
                <button
                  key={profile.key}
                  type="button"
                  className={`btn sm ${profile.key === selectedProfile.key ? 'active' : ''}`}
                  onClick={() => setSelectedProfileKey(profile.key)}
                >
                  {profile.label}
                </button>
              ))}
            </div>
          </div>

          <div className="ps-field" style={{ alignItems: 'start' }}>
            <div className="ps-name">
              RX
              <em>{selectedProfile.key === settings.activeProfileKey ? 'active board family' : 'staged'}</em>
            </div>
            <FilterRows
              title="RX BPF / HPF"
              rows={selectedProfile.rxFilters}
              disabled={disabled}
              onRange={(key, patch) => saveRange('rxFilters', key, patch)}
            />
          </div>

          <div className="ps-field" style={{ alignItems: 'start' }}>
            <div className="ps-name">
              TX
              <em>low-pass filter</em>
            </div>
            <FilterRows
              title="TX LPF"
              rows={selectedProfile.txFilters}
              disabled={disabled}
              onRange={(key, patch) => saveRange('txFilters', key, patch)}
            />
          </div>
        </>
      ) : null}
    </div>
  );
}
