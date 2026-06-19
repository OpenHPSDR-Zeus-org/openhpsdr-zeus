// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.

import { useEffect, useState, type CSSProperties } from 'react';
import { useBandPlanStore } from '../../state/bandPlan';
import type { BandSegment, BandAllocation, ModeRestriction } from '../../api/bands';
import { ConfirmDialog } from '../../layout/ConfirmDialog';

const ALLOCATION_OPTIONS: BandAllocation[] = ['Amateur', 'SWL', 'Broadcast', 'Reserved', 'Unknown'];
const MODE_OPTIONS: ModeRestriction[] = ['Any', 'CwOnly', 'PhoneOnly', 'DigitalOnly', 'CwAndDigital'];

type EditRow = BandSegment & { _key: number };

let keyCounter = 0;
function nextKey() { return ++keyCounter; }

function segToRow(seg: BandSegment): EditRow {
  return { ...seg, _key: nextKey() };
}

const cell: CSSProperties = {
  padding: '4px 6px',
  borderBottom: '1px solid var(--panel-border)',
  fontSize: 11,
  verticalAlign: 'middle',
};

const inputStyle: CSSProperties = {
  background: 'var(--bg-0)',
  border: '1px solid var(--line)',
  borderRadius: 'var(--r-sm)',
  color: 'var(--fg-1)',
  fontFamily: 'var(--font-mono)',
  fontSize: 11,
  padding: '2px 4px',
  width: '100%',
};

const selectStyle: CSSProperties = { ...inputStyle, cursor: 'pointer' };

export function BandPlanEditor() {
  const store = useBandPlanStore();
  const [rows, setRows] = useState<EditRow[]>([]);
  const [dirty, setDirty] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [selectedRegion, setSelectedRegion] = useState(store.currentRegionId);
  const [resetPending, setResetPending] = useState(false);

  // Load plan for selected region whenever it changes
  useEffect(() => {
    setRows(store.segments.map(segToRow));
    setDirty(false);
    setError(null);
  }, [store.segments, store.currentRegionId]);

  // Switching region dropdown
  const handleRegionChange = async (regionId: string) => {
    setSelectedRegion(regionId);
    await store.changeRegion(regionId);
  };

  const updateRow = (key: number, patch: Partial<BandSegment>) => {
    setRows((prev) => prev.map((r) => r._key === key ? { ...r, ...patch } : r));
    setDirty(true);
  };

  const addRow = () => {
    const last = rows[rows.length - 1];
    const newLow = last ? last.highHz + 1 : 1_800_000;
    setRows((prev) => [...prev, segToRow({
      regionId: selectedRegion,
      lowHz: newLow,
      highHz: newLow + 100_000,
      label: 'New Segment',
      allocation: 'Amateur',
      modeRestriction: 'Any',
      maxPowerW: null,
      notes: null,
    })]);
    setDirty(true);
  };

  const deleteRow = (key: number) => {
    setRows((prev) => prev.filter((r) => r._key !== key));
    setDirty(true);
  };

  const handleSave = async () => {
    setSaving(true);
    setError(null);
    try {
      const sorted = [...rows].sort((a, b) => a.lowHz - b.lowHz);

      const segs: BandSegment[] = sorted.map(({ _key, ...rest }) => rest);
      await store.saveOverride(selectedRegion, segs);
      setDirty(false);
    } catch (e) {
      setError(String(e));
    } finally {
      setSaving(false);
    }
  };

  const handleReset = async () => {
    setSaving(true);
    setError(null);
    try {
      await store.resetOverride(selectedRegion);
      setDirty(false);
    } catch (e) {
      setError(String(e));
    } finally {
      setSaving(false);
    }
  };

  const handleGuardToggle = async () => {
    await store.setGuardIgnore(!store.txGuardIgnore);
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 12, height: '100%' }}>
      {/* Region selector + TX guard toggle */}
      <div style={{ display: 'flex', alignItems: 'center', gap: 16, flexWrap: 'wrap' }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
          <span style={{ fontSize: 11, fontWeight: 700, letterSpacing: '0.1em', color: 'var(--fg-2)', textTransform: 'uppercase' }}>
            Region
          </span>
          <select
            value={selectedRegion}
            onChange={(e) => void handleRegionChange(e.target.value)}
            style={{ ...selectStyle, width: 200 }}
          >
            {store.regions.map((r) => (
              <option key={r.id} value={r.id}>{r.displayName}</option>
            ))}
          </select>
        </div>

        <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
          <label style={{ display: 'flex', alignItems: 'center', gap: 6, cursor: 'pointer', fontSize: 11 }}>
            <input
              type="checkbox"
              checked={store.txGuardIgnore}
              onChange={() => void handleGuardToggle()}
              style={{ accentColor: 'var(--accent)', cursor: 'pointer' }}
            />
            <span style={{ color: store.txGuardIgnore ? 'var(--accent)' : 'var(--fg-2)', textTransform: 'uppercase', letterSpacing: '0.1em', fontWeight: 700 }}>
              TX Guard disabled
            </span>
          </label>
          {store.txGuardIgnore && (
            <span style={{ fontSize: 10, color: 'rgba(255,80,80,0.8)', background: 'rgba(255,0,0,0.08)', padding: '2px 6px', borderRadius: 0, border: '1px solid rgba(255,80,80,0.3)' }}>
              ⚠ MOX will fire regardless of band legality
            </span>
          )}
        </div>
      </div>

      <div style={{ fontSize: 10, color: 'var(--fg-3)', background: 'rgba(255,160,40,0.06)', border: '1px solid rgba(255,160,40,0.15)', borderRadius: 0, padding: '6px 10px' }}>
        Defaults are best-effort. You are responsible for operating within your licence.
        Source files under Zeus.Server.Hosting/BandPlans/ — corrections welcome as PRs.
      </div>

      {error && (
        <div style={{ fontSize: 11, color: '#ff5555', background: 'rgba(255,0,0,0.08)', border: '1px solid rgba(255,80,80,0.3)', borderRadius: 0, padding: '6px 10px' }}>
          {error}
        </div>
      )}

      {/* Segment table */}
      <div style={{ flex: 1, minHeight: 0, overflow: 'auto', border: '1px solid var(--panel-border)', borderRadius: 0 }}>
        <table style={{ width: '100%', borderCollapse: 'collapse' }}>
          <thead>
            <tr style={{ background: 'var(--bg-0)', position: 'sticky', top: 0, zIndex: 1 }}>
              {['Low MHz', 'High MHz', 'Label', 'Allocation', 'Mode', 'Source', ''].map((h) => (
                <th key={h} style={{ ...cell, color: 'var(--fg-2)', fontWeight: 700, letterSpacing: '0.1em', textTransform: 'uppercase', textAlign: 'left', whiteSpace: 'nowrap' }}>
                  {h}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {rows.map((row) => (
              <tr key={row._key} style={{ background: 'transparent' }}
                onMouseEnter={(e) => (e.currentTarget.style.background = 'rgba(255,255,255,0.02)')}
                onMouseLeave={(e) => (e.currentTarget.style.background = 'transparent')}
              >
                <td style={{ ...cell, width: 80 }}>
                  <input
                    type="number"
                    value={row.lowHz / 1e6}
                    step={0.001}
                    style={inputStyle}
                    onChange={(e) => updateRow(row._key, { lowHz: Math.round(parseFloat(e.target.value) * 1e6) })}
                  />
                </td>
                <td style={{ ...cell, width: 80 }}>
                  <input
                    type="number"
                    value={row.highHz / 1e6}
                    step={0.001}
                    style={inputStyle}
                    onChange={(e) => updateRow(row._key, { highHz: Math.round(parseFloat(e.target.value) * 1e6) })}
                  />
                </td>
                <td style={{ ...cell, minWidth: 120 }}>
                  <input
                    type="text"
                    value={row.label}
                    style={inputStyle}
                    onChange={(e) => updateRow(row._key, { label: e.target.value })}
                  />
                </td>
                <td style={{ ...cell, width: 90 }}>
                  <select
                    value={row.allocation}
                    style={selectStyle}
                    onChange={(e) => updateRow(row._key, { allocation: e.target.value as BandAllocation })}
                  >
                    {ALLOCATION_OPTIONS.map((o) => <option key={o} value={o}>{o}</option>)}
                  </select>
                </td>
                <td style={{ ...cell, width: 100 }}>
                  <select
                    value={row.modeRestriction}
                    style={selectStyle}
                    onChange={(e) => updateRow(row._key, { modeRestriction: e.target.value as ModeRestriction })}
                  >
                    {MODE_OPTIONS.map((o) => <option key={o} value={o}>{o}</option>)}
                  </select>
                </td>
                <td style={{ ...cell, width: 60, color: row.regionId === selectedRegion ? 'var(--accent)' : 'var(--fg-3)', fontFamily: 'var(--font-mono)', fontSize: 10 }}>
                  {row.regionId}
                </td>
                <td style={{ ...cell, width: 36 }}>
                  <button
                    type="button"
                    onClick={() => deleteRow(row._key)}
                    title="Delete row"
                    style={{ background: 'transparent', border: 'none', color: 'var(--fg-3)', cursor: 'pointer', fontSize: 14, padding: 2 }}
                    onMouseEnter={(e) => (e.currentTarget.style.color = '#ff5555')}
                    onMouseLeave={(e) => (e.currentTarget.style.color = 'var(--fg-3)')}
                  >×</button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {/* Action row */}
      <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
        <button
          type="button"
          className="btn sm"
          onClick={addRow}
          style={{ fontSize: 11 }}
        >
          + Add Row
        </button>
        <div style={{ flex: 1 }} />
        <button
          type="button"
          className="btn ghost sm"
          onClick={() => setResetPending(true)}
          disabled={saving}
          style={{ fontSize: 11 }}
        >
          Reset to Defaults
        </button>
        <button
          type="button"
          className="btn sm"
          onClick={() => void handleSave()}
          disabled={!dirty || saving}
          style={{
            fontSize: 11,
            background: dirty ? 'rgba(255,160,40,0.18)' : undefined,
            borderColor: dirty ? 'var(--accent)' : undefined,
            color: dirty ? 'var(--accent)' : undefined,
          }}
        >
          {saving ? 'Saving…' : 'Save Changes'}
        </button>
      </div>
      {resetPending && (
        <ConfirmDialog
          title="Reset band plan"
          confirmLabel="Reset Defaults"
          onCancel={() => setResetPending(false)}
          onConfirm={() => {
            setResetPending(false);
            void handleReset();
          }}
        >
          <p>Reset the plan for {selectedRegion} to shipped defaults?</p>
          <p>Any local override rows for this region will be discarded.</p>
        </ConfirmDialog>
      )}
    </div>
  );
}
