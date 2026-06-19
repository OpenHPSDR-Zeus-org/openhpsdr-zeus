// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Unified filter control-strip widget. Three favorite filter-preset buttons
// + a "⋯" toggle that opens a popover containing every preset (F1..F10 plus
// VAR1, VAR2). Same UX as the Mode/Band/Step toolbar groups: click any chip
// to apply, drag any chip onto one of the three favorite buttons to pin.

import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { useConnectionStore } from '../../state/connection-store';
import {
  setFilter,
  getFilterPresets,
  type FilterPresetDto,
  type TxVfo,
} from '../../api/client';
import { useFilterFavoritesStore, useFavoritesForMode } from '../../state/filter-favorites-store';
import { FILTER_DRAG_MIME } from './filterRibbonShared';
import { getPresetsForMode } from './filterPresets';

export function FilterPanel() {
  const mode = useConnectionStore((s) => s.mode);
  const modeB = useConnectionStore((s) => s.modeB);
  const filterPresetName = useConnectionStore((s) => s.filterPresetName);
  const filterPresetNameB = useConnectionStore((s) => s.filterPresetNameB);
  const filterLow = useConnectionStore((s) => s.filterLowHz);
  const filterHigh = useConnectionStore((s) => s.filterHighHz);
  const filterLowB = useConnectionStore((s) => s.filterLowHzB);
  const filterHighB = useConnectionStore((s) => s.filterHighHzB);
  const rx2Enabled = useConnectionStore((s) => s.rx2Enabled);
  const rxFocus = useConnectionStore((s) => s.rxFocus);
  const applyState = useConnectionStore((s) => s.applyState);
  const loadFavorites = useFilterFavoritesStore((s) => s.load);
  const updateFavorites = useFilterFavoritesStore((s) => s.update);
  const activeReceiver: TxVfo = rxFocus === 'B' && rx2Enabled ? 'B' : 'A';
  const activeMode = activeReceiver === 'B' ? modeB : mode;
  const activeFilterPresetName = activeReceiver === 'B' ? filterPresetNameB : filterPresetName;
  const activeFilterLow = activeReceiver === 'B' ? filterLowB : filterLow;
  const activeFilterHigh = activeReceiver === 'B' ? filterHighB : filterHigh;
  const favoriteSlotNames = useFavoritesForMode(activeMode);

  // Seed from the local Thetis preset table so labels render correctly on
  // first paint. Without this, the buttons collapse to raw slot names
  // ("F4", "F5", "F6") for the time it takes /api/filter/presets to resolve,
  // because the lookup `presets.find(...)` returns undefined against an
  // empty array. Server VAR overrides land here too once the fetch completes.
  const localPresets = useMemo<FilterPresetDto[]>(
    () => getPresetsForMode(activeMode).map((p) => ({ ...p })),
    [activeMode],
  );
  const [presets, setPresets] = useState<FilterPresetDto[]>(localPresets);
  const [dragOverFav, setDragOverFav] = useState<number | null>(null);
  const [dragSlot, setDragSlot] = useState<string | null>(null);
  const [open, setOpen] = useState(false);
  const [popoverPos, setPopoverPos] = useState<{ top: number; left: number } | null>(null);

  const containerRef = useRef<HTMLDivElement | null>(null);
  const toggleRef = useRef<HTMLButtonElement | null>(null);
  const popoverRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => { void loadFavorites(activeMode); }, [activeMode, loadFavorites]);

  // Reseed `presets` to the new mode's local table whenever the mode flips,
  // so labels never reflect the old mode while the server fetch is in flight.
  useEffect(() => { setPresets(localPresets); }, [localPresets]);

  useEffect(() => {
    let cancelled = false;
    getFilterPresets(activeMode)
      .then((list) => { if (!cancelled && list.length > 0) setPresets(list); })
      .catch(() => { /* keep local fallback */ });
    return () => { cancelled = true; };
  }, [activeMode]);

  // Popover preset order: F-slots ascending by passband width, then VAR1, VAR2.
  // Matches the FilterRibbon panel's grid so operators see the same layout.
  const sortedPresets = useMemo(() => {
    const fSlots = presets.filter((p) => !p.isVar).slice().sort((a, b) => {
      return Math.abs(a.highHz - a.lowHz) - Math.abs(b.highHz - b.lowHz);
    });
    const varSlots = presets.filter((p) => p.isVar);
    return [...fSlots, ...varSlots];
  }, [presets]);

  const activeSlot = activeFilterPresetName ?? null;
  const currentWidth = Math.abs(activeFilterHigh - activeFilterLow);

  const selectPreset = useCallback(
    (slot: FilterPresetDto) => {
      useConnectionStore.setState({
        ...(activeReceiver === 'B'
          ? {
              filterLowHzB: slot.lowHz,
              filterHighHzB: slot.highHz,
              filterPresetNameB: slot.slotName,
            }
          : {
              filterLowHz: slot.lowHz,
              filterHighHz: slot.highHz,
              filterPresetName: slot.slotName,
            }),
      });
      setFilter(slot.lowHz, slot.highHz, slot.slotName, undefined, activeReceiver)
        .then(applyState)
        .catch(() => { /* next state poll reconciles */ });
    },
    [activeReceiver, applyState],
  );

  // Close popover on outside click or Escape. Treats clicks inside either
  // the trigger row or the portaled popover as "inside" so the menu doesn't
  // self-dismiss when the operator clicks one of its own chips.
  useEffect(() => {
    if (!open) return;
    const onDocDown = (e: MouseEvent) => {
      const t = e.target as Node | null;
      if (!t) return;
      if (containerRef.current?.contains(t)) return;
      if (popoverRef.current?.contains(t)) return;
      setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setOpen(false);
    };
    document.addEventListener('mousedown', onDocDown);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('mousedown', onDocDown);
      document.removeEventListener('keydown', onKey);
    };
  }, [open]);

  // Anchor the portaled popover to the "⋯" toggle. Re-measure on open and on
  // window resize / scroll so the menu tracks if the layout shifts. The
  // popover is rendered into document.body to escape `.control-strip`'s
  // `overflow: hidden` clip.
  useLayoutEffect(() => {
    if (!open) {
      setPopoverPos(null);
      return;
    }
    const measure = () => {
      const t = toggleRef.current;
      if (!t) return;
      const r = t.getBoundingClientRect();
      setPopoverPos({ top: r.bottom + 6, left: r.left });
    };
    measure();
    window.addEventListener('resize', measure);
    window.addEventListener('scroll', measure, true);
    return () => {
      window.removeEventListener('resize', measure);
      window.removeEventListener('scroll', measure, true);
    };
  }, [open]);

  const dropOnFav = useCallback(
    (idx: number, slotName: string) => {
      const next = [...favoriteSlotNames];
      const existing = next.indexOf(slotName);
      if (existing === idx) return;
      const displaced = next[idx];
      if (existing >= 0 && displaced !== undefined) {
        next[existing] = displaced;
      }
      next[idx] = slotName;
      void updateFavorites(activeMode, next);
    },
    [favoriteSlotNames, activeMode, updateFavorites],
  );

  const onFavDragOver = (idx: number) => (e: React.DragEvent) => {
    if (!e.dataTransfer.types.includes(FILTER_DRAG_MIME)) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = 'move';
    if (dragOverFav !== idx) setDragOverFav(idx);
  };
  const onFavDragLeave = () => setDragOverFav(null);
  const onFavDrop = (idx: number) => (e: React.DragEvent) => {
    const slotName = e.dataTransfer.getData(FILTER_DRAG_MIME);
    if (!slotName) return;
    e.preventDefault();
    dropOnFav(idx, slotName);
    setDragOverFav(null);
    setDragSlot(null);
  };

  const startDrag = (e: React.DragEvent, slotName: string) => {
    e.dataTransfer.setData(FILTER_DRAG_MIME, slotName);
    e.dataTransfer.effectAllowed = 'move';
    setDragSlot(slotName);
  };
  const endDrag = () => { setDragSlot(null); setDragOverFav(null); };

  if (activeMode === 'FM') return null;

  return (
    <div
      ref={containerRef}
      className="ctrl-group filter-bar toolbar-fav"
      style={{ minWidth: 176, position: 'relative' }}
    >
      <div className="label-xs ctrl-lbl">FILTER</div>
      <div className="btn-row" style={{ gap: 3 }}>
        {favoriteSlotNames.map((slotName, idx) => {
          const slot = presets.find((p) => p.slotName === slotName);
          const isActive = !!slot && activeSlot === slot.slotName;
          return (
            <button
              key={`fav-${idx}`}
              type="button"
              onClick={() => slot && selectPreset(slot)}
              onDragOver={onFavDragOver(idx)}
              onDragLeave={onFavDragLeave}
              onDrop={onFavDrop(idx)}
              className={`btn sm ${isActive ? 'active' : ''} ${dragOverFav === idx ? 'is-drop-target' : ''}`}
              title={
                slot
                  ? `${slot.slotName}: ${slot.lowHz >= 0 ? '+' : ''}${slot.lowHz} / ${slot.highHz >= 0 ? '+' : ''}${slot.highHz} Hz — drag a preset here to replace`
                  : `Empty slot — drop a preset here`
              }
              aria-label={`Filter favorite ${idx + 1}: ${slot ? slot.label : slotName}`}
            >
              {slot ? slot.label : slotName}
            </button>
          );
        })}
        <button
          ref={toggleRef}
          type="button"
          onClick={() => setOpen((v) => !v)}
          className={`btn sm ${open ? 'active' : ''}`}
          title="All filter presets — drag onto a favorite to pin"
          aria-expanded={open}
          style={{ marginLeft: 4 }}
        >
          ⋯
        </button>
      </div>

      {open && popoverPos && createPortal(
        <div
          ref={popoverRef}
          className="toolbar-fav__popover"
          role="dialog"
          aria-label="Filter presets"
          style={{ top: popoverPos.top, left: popoverPos.left }}
        >
          <div className="toolbar-fav__hint">DRAG ONTO A FAVORITE TO PIN</div>
          <div className="toolbar-fav__grid">
            {sortedPresets.map((slot) => {
              const slotWidth = Math.abs(slot.highHz - slot.lowHz);
              const isActive = activeSlot === slot.slotName
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
                  onClick={() => {
                    selectPreset(slot);
                    setOpen(false);
                  }}
                  title={`${slot.slotName}: ${slot.lowHz >= 0 ? '+' : ''}${slot.lowHz} / ${slot.highHz >= 0 ? '+' : ''}${slot.highHz} Hz — drag onto a favorite to pin`}
                  className={`toolbar-fav__chip ${isActive ? 'is-active' : ''} ${isPinned ? 'is-pinned' : ''} ${dragSlot === slot.slotName ? 'is-dragging' : ''}`}
                >
                  {label}
                </button>
              );
            })}
          </div>
        </div>,
        document.body,
      )}
    </div>
  );
}
