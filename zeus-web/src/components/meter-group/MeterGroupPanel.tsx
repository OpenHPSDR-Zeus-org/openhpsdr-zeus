// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// "Meter Group" — a top-level workspace panel holding a single row OR
// column of meters that share the available width / height. Replaces the
// older nested-groups MetersPanel: each former group becomes its own
// top-level tile, the operator drags it around the workspace via the
// usual tile chrome, and the panel's body is just a flex container of
// meter widgets.
//
// Header layout (left → right):
//   [grip] [title] [pencil-rename] | [direction toggle] [+ meter] [×]
//
// Body: flex container in `direction` (row or column). Each widget is
// rendered bare via `MeterRenderer` and grows via `flex: 1` so N widgets
// share the available width / height equally.

import { useCallback, useState, type CSSProperties } from 'react';
import {
  GripVertical,
  Plus,
  ArrowRightLeft,
  ArrowDownUp,
  Pencil,
  X,
  Trash2,
} from 'lucide-react';
import {
  effectiveKind,
  EMPTY_METER_GROUP_CONFIG,
  newWidgetUid,
  type MeterGroupConfig,
  type MeterGroupWidget,
  type MeterGroupWidgetSettings,
} from './meterGroupConfig';
import { MeterRenderer } from './MeterRenderer';
import { AddMeterModal } from './AddMeterModal';
import {
  MeterReadingId,
  type MeterDefaultKind,
} from '../meters/meterCatalog';

// Per-kind intrinsic main-axis size (px). The panel body sizes each
// widget to its own natural footprint and `justify-content: flex-start`
// packs them to the leading edge — no stretch, no centering. That keeps
// a single vertical VU bar at bar-width even when the tile is wider, and
// makes additional bars line up to the left rather than spreading to
// fill the row.
const KIND_INTRINSIC_PX: Record<MeterDefaultKind, number> = {
  vucolumn: 70,   // 60-unit viewBox + lamp-card padding
  bigarc: 200,    // 240×150 viewBox arc, modest display size
  pulldown: 230,  // 280×150 viewBox arc
  hbar: 240,      // wide horizontal bar reads better with room
};

interface MeterGroupPanelProps {
  /** Per-instance config blob from the workspace store. */
  config?: MeterGroupConfig;
  /** Setter wired by the workspace store. */
  setConfig?: (next: MeterGroupConfig) => void;
  /** Tile-removal hook. Headerless panels own their close X. */
  onRemove?: () => void;
}

export function MeterGroupPanel({
  config = EMPTY_METER_GROUP_CONFIG,
  setConfig,
  onRemove,
}: MeterGroupPanelProps) {
  const [libraryOpen, setLibraryOpen] = useState(false);
  const [editingTitle, setEditingTitle] = useState(false);
  const [titleDraft, setTitleDraft] = useState(config.title);
  const [hoveredUid, setHoveredUid] = useState<string | null>(null);
  // Whole-panel hover tracks whether to fade the toolbar buttons in.
  // Mirrors the immersive Section card aesthetic: clean header at rest,
  // affordances reveal on intent.
  const [panelHover, setPanelHover] = useState(false);

  const commit = useCallback(
    (next: MeterGroupConfig) => {
      if (setConfig) setConfig(next);
    },
    [setConfig],
  );

  const setDirection = useCallback(
    (direction: 'row' | 'column') => {
      commit({ ...config, direction });
    },
    [commit, config],
  );

  const setTitle = useCallback(
    (next: string) => {
      const trimmed = next.trim() || 'Meters';
      commit({ ...config, title: trimmed });
    },
    [commit, config],
  );

  const addWidget = useCallback(
    (
      reading: MeterReadingId,
      kind: MeterDefaultKind,
      settings: MeterGroupWidgetSettings,
    ) => {
      const widget: MeterGroupWidget = {
        uid: newWidgetUid(),
        reading,
        kind,
        ...(Object.keys(settings).length > 0 ? { settings } : {}),
      };
      commit({ ...config, widgets: [...config.widgets, widget] });
    },
    [commit, config],
  );

  const removeWidget = useCallback(
    (uid: string) => {
      commit({ ...config, widgets: config.widgets.filter((w) => w.uid !== uid) });
    },
    [commit, config],
  );

  const stopDrag = (e: React.MouseEvent) => e.stopPropagation();

  // ── header styles ─────────────────────────────────────────────────
  // Title styled to mirror the immersive Section.sec-hd label: small caps,
  // wide letter-spacing, fg-2 muted colour. The LED dot before the title
  // sits at fg-3 grey to match the idle "neutral" Section header look.
  const titleStyle: CSSProperties = {
    flex: 1,
    fontSize: 9.5,
    fontFamily: 'var(--font-sans)',
    textTransform: 'uppercase',
    letterSpacing: '0.20em',
    color: 'var(--fg-2)',
    fontWeight: 700,
    cursor: 'text',
    userSelect: 'none',
    minWidth: 0,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  };
  // Toolbar buttons at low alpha so they don't fight the title at rest;
  // panel-hover bumps them to full so the operator can grab them when
  // they actually want to interact.
  const headerBtnStyle: CSSProperties = {
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: 18,
    height: 18,
    borderRadius: 'var(--r-xs)',
    color: 'var(--fg-2)',
    background: 'transparent',
    border: 'none',
    cursor: 'pointer',
    opacity: panelHover ? 0.85 : 0.25,
    transition: 'opacity var(--dur-fast), color var(--dur-fast)',
  };
  const pencilStyle: CSSProperties = {
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: 16,
    height: 16,
    color: 'var(--fg-3)',
    opacity: panelHover ? 0.7 : 0,
    cursor: 'pointer',
    transition: 'opacity var(--dur-fast)',
  };
  const overlayBtnStyle: CSSProperties = {
    position: 'absolute',
    top: 4,
    right: 4,
    width: 18,
    height: 18,
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    borderRadius: 'var(--r-xs)',
    color: 'var(--fg-2)',
    background: 'rgba(10, 11, 14, 0.65)',
    border: '1px solid rgba(255,255,255,0.06)',
    cursor: 'pointer',
    transition: 'opacity var(--dur-fast), color var(--dur-fast)',
    zIndex: 2,
  };

  return (
    <div
      data-testid="meter-group-panel"
      onMouseEnter={() => setPanelHover(true)}
      onMouseLeave={() => setPanelHover(false)}
      style={{
        position: 'relative',
        display: 'flex',
        flexDirection: 'column',
        height: '100%',
        overflow: 'hidden',
        // Match the immersive TX Stage Meters Section card recipe so a
        // top-level Meter Group reads as the same kind of "lit section
        // on a dark bench" as the Final Output / Signal Chain / Gain
        // Reduction sections inside the immersive panel.
        background:
          'linear-gradient(180deg, var(--immersive-panel-2) 0%, var(--immersive-well) 100%)',
        border: '1px solid var(--immersive-line)',
        borderRadius: 8,
        boxShadow:
          'inset 0 1px 0 var(--immersive-rim), inset 0 0 30px rgba(0,0,0,0.25)',
      }}
    >
      {/* ── header — workspace-tile-header so RGL drag works ─────────
          Inline styles override the default `.workspace-tile-header` CSS
          from all-panels.css (which paints the heavy gradient bar +
          border-bottom that made the meter group look "tiled"). The
          immersive Section.sec-hd is borderless and blends into the
          body — that's what we mirror here. */}
      <div
        className="workspace-tile-header"
        style={{
          background: 'transparent',
          borderBottom: 'none',
          padding: '4px 14px',
          height: 30,
          gap: 9,
        }}
      >
        <span
          className="workspace-tile-drag-handle"
          aria-hidden="true"
          title="Drag to reposition"
          style={{ opacity: panelHover ? 0.6 : 0.2, transition: 'opacity var(--dur-fast)' }}
        >
          <GripVertical size={12} />
        </span>
        {/* LED dot — same idle-grey recipe as ImmersiveMetersPanel.Section. */}
        <span
          aria-hidden="true"
          style={{
            width: 5,
            height: 5,
            borderRadius: '50%',
            background: 'var(--fg-3)',
            flexShrink: 0,
          }}
        />

        {editingTitle ? (
          <input
            type="text"
            autoFocus
            value={titleDraft}
            onChange={(e) => setTitleDraft(e.target.value)}
            onBlur={() => {
              setEditingTitle(false);
              setTitle(titleDraft);
            }}
            onMouseDown={stopDrag}
            onKeyDown={(e) => {
              if (e.key === 'Enter') {
                setEditingTitle(false);
                setTitle(titleDraft);
              } else if (e.key === 'Escape') {
                setEditingTitle(false);
                setTitleDraft(config.title);
              }
            }}
            style={{
              ...titleStyle,
              background: 'var(--bg-2)',
              border: '1px solid var(--accent)',
              borderRadius: 'var(--r-xs)',
              padding: '0 4px',
              outline: 'none',
            }}
          />
        ) : (
          <>
            <span
              className="workspace-tile-title"
              role="button"
              tabIndex={0}
              title="Click to rename"
              onClick={(e) => {
                e.stopPropagation();
                setTitleDraft(config.title);
                setEditingTitle(true);
              }}
              onDoubleClick={(e) => {
                e.stopPropagation();
                setTitleDraft(config.title);
                setEditingTitle(true);
              }}
              onMouseDown={stopDrag}
              style={titleStyle}
            >
              {config.title}
            </span>
            <span
              role="button"
              tabIndex={0}
              aria-label="Rename"
              title="Click to rename"
              onClick={(e) => {
                e.stopPropagation();
                setTitleDraft(config.title);
                setEditingTitle(true);
              }}
              onMouseDown={stopDrag}
              style={pencilStyle}
              onMouseEnter={(e) => (e.currentTarget.style.opacity = '1')}
              onMouseLeave={(e) => (e.currentTarget.style.opacity = '0.5')}
            >
              <Pencil size={11} />
            </span>
          </>
        )}

        {/* Direction toggle: row → column → row. Single button cycles. */}
        <button
          type="button"
          aria-label={`Layout: ${config.direction}. Click to switch.`}
          title={`Layout direction: ${config.direction}. Click to flip.`}
          onClick={() => setDirection(config.direction === 'row' ? 'column' : 'row')}
          onMouseDown={stopDrag}
          style={headerBtnStyle}
          data-testid="meter-group-direction"
        >
          {config.direction === 'row' ? (
            <ArrowRightLeft size={13} />
          ) : (
            <ArrowDownUp size={13} />
          )}
        </button>

        {/* Add meter — opens the library drawer. */}
        <button
          type="button"
          aria-label={libraryOpen ? 'Close library' : 'Add meter'}
          aria-pressed={libraryOpen}
          title={libraryOpen ? 'Close library' : 'Add meter'}
          onClick={() => setLibraryOpen((o) => !o)}
          onMouseDown={stopDrag}
          style={headerBtnStyle}
          data-testid="meter-group-add-meter"
        >
          <Plus size={14} />
        </button>

        {onRemove ? (
          <button
            type="button"
            className="workspace-tile-close"
            aria-label="Remove panel"
            title="Remove panel"
            onClick={(e) => {
              e.stopPropagation();
              onRemove();
            }}
            onPointerDown={(e) => e.stopPropagation()}
            onMouseDown={(e) => e.stopPropagation()}
          >
            <X size={12} />
          </button>
        ) : null}
      </div>

      {/* ── body — flex container, widgets share space ─────────────── */}
      <div
        style={{
          flex: '1 1 auto',
          minHeight: 0,
          overflow: 'auto',
          display: 'flex',
          flexDirection: config.direction,
          gap: 8,
          padding: 10,
          alignItems: 'stretch',
          justifyContent:
            config.widgets.length === 0 ? 'center' : 'flex-start',
        }}
      >
        {config.widgets.length === 0 ? (
          <div
            style={{
              color: 'var(--fg-3)',
              fontSize: 12,
              fontFamily: 'var(--font-sans)',
              fontStyle: 'italic',
              textAlign: 'center',
              padding: 24,
            }}
            data-testid="meter-group-empty-state"
          >
            Empty group — tap + to add a meter.
          </div>
        ) : (
          config.widgets.map((w) => {
            const intrinsic = KIND_INTRINSIC_PX[effectiveKind(w)];
            // Main axis = direction; cross axis stretches via the parent's
            // `alignItems: 'stretch'`. `flex: 0 0 <px>` blocks both grow
            // and shrink so each widget always reads at its natural size.
            const sizeAxis =
              config.direction === 'row'
                ? { width: intrinsic, flex: `0 0 ${intrinsic}px` as const }
                : { height: intrinsic, flex: `0 0 ${intrinsic}px` as const };
            return (
            <div
              key={w.uid}
              data-widget-uid={w.uid}
              onMouseEnter={() => setHoveredUid(w.uid)}
              onMouseLeave={() =>
                setHoveredUid((cur) => (cur === w.uid ? null : cur))
              }
              style={{
                position: 'relative',
                ...sizeAxis,
                minWidth: 0,
                minHeight: 0,
                display: 'flex',
              }}
            >
              <MeterRenderer widget={w} />
              {/* Hover-reveal trash — keeps gauges clean at rest. */}
              <button
                type="button"
                aria-label="Remove meter"
                title="Remove meter"
                onClick={(e) => {
                  e.stopPropagation();
                  removeWidget(w.uid);
                }}
                onMouseDown={(e) => e.stopPropagation()}
                style={{
                  ...overlayBtnStyle,
                  opacity: hoveredUid === w.uid ? 1 : 0,
                  pointerEvents: hoveredUid === w.uid ? 'auto' : 'none',
                }}
                onMouseEnter={(e) => {
                  e.currentTarget.style.color = 'var(--tx)';
                }}
                onMouseLeave={(e) => {
                  e.currentTarget.style.color = 'var(--fg-2)';
                }}
              >
                <Trash2 size={11} />
              </button>
            </div>
            );
          })
        )}
      </div>

      {/* ── add-meter modal — popover, not a slide-in drawer, so the
            library doesn't fight a small/narrow Meter Group tile for
            screen real-estate. Modelled on the workspace's AddPanelModal
            for consistent UX. */}
      {libraryOpen ? (
        <AddMeterModal
          existingReadings={
            new Set(config.widgets.map((w) => w.reading as string))
          }
          onAdd={addWidget}
          onClose={() => setLibraryOpen(false)}
        />
      ) : null}
    </div>
  );
}
