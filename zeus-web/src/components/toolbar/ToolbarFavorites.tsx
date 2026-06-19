// SPDX-License-Identifier: GPL-2.0-or-later
//
// Generic three-favorite + dropdown picker used by Mode, Band, and Step in
// the control strip. Mirrors the FilterPanel/FilterRibbon favorites pattern:
// three pinned buttons, a "⋯" toggle that opens a popover containing every
// option, and drag-to-pin gesture from any popover chip into one of the
// three favorite slots. Click a favorite (or any option in the popover) to
// apply it.

import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { useToolbarFavoritesStore, type ToolbarFavKind } from '../../state/toolbar-favorites-store';
import { toolbarFavDragMime } from './toolbarFavoriteDrag';

export type ToolbarOption = {
  key: string;
  label: string;
  title?: string;
};

export type ToolbarFavoritesProps = {
  kind: ToolbarFavKind;
  label: string;
  options: readonly ToolbarOption[];
  currentKey: string | null;
  onSelect: (key: string) => void;
  minWidth?: number;
};

export function ToolbarFavorites({
  kind,
  label,
  options,
  currentKey,
  onSelect,
  minWidth = 180,
}: ToolbarFavoritesProps) {
  const slots = useToolbarFavoritesStore((s) => s[kind]);
  const setFavorites = useToolbarFavoritesStore((s) => s.setFavorites);

  const [open, setOpen] = useState(false);
  const [dragKey, setDragKey] = useState<string | null>(null);
  const [dragOverFav, setDragOverFav] = useState<number | null>(null);
  const [popoverPos, setPopoverPos] = useState<{ top: number; left: number } | null>(null);
  const containerRef = useRef<HTMLDivElement | null>(null);
  const toggleRef = useRef<HTMLButtonElement | null>(null);
  const popoverRef = useRef<HTMLDivElement | null>(null);
  const dragMime = toolbarFavDragMime(kind);

  // Repair a stale slot list (legacy / corrupted persistence) by replacing
  // unknown keys with the first non-favorite option so the operator never
  // sees an empty button.
  const validKeys = new Set(options.map((o) => o.key));
  const safeSlots: string[] =
    slots.length === 3 && slots.every((k) => validKeys.has(k))
      ? slots
      : (() => {
          const fallback: string[] = [];
          for (const o of options) {
            if (fallback.length === 3) break;
            fallback.push(o.key);
          }
          const filler = options[0]?.key;
          while (fallback.length < 3 && filler !== undefined) {
            fallback.push(filler);
          }
          return fallback;
        })();

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
    (idx: number, key: string) => {
      const next = [...safeSlots];
      const existing = next.indexOf(key);
      if (existing === idx) return;
      const displaced = next[idx];
      if (existing >= 0 && displaced !== undefined) {
        next[existing] = displaced;
      }
      next[idx] = key;
      setFavorites(kind, next);
    },
    [safeSlots, kind, setFavorites],
  );

  const startDrag = (e: React.DragEvent, key: string) => {
    e.dataTransfer.setData(dragMime, key);
    e.dataTransfer.effectAllowed = 'move';
    setDragKey(key);
  };
  const endDrag = () => {
    setDragKey(null);
    setDragOverFav(null);
  };
  const onFavDragOver = (idx: number) => (e: React.DragEvent) => {
    if (!e.dataTransfer.types.includes(dragMime)) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = 'move';
    if (dragOverFav !== idx) setDragOverFav(idx);
  };
  const onFavDrop = (idx: number) => (e: React.DragEvent) => {
    const key = e.dataTransfer.getData(dragMime);
    if (!key) return;
    e.preventDefault();
    dropOnFav(idx, key);
    setDragOverFav(null);
    setDragKey(null);
  };

  return (
    <div
      ref={containerRef}
      className="ctrl-group toolbar-fav"
      style={{ minWidth, position: 'relative' }}
    >
      <div className="label-xs ctrl-lbl">{label}</div>
      <div className="btn-row" style={{ gap: 3 }}>
        {safeSlots.map((slotKey, idx) => {
          const opt = options.find((o) => o.key === slotKey);
          const active = opt && currentKey === opt.key;
          return (
            <button
              key={`fav-${idx}`}
              type="button"
              onClick={() => opt && onSelect(opt.key)}
              onDragOver={onFavDragOver(idx)}
              onDragLeave={() => setDragOverFav(null)}
              onDrop={onFavDrop(idx)}
              className={`btn sm toolbar-fav__slot ${active ? 'active' : ''} ${dragOverFav === idx ? 'is-drop-target' : ''}`}
              title={opt ? (opt.title ?? `${opt.label} — drag a different option here to replace`) : 'Empty slot — drop an option here'}
              aria-label={`Favorite ${idx + 1}: ${opt ? opt.label : slotKey}`}
            >
              {opt ? opt.label : slotKey}
            </button>
          );
        })}
        <button
          ref={toggleRef}
          type="button"
          onClick={() => setOpen((v) => !v)}
          className={`btn sm ${open ? 'active' : ''}`}
          title={`All ${label.toLowerCase()} options — drag onto a favorite to pin`}
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
          aria-label={`${label} options`}
          style={{ top: popoverPos.top, left: popoverPos.left }}
        >
          <div className="toolbar-fav__hint">DRAG ONTO A FAVORITE TO PIN</div>
          <div className="toolbar-fav__grid">
            {options.map((opt) => {
              const isFav = safeSlots.includes(opt.key);
              const isActive = currentKey === opt.key;
              return (
                <button
                  key={opt.key}
                  type="button"
                  draggable
                  onDragStart={(e) => startDrag(e, opt.key)}
                  onDragEnd={endDrag}
                  onClick={() => {
                    onSelect(opt.key);
                    setOpen(false);
                  }}
                  title={opt.title ?? `${opt.label} — drag onto a favorite slot to pin`}
                  className={`toolbar-fav__chip ${isActive ? 'is-active' : ''} ${isFav ? 'is-pinned' : ''} ${dragKey === opt.key ? 'is-dragging' : ''}`}
                >
                  {opt.label}
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
