// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Filter ribbon — drops down above the panadapter when toggled. Right rail
// holds three sections inside the existing card width:
//
//   FAVORITES — 3 drop-target chips, always populated. Drag any preset (or
//               VAR1) onto a slot to swap.
//   PRESETS   — F1..F10 + VAR1 + VAR2 from the Thetis-default preset table
//               (with server-side VAR overrides applied). Each chip is
//               draggable into a favorite slot.
//   CUSTOM    — Lo/Hi Hz inputs that arm VAR1 and persist its stored width;
//               drag the VAR1 chip into a favorite to pin a custom width.
//
// Left side (top readouts row, mini-pan, hint) is unchanged.
//
// Layout invariant: the ribbon's vertical footprint is set by the mini-pan
// height. The right rail must fit inside that height — chips are deliberately
// compact (4×3 grid, ~22px tall) so the full preset table + favorites + custom
// row all fit without forcing the ribbon to grow.

import { useCallback, useEffect, useMemo, useState } from 'react';
import { useConnectionStore } from '../../state/connection-store';
import {
  setFilter,
  setFilterAdvancedPaneOpen,
  setFilterPresetOverride,
  getFilterPresets,
  type FilterPresetDto,
  type RxMode,
} from '../../api/client';
import {
  getPresetsForMode,
  nudgeStepHz,
  type FilterPresetSlot,
} from './filterPresets';
import { FilterMiniPan } from './FilterMiniPan';
import { useFavoritesForMode } from '../../state/filter-favorites-store';

const LOCAL_STORAGE_KEY = 'zeus.filter.advancedPaneOpen';
export const FILTER_DRAG_MIME = 'application/x-zeus-filter-slot';

const CUSTOM_MIN = 0;
const CUSTOM_MAX = 10000;

function cachePaneOpenLocal(open: boolean) {
  try { window.localStorage.setItem(LOCAL_STORAGE_KEY, open ? '1' : '0'); } catch { /* ok */ }
}

export function useFilterRibbonOpenSync() {
  useEffect(() => {
    try {
      const cached = window.localStorage.getItem(LOCAL_STORAGE_KEY);
      if (cached === '1') {
        useConnectionStore.setState({ filterAdvancedPaneOpen: true });
      }
    } catch { /* ok */ }
  }, []);
}

function isSymmetricMode(mode: RxMode): boolean {
  return mode === 'AM' || mode === 'SAM' || mode === 'DSB' || mode === 'FM';
}

function signedToAbs(mode: RxMode, low: number, high: number): { lo: number; hi: number } {
  if (isSymmetricMode(mode)) {
    return { lo: 0, hi: Math.max(Math.abs(low), Math.abs(high)) };
  }
  return {
    lo: Math.min(Math.abs(low), Math.abs(high)),
    hi: Math.max(Math.abs(low), Math.abs(high)),
  };
}

function absToSigned(mode: RxMode, loAbs: number, hiAbs: number): { low: number; high: number } {
  const lo = Math.max(CUSTOM_MIN, Math.min(CUSTOM_MAX, Math.round(loAbs)));
  const hi = Math.max(CUSTOM_MIN, Math.min(CUSTOM_MAX, Math.round(hiAbs)));
  const [lCap, hCap] = lo <= hi ? [lo, hi] : [hi, lo];
  switch (mode) {
    case 'USB': case 'DIGU': case 'CWU':
      return { low: lCap, high: hCap };
    case 'LSB': case 'DIGL': case 'CWL':
      return { low: -hCap, high: -lCap };
    case 'AM': case 'SAM': case 'DSB': case 'FM':
      return { low: -hCap, high: hCap };
  }
}

// Merge server VAR overrides on top of the local Thetis-default preset table.
function mergePresets(mode: RxMode, server: FilterPresetDto[] | null): FilterPresetSlot[] {
  const local = getPresetsForMode(mode);
  if (!server) return local.slice();
  return local.map((slot) => {
    if (!slot.isVar) return slot;
    const srv = server.find((s) => s.slotName === slot.slotName);
    return srv ? { ...slot, lowHz: srv.lowHz, highHz: srv.highHz } : slot;
  });
}

/** Which part of the ribbon to render. 'all' (default) is the original
 *  combined card. 'minipan' renders only the left mini-pan + hint; 'presets'
 *  renders only the right preset grid + custom row. The split lets the
 *  workspace host the mini-pan and the preset table as two separate,
 *  independently-sizable panels. Both sub-sections drive the same
 *  connection-store state, so they stay in sync automatically. */
type FilterRibbonSection = 'all' | 'minipan' | 'presets';

export function FilterRibbon({
  embedded = false,
  section = 'all',
}: { embedded?: boolean; section?: FilterRibbonSection } = {}) {
  const mode = useConnectionStore((s) => s.mode);
  const filterLow = useConnectionStore((s) => s.filterLowHz);
  const filterHigh = useConnectionStore((s) => s.filterHighHz);
  const filterPresetName = useConnectionStore((s) => s.filterPresetName);
  const open = useConnectionStore((s) => s.filterAdvancedPaneOpen);
  const applyState = useConnectionStore((s) => s.applyState);
  const favoriteSlotNames = useFavoritesForMode(mode);
  const [serverPresets, setServerPresets] = useState<FilterPresetDto[] | null>(null);
  const [dragSlot, setDragSlot] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    getFilterPresets(mode)
      .then((list) => { if (!cancelled) setServerPresets(list); })
      .catch(() => { /* fall back to local table */ });
    return () => { cancelled = true; };
  }, [mode]);

  const presets = useMemo(() => mergePresets(mode, serverPresets), [mode, serverPresets]);

  // PRESETS grid order: F-slots ascending by passband width, then VAR1, VAR2.
  // The local table is descending (5.0k → 1.0k); operators read narrow-to-wide
  // more naturally, and pinning VAR slots to the end keeps drag targets stable
  // even when their stored widths change.
  const sortedPresets = useMemo(() => {
    const fSlots = presets.filter((p) => !p.isVar).slice().sort((a, b) => {
      return Math.abs(a.highHz - a.lowHz) - Math.abs(b.highHz - b.lowHz);
    });
    const varSlots = presets.filter((p) => p.isVar);
    return [...fSlots, ...varSlots];
  }, [presets]);

  const selectPreset = useCallback((slot: FilterPresetSlot) => {
    useConnectionStore.setState({
      filterLowHz: slot.lowHz,
      filterHighHz: slot.highHz,
      filterPresetName: slot.slotName,
    });
    setFilter(slot.lowHz, slot.highHz, slot.slotName)
      .then(applyState)
      .catch(() => {});
  }, [applyState]);

  const closeRibbon = useCallback(() => {
    useConnectionStore.setState({ filterAdvancedPaneOpen: false });
    cachePaneOpenLocal(false);
    setFilterAdvancedPaneOpen(false).catch(() => {});
  }, []);

  // CUSTOM Lo/Hi inputs always mirror the live filter, so clicking any
  // preset (F1..F10 or VAR1/VAR2) immediately repaints the entry fields
  // with that slot's actual lo/hi. Where edits LAND is a separate question:
  //   - VAR1 active → write to VAR1
  //   - VAR2 active → write to VAR2
  //   - F1..F10 active → fall back to VAR1 (F-slots are Thetis defaults
  //     and must never be overwritten; freeform edits land in VAR1).
  const activeVarSlot: 'VAR1' | 'VAR2' = filterPresetName === 'VAR2' ? 'VAR2' : 'VAR1';
  const seedAbs = signedToAbs(mode, filterLow, filterHigh);
  const [loDraft, setLoDraft] = useState<string>(String(seedAbs.lo));
  const [hiDraft, setHiDraft] = useState<string>(String(seedAbs.hi));

  // Reseed the drafts when the live filter changes (preset click, mode flip,
  // server reconciliation). The CUSTOM inputs always reflect what's playing.
  useEffect(() => {
    const abs = signedToAbs(mode, filterLow, filterHigh);
    setLoDraft(String(abs.lo));
    setHiDraft(String(abs.hi));
  }, [mode, filterLow, filterHigh]);

  const commitCustom = useCallback(async () => {
    const loAbs = Number.parseInt(loDraft, 10);
    const hiAbs = Number.parseInt(hiDraft, 10);
    if (!Number.isFinite(loAbs) || !Number.isFinite(hiAbs)) return;
    const { low, high } = absToSigned(mode, loAbs, hiAbs);
    if (high <= low + 50) return;
    // Writes land on the currently-active VAR slot. F1..F10 are Thetis
    // defaults and never get overwritten — when one is active the edit falls
    // back to VAR1 (set by activeVarSlot above).
    const target = activeVarSlot;
    useConnectionStore.setState({
      filterLowHz: low,
      filterHighHz: high,
      filterPresetName: target,
    });
    try {
      await setFilter(low, high, target).then(applyState);
      await setFilterPresetOverride(mode, target, low, high);
      // Refresh preset list so the VAR chip shows the new values.
      const fresh = await getFilterPresets(mode);
      setServerPresets(fresh);
    } catch { /* next state poll reconciles */ }
  }, [loDraft, hiDraft, mode, applyState, activeVarSlot]);

  const onCustomKeyDown = (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Enter') e.currentTarget.blur();
  };

  // Keyboard arrow nudging — only when ribbon is open and we're not focused
  // on the CUSTOM inputs. The mini-pan owns this gesture; a presets-only
  // instance must NOT also register the listener or every arrow press would
  // nudge twice when both split panels are docked.
  useEffect(() => {
    if (section === 'presets') return;
    if (!embedded && !open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.target instanceof HTMLInputElement) return;
      if (e.key === 'Escape' && !embedded) { closeRibbon(); return; }
      if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return;
      const step = nudgeStepHz(mode) * (e.shiftKey ? 10 : 1);
      const dir = e.key === 'ArrowRight' ? 1 : -1;
      const s = useConnectionStore.getState();
      const newHi = s.filterHighHz + dir * step;
      if (newHi <= s.filterLowHz + 50) return;
      const slot = s.filterPresetName && /^VAR[12]$/.test(s.filterPresetName) ? s.filterPresetName : 'VAR1';
      useConnectionStore.setState({ filterHighHz: newHi, filterPresetName: slot });
      setFilter(s.filterLowHz, newHi, slot).then(applyState).catch(() => {});
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [embedded, open, mode, applyState, closeRibbon, section]);

  if (!embedded && !open) return null;
  // The mini-pan section can render before the preset table resolves; only
  // the presets/all sections need a populated table to show anything.
  if (section !== 'minipan' && presets.length === 0) return null;

  const currentWidth = Math.abs(filterHigh - filterLow);
  const isLowDisabled = isSymmetricMode(mode);

  const startDrag = (e: React.DragEvent, slotName: string) => {
    e.dataTransfer.setData(FILTER_DRAG_MIME, slotName);
    e.dataTransfer.effectAllowed = 'move';
    setDragSlot(slotName);
  };
  const endDrag = () => { setDragSlot(null); };

  return (
    <div
      className={`filter-ribbon ${embedded ? 'filter-ribbon--embedded' : ''}`}
      role="region"
      aria-label="Advanced filter ribbon"
    >
      {!embedded && (
        <button
          type="button"
          aria-label="Close filter ribbon"
          onClick={closeRibbon}
          className="filter-ribbon__close"
        >
          ×
        </button>
      )}

      <div className="filter-ribbon__body">
        {/* Left column: full-width mini-pan, footer hint. The top BW/LO/PB/HI
            readout row was removed — its data is already shown in the topbar
            chips and the mini-pan visualises the same passband. Hidden when
            this instance is rendering the presets-only panel. */}
        {section !== 'presets' && (
          <div className="filter-ribbon__main">
            <div className="filter-ribbon__minipan">
              <FilterMiniPan />
            </div>
          </div>
        )}

        {/* Right column: presets + custom. The in-ribbon FAVORITES row was
            removed — drag any preset chip below onto one of the three filter
            buttons in the control strip to pin it (same UX as Mode/Band/Step).
            Hidden when this instance is rendering the mini-pan-only panel. */}
        {section !== 'minipan' && (
        <div className="filter-ribbon__presets">
          <div className="filter-ribbon__section-label">PRESETS</div>
          <div className="filter-ribbon__preset-grid">
            {sortedPresets.map((slot) => {
              const slotWidth = Math.abs(slot.highHz - slot.lowHz);
              const isActive = filterPresetName === slot.slotName
                || (Math.abs(slotWidth - currentWidth) <= 20 && !slot.isVar);
              const isPinned = favoriteSlotNames.includes(slot.slotName);
              const label = slot.isVar ? slot.slotName : slot.label;
              return (
                <button
                  key={slot.slotName}
                  type="button"
                  draggable
                  onDragStart={(e) => startDrag(e, slot.slotName)}
                  onDragEnd={endDrag}
                  onClick={() => selectPreset(slot)}
                  title={`${slot.slotName}: ${slot.lowHz >= 0 ? '+' : ''}${slot.lowHz} / ${slot.highHz >= 0 ? '+' : ''}${slot.highHz} Hz · drag onto a favorite to pin`}
                  className={`filter-ribbon__chip ${isActive ? 'is-active' : ''} ${isPinned ? 'is-pinned' : ''} ${dragSlot === slot.slotName ? 'is-dragging' : ''}`}
                >
                  {label}
                </button>
              );
            })}
          </div>

          <div className="filter-ribbon__section-label">CUSTOM · {activeVarSlot}</div>
          <div className="filter-ribbon__custom-row">
            <input
              type="number"
              min={CUSTOM_MIN}
              max={CUSTOM_MAX}
              step={50}
              value={loDraft}
              onChange={(e) => setLoDraft(e.currentTarget.value)}
              onBlur={commitCustom}
              onKeyDown={onCustomKeyDown}
              disabled={isLowDisabled}
              aria-label="Custom filter low edge in Hz"
              className="filter-ribbon__custom-input mono"
            />
            <span className="filter-ribbon__custom-sep">–</span>
            <input
              type="number"
              min={CUSTOM_MIN}
              max={CUSTOM_MAX}
              step={50}
              value={hiDraft}
              onChange={(e) => setHiDraft(e.currentTarget.value)}
              onBlur={commitCustom}
              onKeyDown={onCustomKeyDown}
              aria-label="Custom filter high edge in Hz"
              className="filter-ribbon__custom-input mono"
            />
            <span className="filter-ribbon__custom-unit">Hz</span>
          </div>
        </div>
        )}
      </div>
    </div>
  );
}
