// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// FlexWorkspace — react-grid-layout (RGL) substrate for the desktop
// workspace. Replaces the flexlayout-react implementation that lived here
// before. The export name `FlexWorkspace` is preserved so App.tsx import
// paths don't churn; a follow-up rename can land separately.
//
// Layout semantics:
//   - 24-column grid, WORKSPACE_ROW_HEIGHT_PX rows (see workspace.ts).
//   - Tiles persist via the layout-store (debounced PUT to /api/ui/layout).
//   - Drag handle is the small grip in each tile's chrome header — clicks
//     inside the panel body do not initiate a drag (RGL's dragConfig.handle
//     is scoped to .workspace-tile-drag-handle).
//   - "+ Add Panel" is a small workspace-level button at the bottom-right,
//     opening the categorized AddPanelModal.

import {
  memo,
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type PointerEvent as ReactPointerEvent,
} from 'react';
import {
  ResponsiveGridLayout,
  useContainerWidth,
  type Layout,
  type LayoutItem,
} from 'react-grid-layout';
import { absoluteStrategy } from 'react-grid-layout/core';
import { Plus, Puzzle, Settings } from 'lucide-react';
import { useWorkspace } from './WorkspaceContext';
import { parseLayoutOrDefault, useLayoutStore } from '../state/layout-store';
import { getPanelDef } from './panels';
import {
  WORKSPACE_DRAG_COMPACTOR,
  WORKSPACE_RESIZE_COMPACTOR,
  autoFitDroppedPanel,
} from './workspaceGrid';
import { usePluginPanels } from '../plugins/runtime/usePluginPanels';
import {
  EMPTY_WORKSPACE_LAYOUT,
  WORKSPACE_GRID_COLS,
  WORKSPACE_ROW_HEIGHT_PX,
  WORKSPACE_TARGET_ROWS,
  WORKSPACE_TILE_MIN_H,
  WORKSPACE_TILE_MIN_W,
  type WorkspaceTile,
} from './workspace';
import { AddPanelModal } from './AddPanelModal';
import { TileChrome } from './TileChrome';
import { ConfirmDialog } from './ConfirmDialog';
import { TerminatorLines } from '../components/design/TerminatorLines';
import { MeterGroupPanel } from '../components/meter-group/MeterGroupPanel';
import {
  parseMeterGroupConfig,
  type MeterGroupConfig,
} from '../components/meter-group/meterGroupConfig';
import { HeroPanel } from './panels/HeroPanel';
import { UrlEmbedPanel } from './panels/UrlEmbedPanel';
import {
  parseUrlEmbedConfig,
  type UrlEmbedConfig,
} from './panels/urlEmbedConfig';

const WORKSPACE_GRID_MARGIN_PX = 3;
const WORKSPACE_ROW_GAP_SHARE = 6;

type GridInteraction = 'drag' | 'resize' | null;

interface FlexWorkspaceProps {
  /** Omitted = current dock-selected layout; set = fixed detached workspace. */
  layoutId?: string;
  showAddPanelModal?: boolean;
}

export function FlexWorkspace({
  layoutId,
  showAddPanelModal = true,
}: FlexWorkspaceProps = {}) {
  const { terminatorActive } = useWorkspace();
  // Loading is driven by App.tsx via loadForRadio(boardKey) — no local
  // first-load effect here. The dock-selected layout uses `workspace`; a
  // detached window parses its fixed layout id directly from the layouts list.
  const activeLayoutId = useLayoutStore((s) => s.activeLayoutId);
  const activeWorkspace = useLayoutStore((s) => s.workspace);
  const targetLayoutJson = useLayoutStore((s) =>
    layoutId ? s.layouts.find((l) => l.id === layoutId)?.layoutJson : undefined,
  );
  const targetLayoutId = layoutId ?? activeLayoutId;
  const workspace = useMemo(() => {
    if (!layoutId) return activeWorkspace;
    return targetLayoutJson
      ? parseLayoutOrDefault(targetLayoutJson)
      : EMPTY_WORKSPACE_LAYOUT;
  }, [activeWorkspace, layoutId, targetLayoutJson]);
  const isLoaded = useLayoutStore((s) => s.isLoaded);
  const syncToServerBeforeUnload = useLayoutStore((s) => s.syncToServerBeforeUnload);
  const addTileToLayout = useLayoutStore((s) => s.addTileToLayout);
  const removeTileFromLayout = useLayoutStore((s) => s.removeTileFromLayout);
  const updateTilePlacementInLayout = useLayoutStore(
    (s) => s.updateTilePlacementInLayout,
  );
  // Modal visibility lifted into the store so the trigger button can live
  // in the LeftLayoutBar — the workspace just renders the modal when the
  // store says open.
  const addPanelOpen = useLayoutStore((s) => s.addPanelOpen);
  const setAddPanelOpen = useLayoutStore((s) => s.setAddPanelOpen);
  const [pendingRemoveTile, setPendingRemoveTile] = useState<{
    uid: string;
    title: string;
  } | null>(null);

  // Best-effort persist on page-unload (sendBeacon → fetch keepalive fallback).
  useEffect(() => {
    const handler = () => syncToServerBeforeUnload();
    window.addEventListener('beforeunload', handler);
    return () => window.removeEventListener('beforeunload', handler);
  }, [syncToServerBeforeUnload]);

  const existingPanels = useMemo(
    () => new Set(workspace.tiles.map((t) => t.panelId)),
    [workspace.tiles],
  );

  const onLayoutChange = useCallback(
    (next: Layout) => {
      // RGL fires onLayoutChange on every render with the current layout
      // (including the very first paint). Diff each item against the store
      // and only PUT through when something actually moved.
      for (const item of next) {
        updateTilePlacementInLayout(targetLayoutId, item.i, {
          x: item.x,
          y: item.y,
          w: item.w,
          h: item.h,
        });
      }
    },
    [targetLayoutId, updateTilePlacementInLayout],
  );

  const onAddPanel = useCallback(
    (panelId: string) => {
      addTileToLayout(targetLayoutId, panelId);
    },
    [addTileToLayout, targetLayoutId],
  );

  // Brief loading state while the server fetch resolves. We render the
  // empty container so it has measurable width when the tiles arrive.
  return (
    <div className={`flex-workspace ${terminatorActive ? 'terminator' : ''}`}>
      <WorkspaceCanvas
        tiles={workspace.tiles}
        isLoaded={isLoaded}
        layoutId={targetLayoutId}
        onLayoutChange={onLayoutChange}
        onRequestRemoveTile={(uid, title) => setPendingRemoveTile({ uid, title })}
      />
      <TerminatorLines active={terminatorActive} />
      {showAddPanelModal && !addPanelOpen && (
        <button
          type="button"
          className="workspace-add-panel-btn"
          onClick={() => setAddPanelOpen(true)}
          disabled={!isLoaded}
          title="Add a panel to this workspace"
          aria-label="Add panel"
        >
          <Plus size={18} strokeWidth={2.2} aria-hidden />
        </button>
      )}
      {showAddPanelModal && addPanelOpen && (
        <AddPanelModal
          existingPanels={existingPanels}
          onAdd={onAddPanel}
          onClose={() => setAddPanelOpen(false)}
        />
      )}
      {pendingRemoveTile && (
        <ConfirmDialog
          title="Remove panel"
          confirmLabel="Remove Panel"
          onCancel={() => setPendingRemoveTile(null)}
          onConfirm={() => {
            removeTileFromLayout(targetLayoutId, pendingRemoveTile.uid);
            setPendingRemoveTile(null);
          }}
        >
          <p>
            Remove {pendingRemoveTile.title} from the active layout?
          </p>
          <p>
            The panel can be added back later from Add Panel.
          </p>
        </ConfirmDialog>
      )}
    </div>
  );
}

interface WorkspaceCanvasProps {
  tiles: WorkspaceTile[];
  isLoaded: boolean;
  layoutId: string;
  onLayoutChange: (next: Layout) => void;
  onRequestRemoveTile: (uid: string, title: string) => void;
}

function WorkspaceCanvas({
  tiles,
  isLoaded,
  layoutId,
  onLayoutChange,
  onRequestRemoveTile,
}: WorkspaceCanvasProps) {
  // useContainerWidth from RGL's modern API: ResizeObserver-backed parent
  // measurement. mounted=false on first paint to avoid the 1280-px width
  // flash before the observer fires. Same pattern MetersCanvas uses.
  const { width, containerRef, mounted } = useContainerWidth();
  // Subscribe to plugin-registered panels so rglLayouts recomputes once
  // plugin modules load at startup (getPanelDef inside the useMemo would
  // otherwise return undefined and never re-resolve).
  const pluginPanels = usePluginPanels();
  const pluginPanelKey = pluginPanels.map((panel) => panel.panelId).join('\0');
  // Track container height so rowHeight can shrink when the workspace is
  // tight. Rows do not grow past the authored design density; extra viewport
  // height stays on the workspace ground instead of stretching every panel.
  const [containerHeight, setContainerHeight] = useState(0);
  const [gridInteraction, setGridInteraction] =
    useState<GridInteraction>(null);
  const autoFitDropRef = useRef<LayoutItem | null>(null);
  useEffect(() => {
    const el = containerRef.current;
    if (!el) return;
    setContainerHeight(el.getBoundingClientRect().height);
    const ro = new ResizeObserver((entries) => {
      for (const e of entries) setContainerHeight(e.contentRect.height);
    });
    ro.observe(el);
    return () => ro.disconnect();
  }, [containerRef]);
  // The actual vertical extent of the current layout, in grid rows. The
  // default layout is authored to be exactly WORKSPACE_TARGET_ROWS tall, but
  // the operator adds/rearranges panels freely — once the layout is taller
  // than the target, dividing the viewport by a fixed target would render the
  // grid taller than the container and force an outer scrollbar. Tracking the
  // real extent keeps the workspace fitting the viewport no matter how many
  // panels are docked.
  const layoutRows = useMemo(
    () => tiles.reduce((max, t) => Math.max(max, t.y + t.h), 0),
    [tiles],
  );
  // Fit-to-viewport divisor: never smaller than the default target (so a
  // sparse layout doesn't balloon its tiles to fill the screen — it just
  // leaves empty space at the bottom, matching the prior behaviour), and
  // grows to the layout's real height when the operator stacks panels past
  // the default fold. Dense layouts shrink rather than asking the workspace
  // shell for a scrollbar.
  const targetRows = Math.max(layoutRows, WORKSPACE_TARGET_ROWS);

  // Shrink-to-fit rowHeight: solve containerHeight ≈ rowHeight*N + margin*(N-1)
  // + 2*containerPadding, but cap at WORKSPACE_ROW_HEIGHT_PX so resizing a
  // window taller doesn't inject empty vertical space inside fixed-control
  // panels. Margin and containerPadding here mirror the props passed to
  // ResponsiveGridLayout below — keep them in sync if those change. The row
  // gap shrinks with very dense layouts so gaps alone can never make the grid
  // taller than the viewport.
  const rowMargin = useMemo(() => {
    if (containerHeight <= 0 || targetRows <= 1) return WORKSPACE_GRID_MARGIN_PX;
    return Math.min(
      WORKSPACE_GRID_MARGIN_PX,
      containerHeight / (targetRows * WORKSPACE_ROW_GAP_SHARE),
    );
  }, [containerHeight, targetRows]);

  const rowHeight = useMemo(() => {
    if (containerHeight <= 0) return WORKSPACE_ROW_HEIGHT_PX;
    const containerPadding = 0;
    const inner =
      containerHeight - 2 * containerPadding - rowMargin * Math.max(0, targetRows - 1);
    return Math.min(
      WORKSPACE_ROW_HEIGHT_PX,
      Math.max(0.1, inner / Math.max(1, targetRows)),
    );
  }, [containerHeight, rowMargin, targetRows]);

  const workspaceCompactor =
    gridInteraction === 'resize'
      ? WORKSPACE_RESIZE_COMPACTOR
      : WORKSPACE_DRAG_COMPACTOR;

  const onPointerDownCapture = useCallback(
    (event: ReactPointerEvent<HTMLDivElement>) => {
      const target = event.target;
      if (!(target instanceof Element)) return;

      if (target.closest('.react-resizable-handle')) {
        setGridInteraction('resize');
        return;
      }

      if (
        target.closest('.workspace-tile-header') &&
        !target.closest('.workspace-tile-close')
      ) {
        setGridInteraction('drag');
      }
    },
    [],
  );

  const onDragStart = useCallback(() => setGridInteraction('drag'), []);
  const onResizeStart = useCallback(() => setGridInteraction('resize'), []);
  const onDragStop = useCallback((
    _layout: Layout,
    oldItem: LayoutItem | null,
    newItem: LayoutItem | null,
  ) => {
    autoFitDropRef.current = (
      oldItem &&
      newItem &&
      (oldItem.x !== newItem.x || oldItem.y !== newItem.y)
    )
      ? { ...oldItem }
      : null;
    setGridInteraction(null);
  }, []);
  const onResizeStop = useCallback(() => {
    autoFitDropRef.current = null;
    setGridInteraction(null);
  }, []);
  const handleLayoutChange = useCallback(
    (next: Layout) => {
      const previousDropItem = autoFitDropRef.current;
      autoFitDropRef.current = null;
      onLayoutChange(
        previousDropItem
          ? autoFitDroppedPanel(next, WORKSPACE_GRID_COLS, previousDropItem)
          : next,
      );
    },
    [onLayoutChange],
  );

  // RGL needs a stable per-render layouts.lg array. Memoise against the
  // tile list identity so we don't push a new prop on every parent render.
  // Per-panel maxW/maxH (when defined in panels.ts) is propagated here so
  // RGL clamps user resize drags.
  const rglLayouts = useMemo(
    () => ({
      lg: tiles.map((t) => {
        const def = getPanelDef(t.panelId, pluginPanelKey);
        // Clamp persisted size to the panel's maxW/maxH. RGL only enforces
        // these caps during resize drags, so a tile saved wider than its cap
        // (e.g. a Filter Presets tile placed before it gained maxW) would
        // otherwise keep sprawling on load. Clamping here snaps it back into
        // the right-column stack; the next onLayoutChange persists the fix.
        const w = def?.maxW !== undefined ? Math.min(t.w, def.maxW) : t.w;
        const h = def?.maxH !== undefined ? Math.min(t.h, def.maxH) : t.h;
        return {
          i: t.uid,
          x: t.x,
          y: t.y,
          w,
          h,
          // Per-panel legibility floor when the panel declares one, else the
          // workspace-global minimum. RGL clamps drag-resize to these and
          // refuses to compact a tile below them, so the viewport auto-fit
          // can shrink the grid only until a dense panel hits its floor.
          minW: def?.minW ?? WORKSPACE_TILE_MIN_W,
          minH: def?.minH ?? WORKSPACE_TILE_MIN_H,
          ...(def?.maxW !== undefined ? { maxW: def.maxW } : {}),
          ...(def?.maxH !== undefined ? { maxH: def.maxH } : {}),
        };
      }),
    }),
    [tiles, pluginPanelKey],
  );

  return (
    <div
      ref={containerRef}
      className="all-panels-workspace"
      onPointerDownCapture={onPointerDownCapture}
    >
      {!isLoaded || !mounted ? (
        // Reserve space silently while server load + ResizeObserver settle.
        <div style={{ minHeight: 80 }} aria-hidden />
      ) : (
        <ResponsiveGridLayout
          className="all-panels-grid"
          width={width}
          breakpoints={{ lg: 0 }}
          cols={{ lg: WORKSPACE_GRID_COLS }}
          rowHeight={rowHeight}
          margin={[WORKSPACE_GRID_MARGIN_PX, rowMargin]}
          containerPadding={[0, 0]}
          compactor={workspaceCompactor}
          // Position tiles via top/left rather than transform: translate3d.
          // RGL's default `transformStrategy` uses CSS transforms, which
          // (combined with the upstream stylesheet's `will-change: transform`)
          // forces every tile to be its own permanent GPU compositor layer.
          // With a WebGL panadapter inside one of those tiles, every WebGL
          // frame produces a chain re-composite up the tree — visible on
          // macOS as WindowServer + Chrome GPU pinning. `absoluteStrategy`
          // avoids the promotion for static tiles. The only cost is slightly
          // less smooth dragging, and operators rarely re-arrange the
          // workspace.
          positionStrategy={absoluteStrategy}
          // Drag from anywhere in the tile header (the grip + title strip),
          // EXCEPT the close button. A tiny grip-only handle is too small to
          // grab — and panels that have their own pointer logic in the body
          // (panadapter canvas's pan/tune gesture, sliders) also need a
          // generous header target so the operator can reposition the tile
          // without getting their input stolen by the body. dragConfig.cancel
          // excludes the X so close clicks still register.
          dragConfig={{
            handle: '.workspace-tile-header',
            cancel: '.workspace-tile-close',
            bounded: false,
          }}
          onDragStart={onDragStart}
          onDragStop={onDragStop}
          onResizeStart={onResizeStart}
          onResizeStop={onResizeStop}
          onLayoutChange={handleLayoutChange}
          layouts={rglLayouts}
        >
          {tiles.map((tile) => (
            <div key={tile.uid} data-tile-uid={tile.uid}>
              <PanelTile
                tile={tile}
                layoutId={layoutId}
                onRequestRemoveTile={onRequestRemoveTile}
              />
            </div>
          ))}
        </ResponsiveGridLayout>
      )}
    </div>
  );
}

interface PanelTileProps {
  tile: WorkspaceTile;
  layoutId: string;
  onRequestRemoveTile: (uid: string, title: string) => void;
}

// Memoised so a parent re-render (e.g. another tile's drag updating the
// store) doesn't reconcile every panel's subtree. Effective only because
// the store preserves per-tile object identity across unrelated mutations
// and `onRemoveTile` is the stable zustand action reference.
const PanelTile = memo(function PanelTile({
  tile,
  layoutId,
  onRequestRemoveTile,
}: PanelTileProps) {
  const def = getPanelDef(tile.panelId);
  if (!def) {
    return (
      <UnavailablePanelTile
        tile={tile}
        layoutId={layoutId}
        onRequestRemoveTile={onRequestRemoveTile}
      />
    );
  }
  const handleRemove = () => onRequestRemoveTile(tile.uid, def.name);
  // Headerless panels own their entire tile surface and draw their own
  // header (if any). They MUST include an element with class
  // `.workspace-tile-header` so RGL drag picks up, and a
  // `.workspace-tile-close` button bound to the injected onRemove.
  if (def.headerless) {
    return (
      <div
        className="workspace-tile workspace-tile--headerless"
        data-panel-id={tile.panelId}
      >
        <PanelBody tile={tile} layoutId={layoutId} onRemove={handleRemove} />
      </div>
    );
  }
  return (
    <div className="workspace-tile" data-panel-id={tile.panelId}>
      <TileChrome title={def.name} onRemove={handleRemove} />
      <div className="workspace-tile-body">
        <PanelBody tile={tile} layoutId={layoutId} />
      </div>
    </div>
  );
});

function PanelBody({
  tile,
  layoutId,
  onRemove,
}: {
  tile: WorkspaceTile;
  layoutId: string;
  onRemove?: () => void;
}) {
  // Per-tile config-bound rendering for multi-instance / configurable
  // panels. Single-instance panels just render their component as-is.
  if (tile.panelId === 'hero') {
    return <HeroPanel tile={tile} layoutId={layoutId} onRemove={onRemove} />;
  }
  if (tile.panelId === 'metergroup') {
    return <MeterGroupTileBody tile={tile} layoutId={layoutId} onRemove={onRemove} />;
  }
  if (tile.panelId === 'urlembed') {
    return <UrlEmbedTileBody tile={tile} layoutId={layoutId} onRemove={onRemove} />;
  }
  const def = getPanelDef(tile.panelId);
  if (!def) return null;
  const Component = def.component;
  // Headerless single-instance panels that own their own header receive
  // onRemove so their close button can drop the tile (matches the meter
  // group special-case above without pulling in its per-tile config).
  if (def.headerless && onRemove) {
    return <Component onRemove={onRemove} />;
  }
  return <Component />;
}

function UnavailablePanelTile({
  tile,
  onRequestRemoveTile,
}: PanelTileProps) {
  const handleRemove = useCallback(
    () => onRequestRemoveTile(tile.uid, 'Unavailable panel'),
    [onRequestRemoveTile, tile.uid],
  );
  const openPlugins = useCallback(() => {
    useLayoutStore.getState().setSettingsView(true, 'plugins');
  }, []);

  return (
    <div className="workspace-tile workspace-tile--unavailable">
      <TileChrome title="Unavailable panel" onRemove={handleRemove} />
      <div className="workspace-tile-body workspace-unavailable-panel">
        <div className="workspace-unavailable-panel-icon" aria-hidden>
          <Puzzle size={18} />
        </div>
        <div className="workspace-unavailable-panel-copy">
          <div className="workspace-unavailable-panel-title">
            Panel unavailable
          </div>
          <p>
            Zeus preserved this saved tile, but its panel is not registered.
            Install or enable the plugin, or remove the tile.
          </p>
          <code>{tile.panelId}</code>
        </div>
        <div className="workspace-unavailable-panel-actions">
          <button type="button" className="btn sm" onClick={openPlugins}>
            <Settings size={12} aria-hidden />
            Plugins
          </button>
          <button type="button" className="btn ghost sm" onClick={handleRemove}>
            Remove
          </button>
        </div>
      </div>
    </div>
  );
}

function MeterGroupTileBody({
  tile,
  layoutId,
  onRemove,
}: {
  tile: WorkspaceTile;
  layoutId: string;
  onRemove?: () => void;
}) {
  const updateTileInstanceConfig = useLayoutStore(
    (s) => s.updateTileInstanceConfigInLayout,
  );
  const updateTilePlacement = useLayoutStore(
    (s) => s.updateTilePlacementInLayout,
  );
  const config: MeterGroupConfig = useMemo(
    () => parseMeterGroupConfig(tile.instanceConfig),
    [tile.instanceConfig],
  );
  const setConfig = useCallback(
    (next: MeterGroupConfig) => {
      updateTileInstanceConfig(layoutId, tile.uid, next);
    },
    [layoutId, tile.uid, updateTileInstanceConfig],
  );

  // Auto-fit the tile to its widget set. Operators add a Meter Group, drop
  // in two vertical bars, and expect the tile to snap to bar-width — not
  // to leave four grid columns of empty space waiting to be filled. The
  // effect deps are widget count + direction only (live tile geometry is
  // read through a ref) so an operator can still drag-resize the cross
  // axis without the effect snapping it back on every render.
  const tileRef = useRef(tile);
  tileRef.current = tile;
  useEffect(() => {
    const t = tileRef.current;
    const widgetCount = Math.max(1, config.widgets.length);
    // Row: one grid col per widget on the main axis. Column: ~3 grid rows
    // per widget so vertical bars get enough vertical span to read.
    const targetW = config.direction === 'row' ? widgetCount : t.w;
    const targetH =
      config.direction === 'column' ? Math.max(3, widgetCount * 3) : t.h;
    if (targetW !== t.w || targetH !== t.h) {
      updateTilePlacement(layoutId, t.uid, {
        x: t.x,
        y: t.y,
        w: targetW,
        h: targetH,
      });
    }
  }, [config.widgets.length, config.direction, layoutId, updateTilePlacement]);

  return (
    <MeterGroupPanel config={config} setConfig={setConfig} onRemove={onRemove} />
  );
}

function UrlEmbedTileBody({
  tile,
  layoutId,
  onRemove,
}: {
  tile: WorkspaceTile;
  layoutId: string;
  onRemove?: () => void;
}) {
  const updateTileInstanceConfig = useLayoutStore(
    (s) => s.updateTileInstanceConfigInLayout,
  );
  const config: UrlEmbedConfig = useMemo(
    () => parseUrlEmbedConfig(tile.instanceConfig),
    [tile.instanceConfig],
  );
  const setConfig = useCallback(
    (next: UrlEmbedConfig) => {
      updateTileInstanceConfig(layoutId, tile.uid, next);
    },
    [layoutId, tile.uid, updateTileInstanceConfig],
  );

  return (
    <UrlEmbedPanel config={config} setConfig={setConfig} onRemove={onRemove} />
  );
}
