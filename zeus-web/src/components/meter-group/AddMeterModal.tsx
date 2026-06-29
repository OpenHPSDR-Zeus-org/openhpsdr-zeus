// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// Add Meter modal — three-column split view. Modelled on the workspace's
// `AddPanelModal` for the left-rail / card-list visual chrome, then
// extended with a third column on the right that lets the operator
// configure the new widget before pressing Add: change the label,
// pick one of the four meter shapes (analog gauge / vertical bar /
// pull-down arc / horizontal bar), tweak min/max, toggle peak-hold.
//
// UX flow: filter chip → meter card → right-pane fills with that
// reading's catalog defaults → operator tweaks → Add. The right pane
// shows a placeholder until a meter is selected; selecting a different
// meter resets the pane to that reading's defaults so stale overrides
// don't leak across selections.

import { useEffect, useState } from 'react';
import {
  METER_CATALOG,
  METER_FILTERS,
  METER_KINDS,
  METER_KIND_LABELS,
  meterMatchesFilter,
  type MeterDefaultKind,
  type MeterFilter,
  type MeterReadingDef,
  type MeterReadingId,
} from '../meters/meterCatalog';
import type { MeterGroupWidgetSettings } from './meterGroupConfig';

interface AddMeterModalProps {
  /** Reading ids already in the active group. Used to flag duplicates
   *  with a "+ Add another" badge — meter groups happily host the same
   *  reading twice (e.g. one BigArc + one HBar), so we don't filter
   *  them out, just label them. */
  existingReadings: Set<string>;
  onAdd: (
    id: MeterReadingId,
    kind: MeterDefaultKind,
    settings: MeterGroupWidgetSettings,
  ) => void;
  onClose: () => void;
}

const FILTER_LABEL: Record<MeterFilter, string> = {
  all: 'All',
  rx: 'RX',
  tx: 'TX',
  power: 'Power',
  stage: 'Stage',
  agc: 'AGC',
};

interface DraftConfig {
  label: string;
  kind: MeterDefaultKind;
  min: number;
  max: number;
  peakHold: boolean;
}

function defaultConfig(def: MeterReadingDef): DraftConfig {
  return {
    label: def.label,
    kind: def.defaultKind,
    min: def.defaultMin,
    max: def.defaultMax,
    peakHold: true,
  };
}

export function AddMeterModal({
  existingReadings,
  onAdd,
  onClose,
}: AddMeterModalProps) {
  const [searchTerm, setSearchTerm] = useState('');
  const [selectedFilter, setSelectedFilter] = useState<MeterFilter>('all');
  const [selectedId, setSelectedId] = useState<MeterReadingId | null>(null);
  const [draft, setDraft] = useState<DraftConfig | null>(null);

  const items = Object.values(METER_CATALOG).filter((def) => {
    if (!meterMatchesFilter(def, selectedFilter)) return false;
    if (searchTerm) {
      const term = searchTerm.toLowerCase();
      return (
        def.label.toLowerCase().includes(term) ||
        def.short.toLowerCase().includes(term) ||
        def.id.toLowerCase().includes(term)
      );
    }
    return true;
  });

  // Reset draft to the freshly-selected reading's catalog defaults so
  // unrelated tweaks don't leak forward. Operator-edited values for the
  // currently-selected reading are preserved across re-renders by the
  // null check.
  useEffect(() => {
    if (selectedId === null) {
      setDraft(null);
      return;
    }
    const def = METER_CATALOG[selectedId];
    if (!def) {
      setDraft(null);
      return;
    }
    setDraft(defaultConfig(def));
  }, [selectedId]);

  const submit = () => {
    if (!selectedId || !draft) return;
    const def = METER_CATALOG[selectedId];
    if (!def) return;
    const settings: MeterGroupWidgetSettings = {};
    // Only persist values that diverge from the catalog defaults so the
    // widget keeps tracking catalog updates (e.g. revised PA defaults
    // for the connected board) until the operator deliberately overrides.
    if (draft.label.trim() && draft.label !== def.label) {
      settings.label = draft.label.trim();
    }
    if (draft.min !== def.defaultMin) settings.min = draft.min;
    if (draft.max !== def.defaultMax) settings.max = draft.max;
    if (draft.peakHold === false) settings.peakHold = false;
    onAdd(selectedId, draft.kind, settings);
    // Modal stays open after Add so the operator can drop several meters in
    // one trip — the parent's `existingReadings` set updates on each commit,
    // so the just-added card flips to "+ Add another" automatically.
  };

  const updateDraft = <K extends keyof DraftConfig>(
    key: K,
    value: DraftConfig[K],
  ) => {
    setDraft((d) => (d ? { ...d, [key]: value } : d));
  };

  return (
    <div
      className="modal-backdrop"
      style={{
        position: 'fixed',
        inset: 0,
        background: 'rgba(0, 0, 0, 0.7)',
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        zIndex: 10000,
      }}
      onClick={onClose}
    >
      <div
        className="add-panel-modal add-meter-modal"
        onClick={(e) => e.stopPropagation()}
        role="dialog"
        aria-label="Add meter"
        data-testid="add-meter-modal"
      >
        <div className="add-panel-modal-header">
          <h2>Add Meter</h2>
          <button
            type="button"
            className="workspace-tile-close"
            aria-label="Close add-meter modal"
            onClick={onClose}
            style={{ width: 22, height: 22 }}
          >
            ×
          </button>
        </div>

        <div className="add-panel-modal-rail" data-testid="add-meter-rail">
          {METER_FILTERS.map((f) => (
            <button
              key={f}
              type="button"
              className="add-panel-category-btn"
              aria-pressed={selectedFilter === f}
              onClick={() => setSelectedFilter(f)}
              data-testid={`add-meter-filter-${f}`}
            >
              {FILTER_LABEL[f]}
            </button>
          ))}
        </div>

        <div className="add-panel-modal-body">
          <input
            type="text"
            className="add-panel-search"
            placeholder="Search meters…"
            value={searchTerm}
            onChange={(e) => setSearchTerm(e.target.value)}
            aria-label="Search meters"
          />

          <div className="add-panel-cards" data-testid="add-meter-cards">
            {items.length === 0 ? (
              <div className="add-panel-empty">No meters match</div>
            ) : (
              items.map((def) => {
                const showMultiBadge = existingReadings.has(def.id);
                const isSelected = selectedId === def.id;
                return (
                  <button
                    key={def.id}
                    type="button"
                    className="add-panel-card"
                    data-meter-id={def.id}
                    aria-pressed={isSelected}
                    style={
                      isSelected
                        ? {
                            borderColor: 'var(--accent)',
                            background: 'var(--bg-2)',
                          }
                        : undefined
                    }
                    onClick={() => setSelectedId(def.id)}
                  >
                    <span className="add-panel-card-title">
                      {def.label}
                      {showMultiBadge && (
                        <span className="add-panel-card-title-multi">
                          + Add another
                        </span>
                      )}
                    </span>
                    <span className="add-panel-card-tags">
                      {def.category} · {def.unit} · {def.defaultKind}
                    </span>
                  </button>
                );
              })
            )}
          </div>
        </div>

        <div
          className="add-meter-config-pane"
          data-testid="add-meter-config-pane"
        >
          {!draft || !selectedId ? (
            <div className="add-meter-config-empty">
              Select a meter to configure it.
            </div>
          ) : (
            <ConfigForm
              def={METER_CATALOG[selectedId]}
              draft={draft}
              update={updateDraft}
              onAdd={submit}
              onCancel={() => {
                setSelectedId(null);
                setDraft(null);
              }}
            />
          )}
        </div>
      </div>
    </div>
  );
}

interface ConfigFormProps {
  def: MeterReadingDef;
  draft: DraftConfig;
  update: <K extends keyof DraftConfig>(key: K, value: DraftConfig[K]) => void;
  onAdd: () => void;
  onCancel: () => void;
}

function ConfigForm({ def, draft, update, onAdd, onCancel }: ConfigFormProps) {
  return (
    <>
      <div className="add-meter-config-title">
        <span className="add-meter-config-eyebrow">Configure</span>
        <span className="add-meter-config-name">{def.label}</span>
      </div>

      <label className="add-meter-config-row">
        <span className="add-meter-config-label">Name</span>
        <input
          type="text"
          className="add-meter-config-input"
          value={draft.label}
          onChange={(e) => update('label', e.target.value)}
          placeholder={def.label}
          aria-label="Meter name"
        />
      </label>

      <div className="add-meter-config-row">
        <span className="add-meter-config-label">Type</span>
        <div className="add-meter-kind-grid">
          {METER_KINDS.map((k) => {
            const active = draft.kind === k;
            return (
              <button
                key={k}
                type="button"
                className="add-meter-kind-btn"
                aria-pressed={active}
                onClick={() => update('kind', k)}
                data-testid={`add-meter-kind-${k}`}
              >
                <span className="add-meter-kind-glyph">
                  <KindGlyph kind={k} />
                </span>
                <span className="add-meter-kind-name">
                  {METER_KIND_LABELS[k]}
                </span>
              </button>
            );
          })}
        </div>
      </div>

      <div className="add-meter-config-row add-meter-config-row-pair">
        <label className="add-meter-config-half">
          <span className="add-meter-config-label">Min ({def.unit})</span>
          <input
            type="number"
            className="add-meter-config-input"
            value={Number.isFinite(draft.min) ? draft.min : ''}
            onChange={(e) => {
              const n = Number(e.target.value);
              if (Number.isFinite(n)) update('min', n);
            }}
            aria-label="Axis minimum"
          />
        </label>
        <label className="add-meter-config-half">
          <span className="add-meter-config-label">Max ({def.unit})</span>
          <input
            type="number"
            className="add-meter-config-input"
            value={Number.isFinite(draft.max) ? draft.max : ''}
            onChange={(e) => {
              const n = Number(e.target.value);
              if (Number.isFinite(n)) update('max', n);
            }}
            aria-label="Axis maximum"
          />
        </label>
      </div>

      <label className="add-meter-config-toggle">
        <input
          type="checkbox"
          checked={draft.peakHold}
          onChange={(e) => update('peakHold', e.target.checked)}
        />
        <span>Show peak-hold tick</span>
      </label>

      <div className="add-meter-config-actions">
        <button
          type="button"
          className="add-meter-cancel-btn"
          onClick={onCancel}
        >
          Cancel
        </button>
        <button
          type="button"
          className="add-meter-add-btn"
          onClick={onAdd}
          disabled={draft.min >= draft.max}
          data-testid="add-meter-confirm"
        >
          Add
        </button>
      </div>
    </>
  );
}

function KindGlyph({ kind }: { kind: MeterDefaultKind }) {
  // Tiny SVG silhouettes so the operator can pick by shape, not by name.
  // Stroke-only so the active/idle treatment lives in CSS via currentColor.
  switch (kind) {
    case 'bigarc':
      return (
        <svg viewBox="0 0 24 16" width="24" height="16" aria-hidden="true">
          <path
            d="M3 14 A 9 9 0 0 1 21 14"
            fill="none"
            stroke="currentColor"
            strokeWidth="1.6"
            strokeLinecap="round"
          />
          <line x1="12" y1="14" x2="17" y2="6" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" />
        </svg>
      );
    case 'vucolumn':
      return (
        <svg viewBox="0 0 24 16" width="24" height="16" aria-hidden="true">
          <rect x="9" y="2" width="6" height="12" fill="none" stroke="currentColor" strokeWidth="1.4" rx="1" />
          <rect x="9" y="7" width="6" height="7" fill="currentColor" opacity="0.6" />
        </svg>
      );
    case 'pulldown':
      return (
        <svg viewBox="0 0 24 16" width="24" height="16" aria-hidden="true">
          <path
            d="M3 14 A 9 9 0 0 1 21 14"
            fill="none"
            stroke="currentColor"
            strokeWidth="1.6"
            strokeLinecap="round"
          />
          <line x1="21" y1="14" x2="15" y2="7" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" />
        </svg>
      );
    case 'hbar':
      return (
        <svg viewBox="0 0 24 16" width="24" height="16" aria-hidden="true">
          <rect x="2" y="6" width="20" height="4" fill="none" stroke="currentColor" strokeWidth="1.4" rx="1" />
          <rect x="2" y="6" width="12" height="4" fill="currentColor" opacity="0.6" />
        </svg>
      );
  }
}
