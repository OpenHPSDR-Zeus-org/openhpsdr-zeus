// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect, useMemo, useRef, useState } from 'react';
import { RotateCcw, Save } from 'lucide-react';
import {
  useRfFilterStore,
  type RfFilterProfile,
  type RfFilterRange,
  type RfFilterSettings,
} from '../state/rf-filter-store';

type Bank = 'rxFilters' | 'txFilters';
type OptionPatch = Partial<
  Pick<RfFilterSettings, 'customMatrixEnabled' | 'rxBypassAll' | 'rxBypassOnTx' | 'rxBypassOnPureSignal'>
>;

const RF_FILTER_MAX_MHZ = 61.44;

function mhz(hz: number): string {
  return (hz / 1_000_000).toFixed(6);
}

function parseMhz(value: string): number | null {
  const trimmed = value.trim();
  if (trimmed.length === 0) return null;
  const n = Number(trimmed);
  if (!Number.isFinite(n)) return null;
  return Math.round(Math.max(0, Math.min(RF_FILTER_MAX_MHZ, n)) * 1_000_000);
}

function formatActiveHz(hz: number): string {
  if (hz <= 0) return '-';
  return `${(hz / 1_000_000).toFixed(3)} MHz`;
}

function patchOption(settings: RfFilterSettings, patch: OptionPatch): RfFilterSettings {
  return { ...settings, ...patch };
}

function editableSignature(settings: RfFilterSettings): string {
  return JSON.stringify({
    customMatrixEnabled: settings.customMatrixEnabled,
    rxBypassAll: settings.rxBypassAll,
    rxBypassOnTx: settings.rxBypassOnTx,
    rxBypassOnPureSignal: settings.rxBypassOnPureSignal,
    profiles: settings.profiles,
  });
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
    <div className="rf-filter-active-pill">
      <span>{label}</span>
      <strong>{value}</strong>
      <em>{formatActiveHz(hz)}</em>
    </div>
  );
}

function RfMhzInput({
  hz,
  disabled,
  onCommit,
}: {
  hz: number;
  disabled: boolean;
  onCommit: (hz: number) => void;
}) {
  const [text, setText] = useState(mhz(hz));
  const [invalid, setInvalid] = useState(false);

  useEffect(() => {
    setText(mhz(hz));
    setInvalid(false);
  }, [hz]);

  const commit = () => {
    const nextHz = parseMhz(text);
    if (nextHz === null) {
      setText(mhz(hz));
      setInvalid(false);
      return;
    }
    setText(mhz(nextHz));
    setInvalid(false);
    if (nextHz !== hz) onCommit(nextHz);
  };

  return (
    <input
      className={`rf-filter-input${invalid ? ' is-invalid' : ''}`}
      type="number"
      min={0}
      max={RF_FILTER_MAX_MHZ}
      step={0.000001}
      value={text}
      disabled={disabled}
      aria-invalid={invalid}
      onChange={(e) => {
        const next = e.currentTarget.value;
        setText(next);
        setInvalid(next.trim().length > 0 && parseMhz(next) === null);
      }}
      onBlur={commit}
      onKeyDown={(e) => {
        if (e.key === 'Enter') {
          e.currentTarget.blur();
        } else if (e.key === 'Escape') {
          setText(mhz(hz));
          setInvalid(false);
          e.currentTarget.blur();
        }
      }}
    />
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
    <section className="rf-filter-bank" aria-label={title}>
      <div className="rf-filter-bank-title">{title}</div>
      <table className="rf-filter-table">
        <thead>
          <tr>
            <th>Filter</th>
            <th>Start MHz</th>
            <th>End MHz</th>
            <th>Bypass</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((row) => (
            <tr key={row.key}>
              <th scope="row">{row.label}</th>
              <td>
                <RfMhzInput
                  hz={row.startHz}
                  disabled={disabled}
                  onCommit={(hz) => onRange(row.key, { startHz: hz })}
                />
              </td>
              <td>
                <RfMhzInput
                  hz={row.endHz}
                  disabled={disabled}
                  onCommit={(hz) => onRange(row.key, { endHz: hz })}
                />
              </td>
              <td className="rf-filter-check-cell">
                <label className="ps-check">
                  <input
                    type="checkbox"
                    checked={row.forceBypass}
                    disabled={disabled}
                    onChange={(e) => onRange(row.key, { forceBypass: e.currentTarget.checked })}
                  />
                  <span className="ps-check-box" />
                </label>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
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
  const savedSignature = useMemo(() => editableSignature(settings), [settings]);
  const savedSignatureRef = useRef(savedSignature);
  const [draft, setDraft] = useState(settings);
  const [selectedProfileKey, setSelectedProfileKey] = useState(settings.activeProfileKey);

  useEffect(() => {
    void load();
  }, [load]);

  useEffect(() => {
    setDraft((current) =>
      editableSignature(current) === savedSignatureRef.current ? settings : current,
    );
    savedSignatureRef.current = savedSignature;
  }, [settings, savedSignature]);

  useEffect(() => {
    if (!draft.profiles.some((p) => p.key === selectedProfileKey)) {
      setSelectedProfileKey(draft.activeProfileKey);
    }
  }, [draft.activeProfileKey, draft.profiles, selectedProfileKey]);

  const selectedProfile = useMemo<RfFilterProfile | undefined>(
    () => draft.profiles.find((p) => p.key === selectedProfileKey) ?? draft.profiles[0],
    [draft.profiles, selectedProfileKey],
  );

  const draftSignature = useMemo(() => editableSignature(draft), [draft]);
  const dirty = draftSignature !== savedSignature;
  const disabled = inflight || !loaded || draft.profiles.length === 0;
  const active = settings.active;
  const statusMessages = [
    dirty ? 'Unsaved edits pending. Save applies the matrix to the radio and persists it.' : null,
    error,
    settings.warnings.length > 0 ? settings.warnings.join(' ') : null,
  ].filter((msg): msg is string => Boolean(msg));

  const updateOption = (patch: OptionPatch) =>
    setDraft((current) => patchOption(current, patch));

  const saveRange = (bank: Bank, key: string, patch: Partial<RfFilterRange>) => {
    setDraft((current) => {
      const profile = current.profiles.find((p) => p.key === selectedProfileKey) ?? current.profiles[0];
      return profile ? patchRange(current, profile.key, bank, key, patch) : current;
    });
  };

  const saveDraft = () => {
    if (!dirty || disabled) return;
    void save(draft);
  };

  const discardDraft = () => {
    setDraft(settings);
  };

  return (
    <div className="ps-card rf-filter-card">
      <h4>
        <svg className="ps-ic-sm" viewBox="0 0 12 12">
          <path d="M1.5 3h9M3 6h6M4.5 9h3M3 3v6M9 3v6" fill="none" />
        </svg>
        RF Filters
        <span className="ps-card-hint">{settings.boardFamily || 'Alex BPF / LPF'}</span>
      </h4>

      <div className="rf-filter-summary">
        <div className="rf-filter-status">
          <div className="rf-filter-status-title">
            <span>Active</span>
            <em>{active.reason || (loaded ? 'No active matrix state' : 'Loading')}</em>
          </div>
          <div className="rf-filter-active-strip">
            <ActiveChip label="RX1" value={active.rx1Label} hz={active.rx1Hz} />
            <ActiveChip label="RX2" value={active.rx2Label} hz={active.rx2Hz} />
            <ActiveChip label="TX LPF" value={active.txLabel} hz={active.txHz} />
          </div>
        </div>

        <div className="rf-filter-actions">
          <div className="rf-filter-save-group" aria-label="RF filter save actions">
            <button
              type="button"
              className={`btn sm rf-filter-save ${dirty ? 'active' : ''}`}
              disabled={disabled || !dirty}
              onClick={saveDraft}
            >
              <Save size={13} strokeWidth={2} />
              Save
            </button>
            <button type="button" className="btn sm" disabled={disabled || !dirty} onClick={discardDraft}>
              Discard
            </button>
            <button
              type="button"
              className="btn sm rf-filter-reset"
              disabled={inflight || !loaded}
              title="Reset RF filter matrix"
              onClick={() => void reset()}
            >
              <RotateCcw size={13} strokeWidth={2} />
              Reset
            </button>
          </div>
          <div className="rf-filter-control-group" aria-label="RF filter mode">
            <button
              type="button"
              className={`btn sm ${draft.customMatrixEnabled ? 'active' : ''}`}
              disabled={disabled}
              onClick={() => updateOption({ customMatrixEnabled: true })}
            >
              Manual
            </button>
            <button
              type="button"
              className={`btn sm ${!draft.customMatrixEnabled ? 'active' : ''}`}
              disabled={disabled}
              onClick={() => updateOption({ customMatrixEnabled: false })}
            >
              Auto
            </button>
          </div>
          <div className="rf-filter-bypass-group" aria-label="RF filter bypass policy">
            <label className="ps-check">
              <input
                type="checkbox"
                checked={draft.rxBypassAll}
                disabled={disabled}
                onChange={(e) => updateOption({ rxBypassAll: e.currentTarget.checked })}
              />
              <span className="ps-check-box" />
              <span>All RX</span>
            </label>
            <label className="ps-check">
              <input
                type="checkbox"
                checked={draft.rxBypassOnTx}
                disabled={disabled}
                onChange={(e) => updateOption({ rxBypassOnTx: e.currentTarget.checked })}
              />
              <span className="ps-check-box" />
              <span>TX</span>
            </label>
            <label className="ps-check">
              <input
                type="checkbox"
                checked={draft.rxBypassOnPureSignal}
                disabled={disabled}
                onChange={(e) => updateOption({ rxBypassOnPureSignal: e.currentTarget.checked })}
              />
              <span className="ps-check-box" />
              <span>PureSignal</span>
            </label>
          </div>
        </div>
      </div>

      {statusMessages.length > 0 ? (
        <div className={`rf-filter-message${dirty ? ' is-dirty' : ''}`} role="status">
          {statusMessages.join(' ')}
        </div>
      ) : null}

      {selectedProfile ? (
        <>
          <div className="rf-filter-profile-row">
            <div className="rf-filter-profile-label">
              <span>Profile</span>
              <em>{selectedProfile.label}</em>
            </div>
            <div className="rf-filter-profile-tabs">
              {draft.profiles.map((profile) => (
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

          <div className="rf-filter-matrix-grid">
            <FilterRows
              title="RX BPF / HPF"
              rows={selectedProfile.rxFilters}
              disabled={disabled}
              onRange={(key, patch) => saveRange('rxFilters', key, patch)}
            />
            <FilterRows
              title="TX LPF"
              rows={selectedProfile.txFilters}
              disabled={disabled}
              onRange={(key, patch) => saveRange('txFilters', key, patch)}
            />
          </div>
        </>
      ) : (
        <div className="rf-filter-empty">RF filter matrix unavailable.</div>
      )}
    </div>
  );
}
