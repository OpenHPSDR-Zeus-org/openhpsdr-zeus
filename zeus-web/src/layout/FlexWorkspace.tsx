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
import { useConnectionStore } from '../state/connection-store';
import { getPanelDef } from './panels';
import {
  WORKSPACE_RESIZE_COMPACTOR,
  autoFitDroppedPanel,
  createWorkspaceDragCompactor,
  resolveResizeOverlaps,
  type WorkspaceDragStartSnapshot,
} from './workspaceGrid';
import { usePluginPanels } from '../plugins/runtime/usePluginPanels';
import {
  EMPTY_WORKSPACE_LAYOUT,
  WORKSPACE_GRID_COLS,
  WORKSPACE_ROW_HEIGHT_PX,
  WORKSPACE_TILE_MIN_H,
  WORKSPACE_TILE_MIN_W,
  type WorkspaceTile,
} from './workspace';
import { AddPanelModal } from './AddPanelModal';
import {
  findLayoutTabAtPoint,
  setLayoutTabDropTarget,
} from './layout-tab-dnd';
import { ScaleToFitTile } from './ScaleToFitTile';
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
import { LanBrowserPanel } from './panels/LanBrowserPanel';
import {
  parseUrlEmbedConfig,
  type UrlEmbedConfig,
} from './panels/urlEmbedConfig';

const WORKSPACE_GRID_MARGIN_PX = 3;

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

  const isPrimary = !layoutId;
  // Add a panel: it drops into the first free slot on the field, and when the
  // field is full it simply OVERLAPS (the store places it at the origin) for the
  // operator to resize / move. No "field is full" popup, no spill to a new
  // layout — the workspace is overlap-friendly and bounded to the view.
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
        isPrimary={isPrimary}
        onLayoutChange={onLayoutChange}
        onRequestRemoveTile={(uid, title) => setPendingRemoveTile({ uid, title })}
        onToggleTileLock={(uid, locked, lockedHeightPx) =>
          setTileLockedInLayout(targetLayoutId, uid, locked, lockedHeightPx)
        }
      />
      <TerminatorLines active={terminatorActive} />
      {showAddPanelModal && !addPanelOpen && (
        <>
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
        </>
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
  /** True for the main dock workspace (not a detached window). Only the primary
   *  reports its page size to the store, which drives add-panel pagination. */
  isPrimary: boolean;
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
  isPrimary,
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
  const [gridInteraction, setGridInteraction] =
    useState<GridInteraction>(null);
  const draggingRef = useRef(false);
  const skipPostDropLayoutChangeRef = useRef(false);
  const dragStartRef = useRef<WorkspaceDragStartSnapshot | null>(null);
  // Cross-layout panel transfer: while a tile drag is live, track the pointer
  // and remember which LeftLayoutBar tab (if any) it's hovering, so releasing
  // over another layout moves the panel there instead of repositioning it on
  // this workspace.
  const moveTileToLayout = useLayoutStore((s) => s.moveTileToLayout);
  const tileDragActiveRef = useRef(false);
  const dropTargetLayoutIdRef = useRef<string | null>(null);
  useEffect(() => {
    const onPointerMove = (e: PointerEvent) => {
      if (!tileDragActiveRef.current) return;
      // A tab over THIS layout isn't a transfer target — dropping there is a
      // normal reposition. Only foreign tabs arm the move.
      const overId = findLayoutTabAtPoint(e.clientX, e.clientY);
      const target = overId && overId !== layoutId ? overId : null;
      if (dropTargetLayoutIdRef.current !== target) {
        dropTargetLayoutIdRef.current = target;
        setLayoutTabDropTarget(target);
      }
    };
    window.addEventListener('pointermove', onPointerMove);
    return () => window.removeEventListener('pointermove', onPointerMove);
  }, [layoutId]);
  // Live viewport height of the (scrolling) workspace, in pixels. Only used to
  // report how many rows are currently visible to the add-panel pagination flow
  // (setViewportPage below); the fixed-cell geometry itself never depends on it.
  const [containerHeight, setContainerHeight] = useState(0);
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

  // Fixed-cell workspace. A column and a row are a CONSTANT pixel size, so a
  // panel never rescales when the window is resized — the core of the "hardware
  // front panel" model. Resizing the window only changes how many cells are
  // visible: the column COUNT tracks the live width (growing the window adds
  // columns, shrinking removes them, never below the base 24 nor below what the
  // current layout already occupies), and vertically the page grows downward
  // and SCROLLS (all-panels.css). Nothing is ever shrunk to "fit" the viewport,
  // so an operator can expand a panel as far as they like and a smaller window
  // simply scrolls to the content instead of squashing or clipping it.
  //
  // The column pixel pitch is captured ONCE — from a SETTLED width at the base
  // 24-column count — and then held constant. RGL derives a column width of
  // (containerWidth - margin*(cols-1)) / cols, so we invert that to recover the
  // per-column pitch and feed RGL an exact width for `cols` columns, pinning the
  // rendered column width to baseColWidth with no sub-pixel rescale on resize.
  //
  // The latch must wait for the width to STOP changing, not grab the first
  // non-zero value. During connect/mount the container can report a too-small
  // intermediate width (notably in the Photino desktop webview) before the flex
  // pass settles. Freezing on that transient yields a tiny baseColWidth, which
  // inflates `cols` to fill the real window, which crams every tile (placed in
  // columns 0..24) into a narrow strip on the left — the "panels stacked on the
  // left" bug. So debounce: only latch a width that survives a frame without
  // changing, so a later, larger settled width supersedes an early narrow one.
  // (While frozenWidth is still 0 the render falls back to baseColWidth=0 →
  // cols=24 → gridWidth=live width, which already renders correctly; the latch
  // only fixes the pitch held constant across subsequent window resizes.)
  const [frozenWidth, setFrozenWidth] = useState(0);
  useEffect(() => {
    if (frozenWidth > 0) return;
    if (!mounted || !(width > 0)) return;
    const id = window.setTimeout(() => setFrozenWidth(width), 150);
    return () => window.clearTimeout(id);
  }, [frozenWidth, mounted, width]);
  // Workspace UI zoom (operator-set, server-persisted). A multiplier on the
  // CELL PITCH — not a CSS transform — so RGL keeps doing its drag/resize math
  // in real pixels and hit-testing stays correct at any zoom. zoom < 1 shrinks
  // the cells, so monitorCols/monitorRows/visibleRows below all grow and the
  // operator gets MORE grid to spread panels across; zoom > 1 enlarges the
  // cells (fewer fit; the canvas scrolls when a layout outgrows the monitor).
  const workspaceZoomPct = useConnectionStore((s) => s.workspaceZoomPct);
  const zoomFactor = (workspaceZoomPct > 0 ? workspaceZoomPct : 100) / 100;
  const baseColWidth =
    frozenWidth > 0
      ? ((frozenWidth - WORKSPACE_GRID_MARGIN_PX * (WORKSPACE_GRID_COLS - 1)) /
          WORKSPACE_GRID_COLS) *
        zoomFactor
      : 0;
  // The MONITOR's capacity in grid cells — the MAXIMUM field. The workspace
  // canvas is bounded to this (not to the live window), so a panel can never be
  // dragged or sized past the physical screen and there is no infinite void to
  // scroll into. The window is then free to be smaller (scroll to reach panels)
  // or maximized (everything fits, no scrollbars) — the maximized window is the
  // north star. Derived from screen.avail* at the constant row pitch and the
  // latched column pitch. 0 when unknown (screen unavailable / pitch not yet
  // latched): no monitor bound is applied and behaviour falls back to the live
  // window, so headless/SSR/early-mount never break or prune against a bogus
  // field.
  const screenAvailW =
    typeof screen !== 'undefined' && screen.availWidth > 0
      ? screen.availWidth
      : 0;
  const screenAvailH =
    typeof screen !== 'undefined' && screen.availHeight > 0
      ? screen.availHeight
      : 0;
  const monitorRows =
    screenAvailH > 0
      ? Math.max(
          1,
          Math.floor(
            (screenAvailH + WORKSPACE_GRID_MARGIN_PX) /
              (WORKSPACE_ROW_HEIGHT_PX * zoomFactor + WORKSPACE_GRID_MARGIN_PX),
          ),
        )
      : 0;
  const monitorCols =
    screenAvailW > 0 && baseColWidth > 0
      ? Math.max(
          WORKSPACE_GRID_COLS,
          Math.floor(
            (screenAvailW + WORKSPACE_GRID_MARGIN_PX) /
              (baseColWidth + WORKSPACE_GRID_MARGIN_PX),
          ),
        )
      : 0;
  // Never drop below the base count or below the rightmost tile already placed,
  // so a window-shrink never clamps an existing tile inward (RGL would otherwise
  // shove any tile whose x+w exceeds cols). When the window is narrower than the
  // occupied extent the workspace scrolls horizontally rather than reflowing.
  const occupiedCols = useMemo(
    () => tiles.reduce((m, t) => Math.max(m, t.x + t.w), WORKSPACE_GRID_COLS),
    [tiles],
  );
  const colsForWidth =
    baseColWidth > 0
      ? Math.max(
          occupiedCols,
          Math.floor(
            (width + WORKSPACE_GRID_MARGIN_PX) /
              (baseColWidth + WORKSPACE_GRID_MARGIN_PX),
          ),
        )
      : occupiedCols;
  // Cap the canvas at the monitor width: it is never wider than the physical
  // screen, so horizontal drag is bounded to the monitor and scroll only ever
  // spans real content. No cap until the monitor size is known.
  const cols =
    monitorCols > 0 ? Math.min(monitorCols, colsForWidth) : colsForWidth;
  // Exact width for `cols` columns at the fixed pitch, so RGL's derived column
  // width is exactly baseColWidth (no sub-pixel rescale as the window resizes).
  const gridWidth =
    baseColWidth > 0
      ? cols * baseColWidth + (cols - 1) * WORKSPACE_GRID_MARGIN_PX
      : frozenWidth || width;
  // Stable ref so the drag/resize-stop callbacks read the live column count
  // without being recreated (and re-binding RGL) on every resize tick.
  const colsRef = useRef(cols);
  colsRef.current = cols;
  // Cells are a constant pixel size — rowHeight and the grid margins never change
  // with the window. A locked tile therefore holds its pixel height for free
  // (h rows × a constant rowHeight), so the old shrink-to-fit solver and its
  // per-tile pixel compensation are gone: the render layout is simply the stored
  // geometry, clamped to each panel's maxW/maxH.
  // Row pitch scales with the same zoom factor so cells grow/shrink uniformly
  // (square-ish aspect preserved). RGL accepts a fractional rowHeight.
  const rowHeight = WORKSPACE_ROW_HEIGHT_PX * zoomFactor;
  const rowMargin = WORKSPACE_GRID_MARGIN_PX;

  // Per-tile render placement = stored geometry clamped to the panel's caps. A
  // tile saved wider/taller than its cap snaps back here, and RGL's echo of that
  // clamped layout persists the correction on the next onLayoutChange.
  const placementByUid = useMemo(() => {
    const m = new Map<
      string,
      { x: number; y: number; w: number; h: number }
    >();
    for (const t of tiles) {
      const def = getPanelDef(t.panelId);
      const w = def?.maxW !== undefined ? Math.min(t.w, def.maxW) : t.w;
      const h = def?.maxH !== undefined ? Math.min(t.h, def.maxH) : t.h;
      m.set(t.uid, { x: t.x, y: t.y, w, h });
    }
    return m;
  }, [tiles, pluginPanelKey]);

  // With constant cells the render geometry equals the stored geometry, so
  // persistence is a straight passthrough to the store (no reconcile pass).
  const persist = onLayoutChange;

  // Rows that fit the live workspace area (the measured container, which sits
  // ABOVE the footer). This is the vertical FIELD OF VIEW: the hard drag/resize
  // bound (RGL maxRows) and the height the add-panel flow places into. Bounding
  // to the window — not the monitor — is what keeps horizontal and vertical
  // symmetric: a panel can't be dragged below the visible area (past the footer)
  // any more than it can be dragged past the right edge, so dragging never
  // creates scroll. The window may still be smaller than a layout authored when
  // maximized — that legitimately scrolls — but nothing the operator does in the
  // current view pushes content out of it.
  const visibleRows =
    rowHeight > 0 && containerHeight > 0
      ? Math.max(
          1,
          Math.floor((containerHeight + rowMargin) / (rowHeight + rowMargin)),
        )
      : 0;

  // Report the visible field size (grid cells) to the store so the add-panel
  // flow places a new panel within the current view (and overlaps at the origin
  // when the view is full). Only the primary (docked) workspace reports.
  const pageCols = cols;
  const pageRows = visibleRows;
  const setViewportPage = useLayoutStore((s) => s.setViewportPage);
  useEffect(() => {
    if (!isPrimary || pageRows <= 0 || pageCols <= 0) return;
    setViewportPage(pageCols, pageRows);
  }, [isPrimary, pageCols, pageRows, setViewportPage]);

  // One-time cleanup: close panels saved BEYOND the monitor (unreachable even
  // when maximized — e.g. dragged off-screen before the canvas was bounded).
  // Runs once per layout, and only after the column pitch has latched
  // (frozenWidth > 0) and the monitor size is known, so a transient narrow
  // mount can never prune against a bogus field. Panels merely outside a small
  // window are WITHIN the monitor and are left untouched — they reappear when
  // the window is maximized or scrolled to.
  const pruneOffscreenTilesFromLayout = useLayoutStore(
    (s) => s.pruneOffscreenTilesFromLayout,
  );
  const prunedLayoutsRef = useRef<Set<string>>(new Set());
  useEffect(() => {
    if (!isPrimary) return;
    if (frozenWidth <= 0 || monitorCols <= 0 || monitorRows <= 0) return;
    if (prunedLayoutsRef.current.has(layoutId)) return;
    prunedLayoutsRef.current.add(layoutId);
    pruneOffscreenTilesFromLayout(layoutId, monitorCols, monitorRows);
  }, [
    isPrimary,
    frozenWidth,
    monitorCols,
    monitorRows,
    layoutId,
    pruneOffscreenTilesFromLayout,
  ]);

  // Locking just pins the tile (static). With a constant cell size there is no
  // pixel height to capture — the tile keeps its size automatically.
  const handleToggleTileLock = useCallback(
    (uid: string, locked: boolean) => onToggleTileLock(uid, locked),
    [onToggleTileLock],
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
    tileDragActiveRef.current = true;
    dropTargetLayoutIdRef.current = null;
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
    // Cross-layout transfer: the panel was released over a different layout's
    // tab. Move it there and skip the normal reposition persist entirely — the
    // tile no longer belongs to this workspace, so writing back the dragged
    // geometry would resurrect it.
    tileDragActiveRef.current = false;
    const transferTarget = dropTargetLayoutIdRef.current;
    dropTargetLayoutIdRef.current = null;
    setLayoutTabDropTarget(null);
    if (transferTarget && transferTarget !== layoutId) {
      const movedUid =
        dragStartRef.current?.item.i ?? oldItem?.i ?? newItem?.i ?? null;
      draggingRef.current = false;
      dragStartRef.current = null;
      setGridInteraction(null);
      if (movedUid) {
        // Suppress the post-drop layout echo so RGL's re-render (which still
        // momentarily holds the tile) doesn't re-persist it onto this layout.
        skipPostDropLayoutChangeRef.current = true;
        window.setTimeout(() => {
          skipPostDropLayoutChangeRef.current = false;
        }, 250);
        moveTileToLayout(layoutId, transferTarget, movedUid);
        return;
      }
    }

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
  }, [persist, layoutId, moveTileToLayout]);
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
        const def = getPanelDef(t.panelId);
        const tileLocked = workspaceLocked || t.locked === true;
        // Geometry is the stored placement, already clamped to the panel's
        // maxW/maxH (placementByUid does the clamp), so a tile saved wider than
        // its cap snaps back here and the next persist writes the fix. Cells are
        // a constant size, so a locked tile holds its pixel height automatically.
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
          // workspace-global minimum. RGL clamps drag-resize to these so a tile
          // can never be sized below its readable footprint.
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
          // Hard vertical bound of the field — the VISIBLE workspace height in
          // rows (above the footer). RGL's default gridBounds constraint clamps
          // every drag/resize to 0..maxRows-h, so a panel can move or grow
          // anywhere within the current view but never below it — dragging never
          // pushes content past the footer into scroll. Width is bounded the
          // same way by `cols` (0..cols-w). Left unset (Infinity) until the
          // height is measured so nothing is clamped against a bogus early-mount
          // field. Maximize and the bound grows with the window; shrink it and a
          // layout authored larger simply scrolls to reach the rest.
          {...(visibleRows > 0 ? { maxRows: visibleRows } : {})}
          rowHeight={rowHeight}
          margin={[WORKSPACE_GRID_MARGIN_PX, rowMargin]}
          containerPadding={[0, 0]}
          // Resize handles on the sides and bottom — NOT the top edge.
          // 'n'/'ne'/'nw' would overlay the tile header and steal the
          // mousedown that drags the panel (a header-grab would start a resize
          // instead of a move). 'e'/'w' reach any width, 's' any height, and
          // the bottom corners cover diagonal — enough for "any size" while the
          // header stays grabbable. all-panels.css styles each handle
          // per-direction so they don't stack in the SE corner.
          resizeConfig={{
            handles: ['s', 'e', 'w', 'se', 'sw'],
          }}
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
        {!def.fillNative &&
        (def.scaleToFit === true ||
          (def.designW !== undefined && def.designH !== undefined)) ? (
          // Explicit design size wins; otherwise auto-measure mode (the
          // scaleToFit opt-in renders ScaleToFitTile with no design props so
          // it measures the content's intrinsic footprint itself).
          def.designW !== undefined && def.designH !== undefined ? (
            <ScaleToFitTile designW={def.designW} designH={def.designH}>
              <PanelBody tile={tile} layoutId={layoutId} />
            </ScaleToFitTile>
          ) : (
            <ScaleToFitTile>
              <PanelBody tile={tile} layoutId={layoutId} />
            </ScaleToFitTile>
          )
        ) : (
          <PanelBody tile={tile} layoutId={layoutId} />
        )}
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
  if (tile.panelId === 'lanbrowser') {
    return (
      <LanBrowserTileBody
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

// LAN Browser shares the URL-embed per-tile config shape (url + title); only the
// rendered component differs (it frames the server-side LAN proxy).
function LanBrowserTileBody({
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
    <LanBrowserPanel
      config={config}
      setConfig={setConfig}
      onRemove={onRemove}
      tileLocked={tileLocked}
      workspaceLocked={workspaceLocked}
      onToggleLock={onToggleLock}
    />
  );
}
