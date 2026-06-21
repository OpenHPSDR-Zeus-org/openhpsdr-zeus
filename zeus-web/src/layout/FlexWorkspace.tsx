// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
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
  WORKSPACE_RESIZE_COMPACTOR,
  autoFitDroppedPanel,
  createWorkspaceDragCompactor,
  resolveResizeOverlaps,
  type WorkspaceDragStartSnapshot,
} from './workspaceGrid';
import {
  deriveWorkspaceLayout,
  reconcileReportedToStored,
  type DeriveTile,
} from './lockedWorkspaceLayout';
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
  const setTileLockedInLayout = useLayoutStore((s) => s.setTileLockedInLayout);
  const updateTilePlacementsInLayout = useLayoutStore(
    (s) => s.updateTilePlacementsInLayout,
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
  const workspaceLocked = workspace.locked === true;

  const onLayoutChange = useCallback(
    (next: Layout) => {
      if (workspaceLocked) return;
      // RGL fires onLayoutChange on every render with the current layout
      // (including the very first paint). Diff each item against the store
      // and only PUT through when something actually moved.
      updateTilePlacementsInLayout(
        targetLayoutId,
        next.map((item) => ({
          uid: item.i,
          x: item.x,
          y: item.y,
          w: item.w,
          h: item.h,
        })),
      );
    },
    [targetLayoutId, updateTilePlacementsInLayout, workspaceLocked],
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
        workspaceLocked={workspaceLocked}
        isLoaded={isLoaded}
        layoutId={targetLayoutId}
        onLayoutChange={onLayoutChange}
        onRequestRemoveTile={(uid, title) => setPendingRemoveTile({ uid, title })}
        onToggleTileLock={(uid, locked, lockedHeightPx) =>
          setTileLockedInLayout(targetLayoutId, uid, locked, lockedHeightPx)
        }
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
  workspaceLocked: boolean;
  isLoaded: boolean;
  layoutId: string;
  onLayoutChange: (next: Layout) => void;
  onRequestRemoveTile: (uid: string, title: string) => void;
  onToggleTileLock: (
    uid: string,
    locked: boolean,
    lockedHeightPx?: number,
  ) => void;
}

function WorkspaceCanvas({
  tiles,
  workspaceLocked,
  isLoaded,
  layoutId,
  onLayoutChange,
  onRequestRemoveTile,
  onToggleTileLock,
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
  // Track container height so the grid can be sized on first paint. Once the
  // grid metrics are frozen (below) this only feeds the initial capture.
  const [containerHeight, setContainerHeight] = useState(0);
  const [gridInteraction, setGridInteraction] =
    useState<GridInteraction>(null);
  const draggingRef = useRef(false);
  const skipPostDropLayoutChangeRef = useRef(false);
  const dragStartRef = useRef<WorkspaceDragStartSnapshot | null>(null);
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

  // Fixed-cell workspace. Capture the grid's pixel metrics ONCE — at the first
  // valid measurement — so a column and a row keep a CONSTANT pixel size and
  // panels never rescale when the window is resized. The column COUNT, however,
  // tracks the live window width: growing the window adds columns (more room to
  // arrange panels into) and shrinking removes them — down to the base 24, or to
  // whatever the current layout already occupies, so existing tiles are never
  // shoved. Vertically the page grows downward and the canvas scrolls
  // (all-panels.css). Net effect: panels stay a fixed size, but the usable area
  // always fills the window instead of being capped at the first size.
  const [frozen, setFrozen] = useState<{ width: number; height: number } | null>(
    null,
  );
  useEffect(() => {
    if (frozen) return;
    if (mounted && width > 0 && containerHeight > 0) {
      setFrozen({ width, height: containerHeight });
    }
  }, [frozen, mounted, width, containerHeight]);
  // Frozen height drives the (fixed) row height via deriveWorkspaceLayout.
  const gridHeight = frozen?.height ?? containerHeight;
  // Fixed column width, captured from the first layout at the base 24-column
  // count. RGL's column width = (containerWidth - margin*(cols-1)) / cols, so we
  // invert that to recover the per-column pixel pitch and then hold it constant.
  const baseColWidth =
    frozen && frozen.width > 0
      ? (frozen.width - WORKSPACE_GRID_MARGIN_PX * (WORKSPACE_GRID_COLS - 1)) /
        WORKSPACE_GRID_COLS
      : 0;
  // Never drop below the base count or below the rightmost tile already placed,
  // so a window-shrink never clamps an existing tile inward (RGL would otherwise
  // shove any tile whose x+w exceeds cols).
  const occupiedCols = useMemo(
    () => tiles.reduce((m, t) => Math.max(m, t.x + t.w), WORKSPACE_GRID_COLS),
    [tiles],
  );
  const cols =
    baseColWidth > 0
      ? Math.max(
          occupiedCols,
          Math.floor(
            (width + WORKSPACE_GRID_MARGIN_PX) /
              (baseColWidth + WORKSPACE_GRID_MARGIN_PX),
          ),
        )
      : occupiedCols;
  // Exact width for `cols` columns at the fixed pitch, so RGL's derived column
  // width is exactly baseColWidth (no sub-pixel rescale as the window resizes).
  const gridWidth =
    baseColWidth > 0
      ? cols * baseColWidth + (cols - 1) * WORKSPACE_GRID_MARGIN_PX
      : frozen?.width ?? width;
  // Stable ref so the drag/resize-stop callbacks read the live column count
  // without being recreated (and re-binding RGL) on every resize tick.
  const colsRef = useRef(cols);
  colsRef.current = cols;
  // deriveWorkspaceLayout sizes the (fixed) row height from the frozen container
  // height; with nothing locked it is a pure passthrough of the stored geometry.
  //
  // Locking is deliberately NOT fed into the solver. In the fixed-cell page model
  // a panel never rescales — a locked tile is simply made `static` (non-draggable
  // / non-resizable) in rglLayouts below. The old behaviour re-solved rowHeight
  // and shrank the unlocked tiles whenever a tile was locked (to hold the locked
  // one at a frozen pixel height while fitting the viewport); with a constant
  // cell size that only made the panel visibly change size on lock/unlock. So we
  // pass `locked: false` here to keep the solver on its inert passthrough path
  // regardless of lock state.
  const deriveTiles = useMemo<DeriveTile[]>(
    () =>
      tiles.map((t) => {
        const def = getPanelDef(t.panelId, pluginPanelKey);
        const w = def?.maxW !== undefined ? Math.min(t.w, def.maxW) : t.w;
        const h = def?.maxH !== undefined ? Math.min(t.h, def.maxH) : t.h;
        return {
          uid: t.uid,
          x: t.x,
          y: t.y,
          w,
          h,
          // Lock is a pure static flag now — never a size-compensation trigger.
          locked: false,
          minH: def?.minH ?? WORKSPACE_TILE_MIN_H,
        };
      }),
    [tiles, pluginPanelKey],
  );

  const derived = useMemo(
    () =>
      deriveWorkspaceLayout(deriveTiles, {
        containerHeight: gridHeight,
        authoredRowHeightPx: WORKSPACE_ROW_HEIGHT_PX,
        gridMarginPx: WORKSPACE_GRID_MARGIN_PX,
        rowGapShare: WORKSPACE_ROW_GAP_SHARE,
        targetRows: WORKSPACE_TARGET_ROWS,
        minRowHeightPx: 0.1,
      }),
    [deriveTiles, gridHeight],
  );
  const { rowHeight, rowMargin } = derived;

  // Stored geometry, keyed by uid, for reconciling RGL's echo of the derived
  // (possibly shrunk) render layout back to what we persist. Refs keep the
  // persist callback stable without going stale.
  const storedByUid = useMemo(
    () => new Map(tiles.map((t) => [t.uid, { x: t.x, y: t.y, w: t.w, h: t.h }])),
    [tiles],
  );
  const derivedRef = useRef(derived);
  derivedRef.current = derived;
  const storedByUidRef = useRef(storedByUid);
  storedByUidRef.current = storedByUid;
  // All persistence flows through here so the shrunk render geometry never
  // reaches the store. When nothing is compensated this is the identity, so the
  // common path is byte-identical to the legacy behaviour.
  const persist = useCallback(
    (next: Layout) => {
      onLayoutChange(
        reconcileReportedToStored(
          next,
          derivedRef.current,
          storedByUidRef.current,
        ),
      );
    },
    [onLayoutChange],
  );

  const placementByUid = useMemo(
    () => new Map(derived.placements.map((p) => [p.uid, p])),
    [derived],
  );

  // When locking a tile, capture its current on-screen pixel height so the
  // store can hold it there afterwards. The capture is the live rendered size,
  // so clicking lock never resizes the panel (see deriveWorkspaceLayout).
  const handleToggleTileLock = useCallback(
    (uid: string, locked: boolean) => {
      if (!locked) {
        onToggleTileLock(uid, false);
        return;
      }
      const p = placementByUid.get(uid);
      const lockedHeightPx = p
        ? derived.rowHeight * p.h + Math.max(0, p.h - 1) * derived.rowMargin
        : undefined;
      onToggleTileLock(uid, true, lockedHeightPx);
    },
    [onToggleTileLock, placementByUid, derived.rowHeight, derived.rowMargin],
  );

  const workspaceDragCompactor = useMemo(
    () => createWorkspaceDragCompactor(() => dragStartRef.current),
    [],
  );
  const workspaceCompactor =
    gridInteraction === 'resize'
      ? WORKSPACE_RESIZE_COMPACTOR
      : workspaceDragCompactor;

  const onPointerDownCapture = useCallback(
    (event: ReactPointerEvent<HTMLDivElement>) => {
      const target = event.target;
      if (!(target instanceof Element)) return;
      if (workspaceLocked) return;
      if (target.closest('[data-tile-locked="true"]')) return;

      if (target.closest('.react-resizable-handle')) {
        setGridInteraction('resize');
        return;
      }

      if (
        target.closest('.workspace-tile-header') &&
        !target.closest('.workspace-tile-close') &&
        !target.closest('.workspace-tile-lock')
      ) {
        setGridInteraction('drag');
      }
    },
    [workspaceLocked],
  );

  const onDragStart = useCallback((
    layout: Layout,
    oldItem: LayoutItem | null,
  ) => {
    draggingRef.current = true;
    skipPostDropLayoutChangeRef.current = false;
    dragStartRef.current = oldItem
      ? {
          item: { ...oldItem },
          layout: layout.map((item) => ({ ...item })),
        }
      : null;
    setGridInteraction('drag');
  }, []);
  const onResizeStart = useCallback(() => {
    // Mark the gesture active so the per-frame onLayoutChange persist is
    // suppressed: live resize uses the free (overlap-allowed) compactor, and we
    // resolve the overlap once on stop rather than persisting overlapping
    // geometry mid-drag.
    draggingRef.current = true;
    setGridInteraction('resize');
  }, []);
  const onDragStop = useCallback((
    layout: Layout,
    oldItem: LayoutItem | null,
    newItem: LayoutItem | null,
  ) => {
    const dragStart = dragStartRef.current;
    const finalItem = dragStart
      ? layout.find((item) => item.i === dragStart.item.i)
      : newItem;
    const moved = dragStart && finalItem
      ? dragStart.item.x !== finalItem.x || dragStart.item.y !== finalItem.y
      : Boolean(
          oldItem &&
            newItem &&
            (oldItem.x !== newItem.x || oldItem.y !== newItem.y),
        );
    const previousDropItem = moved
      ? dragStart ?? (oldItem ? { item: { ...oldItem }, layout: [] } : null)
      : null;
    draggingRef.current = false;
    dragStartRef.current = null;
    setGridInteraction(null);

    if (previousDropItem) {
      skipPostDropLayoutChangeRef.current = true;
      window.setTimeout(() => {
        skipPostDropLayoutChangeRef.current = false;
      }, 250);
      persist(
        autoFitDroppedPanel(layout, colsRef.current, previousDropItem),
      );
    }
  }, [persist]);
  const onResizeStop = useCallback((
    layout: Layout,
    oldItem: LayoutItem | null,
    newItem: LayoutItem | null,
  ) => {
    draggingRef.current = false;
    dragStartRef.current = null;
    setGridInteraction(null);
    const resizedId = newItem?.i ?? oldItem?.i;
    if (!resizedId) return;
    // Keep the resized tile's new size; nudge only the neighbours it now
    // overlaps to their nearest free slot (no cascade). Guard the post-resize
    // echo for a beat so RGL's re-render doesn't re-persist the raw layout.
    skipPostDropLayoutChangeRef.current = true;
    window.setTimeout(() => {
      skipPostDropLayoutChangeRef.current = false;
    }, 250);
    persist(resolveResizeOverlaps(layout, resizedId, colsRef.current));
  }, [persist]);
  const handleLayoutChange = useCallback(
    (next: Layout) => {
      if (draggingRef.current || skipPostDropLayoutChangeRef.current) {
        return;
      }

      persist(next);
    },
    [persist],
  );

  // RGL needs a stable per-render layouts.lg array. Memoise against the
  // tile list identity so we don't push a new prop on every parent render.
  // Per-panel maxW/maxH (when defined in panels.ts) is propagated here so
  // RGL clamps user resize drags.
  const rglLayouts = useMemo(
    () => ({
      lg: tiles.map((t) => {
        const def = getPanelDef(t.panelId, pluginPanelKey);
        const tileLocked = workspaceLocked || t.locked === true;
        // Geometry comes from the derived render layout: locked tiles stay at
        // their authored size while unlocked tiles shrink to fit (see
        // deriveWorkspaceLayout). The placement is already clamped to the
        // panel's maxW/maxH (deriveTiles does the clamp before solving), so a
        // tile saved wider than its cap snaps back here and the next persist
        // writes the fix.
        const placement = placementByUid.get(t.uid);
        const w = placement?.w ?? t.w;
        const h = placement?.h ?? t.h;
        const x = placement?.x ?? t.x;
        const y = placement?.y ?? t.y;
        return {
          i: t.uid,
          x,
          y,
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
          static: tileLocked,
          isDraggable: !tileLocked,
          isResizable: !tileLocked,
        };
      }),
    }),
    [tiles, pluginPanelKey, workspaceLocked, placementByUid],
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
          className={`all-panels-grid${
            gridInteraction ? ' all-panels-grid--interacting' : ''
          }`}
          width={gridWidth}
          breakpoints={{ lg: 0 }}
          cols={{ lg: cols }}
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
            cancel: '.workspace-tile-close, .workspace-tile-lock',
            bounded: false,
          }}
          onDragStart={onDragStart}
          onDragStop={onDragStop}
          onResizeStart={onResizeStart}
          onResizeStop={onResizeStop}
          onLayoutChange={handleLayoutChange}
          layouts={rglLayouts}
        >
          {tiles.map((tile) => {
            const effectiveLocked = workspaceLocked || tile.locked === true;
            return (
              <div
                key={tile.uid}
                data-tile-uid={tile.uid}
                data-tile-locked={effectiveLocked ? 'true' : undefined}
              >
                <PanelTile
                  tile={tile}
                  layoutId={layoutId}
                  workspaceLocked={workspaceLocked}
                  onRequestRemoveTile={onRequestRemoveTile}
                  onToggleTileLock={handleToggleTileLock}
                />
              </div>
            );
          })}
        </ResponsiveGridLayout>
      )}
    </div>
  );
}

interface PanelTileProps {
  tile: WorkspaceTile;
  layoutId: string;
  workspaceLocked: boolean;
  onRequestRemoveTile: (uid: string, title: string) => void;
  onToggleTileLock: (uid: string, locked: boolean) => void;
}

// Memoised so a parent re-render (e.g. another tile's drag updating the
// store) doesn't reconcile every panel's subtree. Effective only because
// the store preserves per-tile object identity across unrelated mutations
// and `onRemoveTile` is the stable zustand action reference.
const PanelTile = memo(function PanelTile({
  tile,
  layoutId,
  workspaceLocked,
  onRequestRemoveTile,
  onToggleTileLock,
}: PanelTileProps) {
  const def = getPanelDef(tile.panelId);
  if (!def) {
    return (
      <UnavailablePanelTile
        tile={tile}
        layoutId={layoutId}
        workspaceLocked={workspaceLocked}
        onRequestRemoveTile={onRequestRemoveTile}
        onToggleTileLock={onToggleTileLock}
      />
    );
  }
  const handleRemove = () => onRequestRemoveTile(tile.uid, def.name);
  const handleToggleLock = () => onToggleTileLock(tile.uid, tile.locked !== true);
  const tileLocked = tile.locked === true;
  const effectiveLocked = workspaceLocked || tileLocked;
  // Headerless panels own their entire tile surface and draw their own
  // header (if any). They MUST include an element with class
  // `.workspace-tile-header` so RGL drag picks up, and a
  // `.workspace-tile-close` button bound to the injected onRemove.
  if (def.headerless) {
    return (
      <div
        className={`workspace-tile workspace-tile--headerless${
          effectiveLocked ? ' workspace-tile--locked' : ''
        }`}
        data-panel-id={tile.panelId}
      >
        <PanelBody
          tile={tile}
          layoutId={layoutId}
          onRemove={handleRemove}
          tileLocked={tileLocked}
          workspaceLocked={workspaceLocked}
          onToggleLock={handleToggleLock}
        />
      </div>
    );
  }
  return (
    <div
      className={`workspace-tile${effectiveLocked ? ' workspace-tile--locked' : ''}`}
      data-panel-id={tile.panelId}
    >
      <TileChrome
        title={def.name}
        onRemove={handleRemove}
        locked={tileLocked}
        workspaceLocked={workspaceLocked}
        onToggleLock={handleToggleLock}
      />
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
  tileLocked = false,
  workspaceLocked = false,
  onToggleLock,
}: {
  tile: WorkspaceTile;
  layoutId: string;
  onRemove?: () => void;
  tileLocked?: boolean;
  workspaceLocked?: boolean;
  onToggleLock?: () => void;
}) {
  // Per-tile config-bound rendering for multi-instance / configurable
  // panels. Single-instance panels just render their component as-is.
  if (tile.panelId === 'hero') {
    return (
      <HeroPanel
        tile={tile}
        layoutId={layoutId}
        onRemove={onRemove}
        tileLocked={tileLocked}
        workspaceLocked={workspaceLocked}
        onToggleLock={onToggleLock}
      />
    );
  }
  if (tile.panelId === 'metergroup') {
    return (
      <MeterGroupTileBody
        tile={tile}
        layoutId={layoutId}
        onRemove={onRemove}
        tileLocked={tileLocked}
        workspaceLocked={workspaceLocked}
        onToggleLock={onToggleLock}
      />
    );
  }
  if (tile.panelId === 'urlembed') {
    return (
      <UrlEmbedTileBody
        tile={tile}
        layoutId={layoutId}
        onRemove={onRemove}
        tileLocked={tileLocked}
        workspaceLocked={workspaceLocked}
        onToggleLock={onToggleLock}
      />
    );
  }
  const def = getPanelDef(tile.panelId);
  if (!def) return null;
  const Component = def.component;
  // Headerless single-instance panels that own their own header receive
  // onRemove so their close button can drop the tile (matches the meter
  // group special-case above without pulling in its per-tile config).
  if (def.headerless && onRemove) {
    return (
      <Component
        onRemove={onRemove}
        tileLocked={tileLocked}
        workspaceLocked={workspaceLocked}
        onToggleLock={onToggleLock}
      />
    );
  }
  return <Component />;
}

function UnavailablePanelTile({
  tile,
  workspaceLocked,
  onRequestRemoveTile,
  onToggleTileLock,
}: PanelTileProps) {
  const handleRemove = useCallback(
    () => onRequestRemoveTile(tile.uid, 'Unavailable panel'),
    [onRequestRemoveTile, tile.uid],
  );
  const handleToggleLock = useCallback(
    () => onToggleTileLock(tile.uid, tile.locked !== true),
    [onToggleTileLock, tile.locked, tile.uid],
  );
  const openPlugins = useCallback(() => {
    useLayoutStore.getState().setSettingsView(true, 'plugins');
  }, []);

  return (
    <div
      className={`workspace-tile workspace-tile--unavailable${
        workspaceLocked || tile.locked ? ' workspace-tile--locked' : ''
      }`}
    >
      <TileChrome
        title="Unavailable panel"
        onRemove={handleRemove}
        locked={tile.locked === true}
        workspaceLocked={workspaceLocked}
        onToggleLock={handleToggleLock}
      />
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
  tileLocked,
  workspaceLocked,
  onToggleLock,
}: {
  tile: WorkspaceTile;
  layoutId: string;
  onRemove?: () => void;
  tileLocked?: boolean;
  workspaceLocked?: boolean;
  onToggleLock?: () => void;
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
    <MeterGroupPanel
      config={config}
      setConfig={setConfig}
      onRemove={onRemove}
      tileLocked={tileLocked}
      workspaceLocked={workspaceLocked}
      onToggleLock={onToggleLock}
    />
  );
}

function UrlEmbedTileBody({
  tile,
  layoutId,
  onRemove,
  tileLocked,
  workspaceLocked,
  onToggleLock,
}: {
  tile: WorkspaceTile;
  layoutId: string;
  onRemove?: () => void;
  tileLocked?: boolean;
  workspaceLocked?: boolean;
  onToggleLock?: () => void;
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
    <UrlEmbedPanel
      config={config}
      setConfig={setConfig}
      onRemove={onRemove}
      tileLocked={tileLocked}
      workspaceLocked={workspaceLocked}
      onToggleLock={onToggleLock}
    />
  );
}
