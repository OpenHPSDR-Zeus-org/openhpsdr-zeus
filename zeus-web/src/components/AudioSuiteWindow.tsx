// SPDX-License-Identifier: GPL-2.0-or-later
//
// AudioSuiteWindow — draggable floating window containing the audio
// plugin chain. Replaces the inline rendering that used to live in
// TxAudioToolsPanel (per Phase 2 of issue #332). Operators open it
// via the "Audio Suite" button on TX Audio Tools, drag tiles at the
// top to reorder the chain, toggle audition to hear the chain output
// in their RX playback path, and adjust per-plugin settings in the
// stacked panels below.
//
// Drag-and-drop is vanilla HTML5 (no npm dep per CLAUDE.md red-line
// on new deps). Window dragging uses Pointer Events with capture.
// All chrome is in Zeus tokens — no raw hex per the design rules in
// docs/lessons/dev-conventions.md.

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { usePluginPanels } from '../plugins/runtime/usePluginPanels';
import type { RegisteredPluginPanel } from '../plugins/runtime/pluginRuntime';
import { AudioChainMeters } from './AudioChainMeters';
import {
  AUDIO_SUITE_WINDOW_MIN_WIDTH,
  AUDIO_SUITE_WINDOW_MIN_HEIGHT,
  useAudioSuiteStore,
} from '../state/audio-suite-store';

const CHAIN_SLOT = 'tx-audio-tools.chain';

// Plugin editors (the built-in EQ graph, gate, etc.) are responsive and
// stretch to fill whatever width they're given. The embedded host lives
// in the TX Audio Tools settings pane, which is ~2000 px wide on a normal
// monitor — letting a 10-band EQ sprawl across all of it looks cheap and
// makes the controls hard to read. Cap the rack column at a sane width
// and centre it so each unit reads like a real rack module (the VSTHost
// look the operator asked for). Tuned so the EQ graph + meters sit at a
// comfortable density without horizontal sprawl.
const RACK_MAX_WIDTH = 1120;

/** Edge codes for the resize handles — 4 edges + 4 corners. */
type ResizeEdge = 'n' | 's' | 'e' | 'w' | 'ne' | 'nw' | 'se' | 'sw';

const RESIZE_HANDLE_PX = 6; // grab thickness for each edge

const CURSOR_FOR_EDGE: Record<ResizeEdge, string> = {
  n: 'ns-resize',
  s: 'ns-resize',
  e: 'ew-resize',
  w: 'ew-resize',
  ne: 'nesw-resize',
  sw: 'nesw-resize',
  nw: 'nwse-resize',
  se: 'nwse-resize',
};

/**
 * Compute the absolute-position style for a resize handle on the
 * given edge. Handles cover only their edge / corner — clicks in
 * the interior pass through to the window content. Corners get a
 * faint L-shaped border in --fg-3 as a discoverability hint so
 * operators see "there's a resize handle here" without the cursor
 * change being the only cue.
 */
function handleStyleFor(edge: ResizeEdge): React.CSSProperties {
  const base: React.CSSProperties = {
    position: 'absolute',
    zIndex: 1,
    cursor: CURSOR_FOR_EDGE[edge],
    touchAction: 'none',
  };
  const cornerBorder = '1px solid var(--fg-3)';
  switch (edge) {
    case 'n':  return { ...base, top: 0, left: RESIZE_HANDLE_PX, right: RESIZE_HANDLE_PX, height: RESIZE_HANDLE_PX };
    case 's':  return { ...base, bottom: 0, left: RESIZE_HANDLE_PX, right: RESIZE_HANDLE_PX, height: RESIZE_HANDLE_PX };
    case 'e':  return { ...base, top: RESIZE_HANDLE_PX, bottom: RESIZE_HANDLE_PX, right: 0, width: RESIZE_HANDLE_PX };
    case 'w':  return { ...base, top: RESIZE_HANDLE_PX, bottom: RESIZE_HANDLE_PX, left: 0, width: RESIZE_HANDLE_PX };
    case 'ne': return { ...base, top: 0, right: 0, width: RESIZE_HANDLE_PX, height: RESIZE_HANDLE_PX,
                        borderTop: cornerBorder, borderRight: cornerBorder, opacity: 0.6 };
    case 'nw': return { ...base, top: 0, left: 0, width: RESIZE_HANDLE_PX, height: RESIZE_HANDLE_PX,
                        borderTop: cornerBorder, borderLeft: cornerBorder, opacity: 0.6 };
    case 'se': return { ...base, bottom: 0, right: 0, width: RESIZE_HANDLE_PX, height: RESIZE_HANDLE_PX,
                        borderBottom: cornerBorder, borderRight: cornerBorder, opacity: 0.6 };
    case 'sw': return { ...base, bottom: 0, left: 0, width: RESIZE_HANDLE_PX, height: RESIZE_HANDLE_PX,
                        borderBottom: cornerBorder, borderLeft: cornerBorder, opacity: 0.6 };
  }
}

/**
 * One resize handle. Invisible 6px region on its assigned edge /
 * corner; the cursor changes on hover so operators can see where
 * the grab regions are. Pointer Events with capture so the drag
 * keeps tracking even if the cursor leaves the handle while
 * dragging.
 */
function ResizeHandle({ edge }: { edge: ResizeEdge }) {
  const x = useAudioSuiteStore((s) => s.x);
  const y = useAudioSuiteStore((s) => s.y);
  const width = useAudioSuiteStore((s) => s.width);
  const height = useAudioSuiteStore((s) => s.height);
  const setPosition = useAudioSuiteStore((s) => s.setPosition);
  const setSize = useAudioSuiteStore((s) => s.setSize);

  const dragRef = useRef<{
    pointerId: number;
    startX: number;
    startY: number;
    origX: number;
    origY: number;
    origW: number;
    origH: number;
  } | null>(null);

  const onPointerDown = useCallback(
    (e: React.PointerEvent<HTMLDivElement>) => {
      e.stopPropagation();
      e.preventDefault();
      e.currentTarget.setPointerCapture(e.pointerId);
      dragRef.current = {
        pointerId: e.pointerId,
        startX: e.clientX,
        startY: e.clientY,
        origX: x, origY: y,
        origW: width, origH: height,
      };
    },
    [x, y, width, height],
  );

  const onPointerMove = useCallback(
    (e: React.PointerEvent<HTMLDivElement>) => {
      const d = dragRef.current;
      if (!d || d.pointerId !== e.pointerId) return;
      const dx = e.clientX - d.startX;
      const dy = e.clientY - d.startY;
      let nX = d.origX;
      let nY = d.origY;
      let nW = d.origW;
      let nH = d.origH;
      if (edge.includes('e')) nW = d.origW + dx;
      if (edge.includes('s')) nH = d.origH + dy;
      if (edge.includes('w')) { nX = d.origX + dx; nW = d.origW - dx; }
      if (edge.includes('n')) { nY = d.origY + dy; nH = d.origH - dy; }

      // Enforce minimums; when shrinking from the left / top, prevent
      // the window's x/y from drifting past the would-be max.
      if (nW < AUDIO_SUITE_WINDOW_MIN_WIDTH) {
        if (edge.includes('w')) nX = d.origX + d.origW - AUDIO_SUITE_WINDOW_MIN_WIDTH;
        nW = AUDIO_SUITE_WINDOW_MIN_WIDTH;
      }
      if (nH < AUDIO_SUITE_WINDOW_MIN_HEIGHT) {
        if (edge.includes('n')) nY = d.origY + d.origH - AUDIO_SUITE_WINDOW_MIN_HEIGHT;
        nH = AUDIO_SUITE_WINDOW_MIN_HEIGHT;
      }

      // Edges that include a dimension update push both position
      // and size in one render; setting them in order so the store
      // sees the combined change as a single React render.
      setPosition(nX, nY);
      setSize(nW, nH);
    },
    [edge, setPosition, setSize],
  );

  const onPointerUp = useCallback(
    (e: React.PointerEvent<HTMLDivElement>) => {
      const d = dragRef.current;
      if (!d || d.pointerId !== e.pointerId) return;
      try { e.currentTarget.releasePointerCapture(e.pointerId); } catch { /* release after capture is best-effort */ }
      dragRef.current = null;
    },
    [],
  );

  return (
    <div
      data-no-drag
      onPointerDown={onPointerDown}
      onPointerMove={onPointerMove}
      onPointerUp={onPointerUp}
      onPointerCancel={onPointerUp}
      style={handleStyleFor(edge)}
    />
  );
}

const RESIZE_EDGES: ResizeEdge[] = ['n', 's', 'e', 'w', 'ne', 'nw', 'se', 'sw'];

/**
 * Short display name for a chain tile, derived from the plugin ID.
 * Recognised v1/v2 plugins get a hand-tuned label; others fall back
 * to the panel title or the trailing segment of the plugin ID.
 */
function shortLabelFor(pluginId: string, panelTitle: string): string {
  switch (pluginId) {
    case 'com.openhpsdr.zeus.samples.gate':       return 'GATE';
    case 'com.openhpsdr.zeus.samples.downexp':    return 'D-EXP';
    case 'com.openhpsdr.zeus.samples.tube':       return 'TUBE';
    case 'com.openhpsdr.zeus.samples.eq':         return 'EQ';
    case 'com.openhpsdr.zeus.samples.compressor': return 'COMP';
    case 'com.openhpsdr.zeus.samples.exciter':    return 'EXCITER';
    case 'com.openhpsdr.zeus.samples.bass':       return 'BASS';
    case 'com.openhpsdr.zeus.samples.reverb':     return 'REVERB';
    default: {
      if (panelTitle && panelTitle.length > 0) return panelTitle.toUpperCase();
      const seg = pluginId.split('.').pop() ?? pluginId;
      return seg.toUpperCase().slice(0, 8);
    }
  }
}

/**
 * Sort the chain panels into the canonical order from the store.
 * Plugins present locally but missing from the canonical order
 * (e.g. server hasn't pushed an update yet) sort to the end in
 * panel-id order for determinism.
 */
function sortChainPanels(
  panels: RegisteredPluginPanel[],
  canonicalOrder: string[],
): RegisteredPluginPanel[] {
  const orderIndex = new Map<string, number>();
  canonicalOrder.forEach((id, i) => orderIndex.set(id, i));
  return [...panels].sort((a, b) => {
    const ia = orderIndex.get(a.pluginId) ?? Number.POSITIVE_INFINITY;
    const ib = orderIndex.get(b.pluginId) ?? Number.POSITIVE_INFINITY;
    if (ia !== ib) return ia - ib;
    return a.panelId.localeCompare(b.panelId);
  });
}

/** Six-dot drag-handle glyph — the universal "grab to reorder" cue. */
function DragHandleIcon() {
  return (
    <svg width="8" height="14" viewBox="0 0 8 14" aria-hidden focusable="false">
      <g fill="currentColor">
        <circle cx="2" cy="2" r="1.2" />
        <circle cx="6" cy="2" r="1.2" />
        <circle cx="2" cy="7" r="1.2" />
        <circle cx="6" cy="7" r="1.2" />
        <circle cx="2" cy="12" r="1.2" />
        <circle cx="6" cy="12" r="1.2" />
      </g>
    </svg>
  );
}

interface ChainChipProps {
  panel: RegisteredPluginPanel;
  index: number;
  selected: boolean;
  isDragTarget: boolean;
  isDragSource: boolean;
  onSelect(): void;
  onRemove(): void;
  onHandleDown(): void;
  onDragStart(e: React.DragEvent): void;
  onDragOver(e: React.DragEvent): void;
  onDragLeave(): void;
  onDrop(e: React.DragEvent): void;
  onDragEnd(): void;
}

/**
 * One chain chip — a compact, reorderable tab for a plugin in the
 * signal chain. Clicking the chip loads that plugin into the shared
 * detail pane below; exactly one chip is "selected" at a time, so the
 * rack never grows into a tall stack of inline panels. Drag the chip's
 * handle to reorder. A VST chip carries a small VST tag — its real UI
 * is a native window opened from the detail pane, not inline HTML.
 */
function ChainChip({
  panel,
  index,
  selected,
  isDragTarget,
  isDragSource,
  onSelect,
  onRemove,
  onHandleDown,
  onDragStart,
  onDragOver,
  onDragLeave,
  onDrop,
  onDragEnd,
}: ChainChipProps) {
  const isVst = panel.editorBacked === true;
  const accented = selected || isDragTarget;
  return (
    <div
      draggable
      onDragStart={onDragStart}
      onDragOver={onDragOver}
      onDragLeave={onDragLeave}
      onDrop={onDrop}
      onDragEnd={onDragEnd}
      onClick={onSelect}
      data-plugin-id={panel.pluginId}
      title={panel.pluginId}
      style={{
        display: 'inline-flex',
        alignItems: 'center',
        gap: 7,
        padding: '5px 7px',
        borderRadius: 5,
        border: '1px solid ' + (accented ? 'var(--accent)' : 'var(--line)'),
        background: selected ? 'var(--accent-soft)' : 'var(--bg-2)',
        boxShadow: isDragTarget ? '0 0 0 1px var(--accent)' : 'none',
        opacity: isDragSource ? 0.4 : 1,
        cursor: 'pointer',
        userSelect: 'none',
        whiteSpace: 'nowrap',
      }}
    >
      {/* Drag handle — the only place a reorder drag may start. */}
      <span
        onMouseDown={onHandleDown}
        onClick={(e) => e.stopPropagation()}
        title="Drag to reorder"
        style={{
          display: 'inline-flex',
          alignItems: 'center',
          color: 'var(--fg-3)',
          cursor: 'grab',
        }}
      >
        <DragHandleIcon />
      </span>

      <span
        style={{
          fontSize: 9.5,
          fontWeight: 600,
          fontFamily: 'var(--font-mono, JetBrains Mono, monospace)',
          color: 'var(--fg-3)',
        }}
      >
        {String(index + 1).padStart(2, '0')}
      </span>

      <span
        style={{
          fontSize: 11.5,
          fontWeight: 600,
          color: selected ? 'var(--fg-0)' : 'var(--fg-1)',
        }}
      >
        {panel.title || shortLabelFor(panel.pluginId, panel.title)}
      </span>

      {isVst && (
        <span
          style={{
            fontSize: 8,
            fontWeight: 700,
            letterSpacing: 0.8,
            color: 'var(--fg-3)',
            border: '1px solid var(--line)',
            borderRadius: 3,
            padding: '0 3px',
          }}
        >
          VST
        </span>
      )}

      {/* Remove from chain = park (non-destructive). Stops it
          processing and moves it to the sidebar's Available list;
          the plugin stays installed. */}
      <button
        type="button"
        onClick={(e) => { e.stopPropagation(); onRemove(); }}
        aria-label={`Remove ${panel.title} from chain`}
        title="Remove from chain (keeps it installed)"
        style={{
          display: 'inline-flex',
          alignItems: 'center',
          justifyContent: 'center',
          width: 16,
          height: 16,
          borderRadius: 3,
          border: '1px solid var(--line)',
          background: 'var(--bg-1)',
          color: 'var(--fg-3)',
          cursor: 'pointer',
          fontSize: 11,
          lineHeight: 1,
          fontFamily: 'inherit',
          padding: 0,
        }}
      >
        ×
      </button>
    </div>
  );
}

/** Custom DnD MIME so a sidebar "add" drag is distinct from a
 *  rack-card reorder drag (which carries text/plain index). */
const PARK_DRAG_MIME = 'application/x-zeus-park-id';

interface PluginSidebarProps {
  collapsed: boolean;
  onToggle(): void;
  parked: RegisteredPluginPanel[];
  onAdd(pluginId: string): void;
  onRemove(pluginId: string): void;
  onParkedDragStart(pluginId: string): (e: React.DragEvent) => void;
  onParkedDragEnd(): void;
  onScanDirectory(): void;
  scanning: boolean;
  /** Embedded (in-page) mode flows at natural height, so the browser
   *  sticks to the top of the scrolling settings pane instead of
   *  filling a fixed-height window. */
  embedded: boolean;
}

/**
 * Plugin browser sidebar — lists the "Available" (parked) plugins not
 * currently in the chain. Add (+) on an available plugin slots it into
 * the rack; available plugins are also draggable onto the rack to add
 * them. The active chain itself is shown in the rack, so it isn't
 * duplicated here. Collapses to a thin labelled strip to reclaim width.
 */
function PluginSidebar({
  collapsed,
  onToggle,
  parked,
  onAdd,
  onRemove,
  onParkedDragStart,
  onParkedDragEnd,
  onScanDirectory,
  scanning,
  embedded,
}: PluginSidebarProps) {
  // In flow (embedded) mode the host has no bounded height, so the
  // browser sticks to the top of the scroll viewport and caps its own
  // height; in the floating window it simply fills the panel.
  const stickyStyle: React.CSSProperties = embedded
    ? {
        position: 'sticky',
        top: 0,
        alignSelf: 'flex-start',
        maxHeight: 'calc(100vh - 160px)',
      }
    : {};

  if (collapsed) {
    return (
      <div
        style={{
          width: 28,
          flex: '0 0 28px',
          background: 'var(--bg-1)',
          borderRight: '1px solid var(--line)',
          display: 'flex',
          flexDirection: 'column',
          alignItems: 'center',
          paddingTop: 8,
          gap: 8,
          ...stickyStyle,
        }}
      >
        <button
          type="button"
          onClick={onToggle}
          title="Show plugin browser"
          aria-label="Show plugin browser"
          style={{
            width: 18,
            height: 18,
            borderRadius: 3,
            border: '1px solid var(--line)',
            background: 'var(--bg-2)',
            color: 'var(--fg-2)',
            cursor: 'pointer',
            fontSize: 11,
            lineHeight: 1,
            padding: 0,
          }}
        >
          ›
        </button>
        <span
          style={{
            writingMode: 'vertical-rl',
            transform: 'rotate(180deg)',
            color: 'var(--fg-3)',
            fontSize: 9,
            letterSpacing: 1.4,
            textTransform: 'uppercase',
            userSelect: 'none',
          }}
        >
          Plugins
        </span>
      </div>
    );
  }

  const row = (panel: RegisteredPluginPanel, inChain: boolean) => (
    <div
      key={panel.pluginId}
      draggable={!inChain}
      onDragStart={!inChain ? onParkedDragStart(panel.pluginId) : undefined}
      onDragEnd={!inChain ? onParkedDragEnd : undefined}
      title={
        inChain
          ? `${panel.pluginId} — in chain`
          : `${panel.pluginId} — drag into the rack or click + to add`
      }
      style={{
        display: 'flex',
        alignItems: 'center',
        gap: 6,
        padding: '4px 6px',
        borderRadius: 3,
        background: 'var(--bg-2)',
        border: '1px solid var(--line)',
        cursor: inChain ? 'default' : 'grab',
      }}
    >
      <span
        style={{
          flex: 1,
          minWidth: 0,
          fontSize: 11,
          color: 'var(--fg-1)',
          overflow: 'hidden',
          textOverflow: 'ellipsis',
          whiteSpace: 'nowrap',
        }}
      >
        {panel.title || shortLabelFor(panel.pluginId, panel.title)}
      </span>
      <button
        type="button"
        onClick={() => (inChain ? onRemove(panel.pluginId) : onAdd(panel.pluginId))}
        aria-label={
          inChain ? `Remove ${panel.title}` : `Add ${panel.title} to chain`
        }
        title={inChain ? 'Remove from chain' : 'Add to chain'}
        style={{
          width: 18,
          height: 18,
          borderRadius: 3,
          border: '1px solid ' + (inChain ? 'var(--line)' : 'var(--accent)'),
          background: inChain ? 'var(--bg-1)' : 'var(--accent)',
          color: inChain ? 'var(--fg-2)' : 'var(--fg-0)',
          cursor: 'pointer',
          fontSize: 12,
          lineHeight: 1,
          padding: 0,
          flex: '0 0 auto',
        }}
      >
        {inChain ? '−' : '+'}
      </button>
    </div>
  );

  return (
    <div
      style={{
        width: 200,
        flex: '0 0 200px',
        background: 'var(--bg-1)',
        borderRight: '1px solid var(--line)',
        display: 'flex',
        flexDirection: 'column',
        overflow: 'hidden',
        ...stickyStyle,
      }}
    >
      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: 6,
          padding: '9px 10px',
          borderBottom: '1px solid var(--line)',
        }}
      >
        <span
          style={{
            flex: 1,
            color: 'var(--fg-3)',
            fontSize: 10,
            fontWeight: 600,
            letterSpacing: 1.2,
            textTransform: 'uppercase',
          }}
        >
          Plugins
        </span>
        <button
          type="button"
          onClick={onToggle}
          title="Hide plugin browser"
          aria-label="Hide plugin browser"
          style={{
            width: 18,
            height: 18,
            borderRadius: 3,
            border: '1px solid var(--line)',
            background: 'var(--bg-2)',
            color: 'var(--fg-2)',
            cursor: 'pointer',
            fontSize: 11,
            lineHeight: 1,
            padding: 0,
          }}
        >
          ‹
        </button>
      </div>

      <div
        style={{
          flex: 1,
          overflowY: 'auto',
          padding: 8,
          display: 'flex',
          flexDirection: 'column',
          gap: 10,
        }}
      >
        <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
          <span style={groupLabelStyle}>Available · {parked.length}</span>
          {parked.length === 0 && (
            <span style={emptyHintStyle}>All installed plugins are in the chain.</span>
          )}
          {parked.map((p) => row(p, false))}
        </div>
      </div>

      {/* Footer — register VST3 plugins from a folder. */}
      <div style={{ padding: 8, borderTop: '1px solid var(--line)' }}>
        <button
          type="button"
          onClick={onScanDirectory}
          disabled={scanning}
          title="Scan a folder for VST3 plugins and add them to the rack"
          style={{
            width: '100%',
            padding: '5px 8px',
            borderRadius: 3,
            border: '1px solid var(--accent)',
            background: 'var(--bg-2)',
            color: 'var(--fg-0)',
            cursor: scanning ? 'progress' : 'pointer',
            opacity: scanning ? 0.6 : 1,
            fontSize: 10,
            fontWeight: 600,
            letterSpacing: 0.6,
            textTransform: 'uppercase',
            fontFamily: 'inherit',
          }}
        >
          {scanning ? 'Scanning…' : '+ Add VST folder'}
        </button>
      </div>
    </div>
  );
}

const groupLabelStyle: React.CSSProperties = {
  color: 'var(--fg-3)',
  fontSize: 9,
  fontWeight: 600,
  letterSpacing: 1,
  textTransform: 'uppercase',
  padding: '0 2px',
};

const emptyHintStyle: React.CSSProperties = {
  color: 'var(--fg-3)',
  fontSize: 10,
  fontStyle: 'italic',
  padding: '0 2px',
};

function profileBtnStyle(disabled: boolean): React.CSSProperties {
  return {
    padding: '3px 10px',
    borderRadius: 3,
    border: '1px solid var(--line)',
    background: 'var(--bg-2)',
    color: 'var(--fg-2)',
    cursor: disabled ? 'not-allowed' : 'pointer',
    opacity: disabled ? 0.5 : 1,
    fontSize: 10,
    fontWeight: 600,
    letterSpacing: 0.6,
    textTransform: 'uppercase',
    fontFamily: 'inherit',
    flex: '0 0 auto',
  };
}

export function AudioSuiteWindow({ embedded = false }: { embedded?: boolean } = {}) {
  const isOpen = useAudioSuiteStore((s) => s.isOpen);
  const close = useAudioSuiteStore((s) => s.close);
  const x = useAudioSuiteStore((s) => s.x);
  const y = useAudioSuiteStore((s) => s.y);
  const width = useAudioSuiteStore((s) => s.width);
  const height = useAudioSuiteStore((s) => s.height);
  const setPosition = useAudioSuiteStore((s) => s.setPosition);
  const setDragging = useAudioSuiteStore((s) => s.setDragging);
  const chainOrder = useAudioSuiteStore((s) => s.chainOrder);
  const reorderChain = useAudioSuiteStore((s) => s.reorderChain);
  const selectedChainId = useAudioSuiteStore((s) => s.selectedChainId);
  const setSelectedChainId = useAudioSuiteStore((s) => s.setSelectedChainId);
  const sidebarCollapsed = useAudioSuiteStore((s) => s.sidebarCollapsed);
  const toggleSidebar = useAudioSuiteStore((s) => s.toggleSidebar);
  const setChainMembership = useAudioSuiteStore((s) => s.setChainMembership);
  const profiles = useAudioSuiteStore((s) => s.profiles);
  const loadProfiles = useAudioSuiteStore((s) => s.loadProfiles);
  const saveProfile = useAudioSuiteStore((s) => s.saveProfile);
  const applyProfile = useAudioSuiteStore((s) => s.applyProfile);
  const deleteProfile = useAudioSuiteStore((s) => s.deleteProfile);
  const scanVstDirectory = useAudioSuiteStore((s) => s.scanVstDirectory);
  const loadChainOrderFromServer = useAudioSuiteStore(
    (s) => s.loadChainOrderFromServer,
  );
  const auditionSupported = useAudioSuiteStore((s) => s.auditionSupported);
  const auditionEnabled = useAudioSuiteStore((s) => s.auditionEnabled);
  const setAuditionEnabled = useAudioSuiteStore((s) => s.setAuditionEnabled);
  const loadAuditionState = useAudioSuiteStore((s) => s.loadAuditionState);
  const loadMasterBypassFromServer = useAudioSuiteStore(
    (s) => s.loadMasterBypassFromServer,
  );

  const allPanels = usePluginPanels();
  // Every plugin that targets the TX audio chain — installed, whether
  // or not it's currently in the active chain.
  const chainSlotPanels = useMemo(
    () => allPanels.filter((p) => p.slot === CHAIN_SLOT),
    [allPanels],
  );
  // Active rack = the panels whose plugin ID is in the server's active
  // order, sorted by it. Parking removes an ID from chainOrder, so a
  // parked plugin simply falls out of here and into the sidebar.
  const chainOrderSet = useMemo(() => new Set(chainOrder), [chainOrder]);
  const chainPanels = useMemo(
    () =>
      sortChainPanels(
        chainSlotPanels.filter((p) => chainOrderSet.has(p.pluginId)),
        chainOrder,
      ),
    [chainSlotPanels, chainOrderSet, chainOrder],
  );
  // Available (parked) = chain-slot plugins NOT in the active order.
  // These show in the sidebar's "Available" group, ready to add back.
  const parkedPanels = useMemo(
    () =>
      chainSlotPanels
        .filter((p) => !chainOrderSet.has(p.pluginId))
        .sort((a, b) => a.title.localeCompare(b.title)),
    [chainSlotPanels, chainOrderSet],
  );

  // Fetch server-side state on first open. Subsequent updates arrive
  // via the AudioChainOrder + AudioMasterBypass WS broadcast handlers
  // in ws-client.ts.
  useEffect(() => {
    if (!embedded && !isOpen) return;
    loadChainOrderFromServer();
    loadAuditionState();
    loadMasterBypassFromServer();
    loadProfiles();
  }, [
    embedded,
    isOpen,
    loadChainOrderFromServer,
    loadAuditionState,
    loadMasterBypassFromServer,
    loadProfiles,
  ]);

  // Escape closes the window — standard modal/popup keyboard
  // affordance. Listener only attached while the window is open
  // so it doesn't fight other Escape handlers (e.g. closing the
  // panadapter cursor crosshair) when the suite is hidden.
  useEffect(() => {
    if (!isOpen) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') close();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [isOpen, close]);

  // Viewport-resize clamp — if the operator shrinks their browser
  // window after the suite is positioned, the suite's stored x/y
  // could end up off-screen and unreachable (no header to grab,
  // no resize handle to grab either). Re-apply the same clamp
  // rules used during drag whenever the viewport size changes.
  useEffect(() => {
    if (!isOpen) return;
    const onResize = () => {
      const minX = -width + 80;
      const minY = 64;
      const maxX = window.innerWidth - 80;
      const maxY = window.innerHeight - 40;
      const nextX = Math.min(maxX, Math.max(minX, x));
      const nextY = Math.min(maxY, Math.max(minY, y));
      if (nextX !== x || nextY !== y) setPosition(nextX, nextY);
    };
    window.addEventListener('resize', onResize);
    return () => window.removeEventListener('resize', onResize);
  }, [isOpen, x, y, width, setPosition]);

  // --- Window dragging via Pointer Events --------------------------
  const dragStateRef = useRef<{
    pointerId: number;
    startX: number;
    startY: number;
    offsetX: number;
    offsetY: number;
  } | null>(null);

  const onHeaderPointerDown = useCallback(
    (e: React.PointerEvent<HTMLDivElement>) => {
      // Ignore drags initiated on header controls (close button etc).
      const target = e.target as HTMLElement;
      if (target.closest('[data-no-drag]')) return;
      e.currentTarget.setPointerCapture(e.pointerId);
      dragStateRef.current = {
        pointerId: e.pointerId,
        startX: e.clientX,
        startY: e.clientY,
        offsetX: x,
        offsetY: y,
      };
      setDragging(true);
    },
    [x, y, setDragging],
  );

  const onHeaderPointerMove = useCallback(
    (e: React.PointerEvent<HTMLDivElement>) => {
      const ds = dragStateRef.current;
      if (!ds || ds.pointerId !== e.pointerId) return;
      const dx = e.clientX - ds.startX;
      const dy = e.clientY - ds.startY;
      // Clamp so the header can't drag off-screen (always leave at
      // least 80px visible on every edge so the operator can grab it).
      // minY = 64 keeps the header from sliding under the 60-px Zeus
      // topbar — the operator must always have somewhere to grab to
      // pull the window back down. zIndex on the window root puts us
      // above the topbar visually anyway, but enforcing the clamp
      // avoids covering the radio chrome unnecessarily.
      const minX = -width + 80;
      const minY = 64;
      const maxX = window.innerWidth - 80;
      const maxY = window.innerHeight - 40;
      const nextX = Math.min(maxX, Math.max(minX, ds.offsetX + dx));
      const nextY = Math.min(maxY, Math.max(minY, ds.offsetY + dy));
      setPosition(nextX, nextY);
    },
    [width, setPosition],
  );

  const onHeaderPointerUp = useCallback(
    (e: React.PointerEvent<HTMLDivElement>) => {
      const ds = dragStateRef.current;
      if (!ds || ds.pointerId !== e.pointerId) return;
      try { e.currentTarget.releasePointerCapture(e.pointerId); } catch { /* release after capture is best-effort */ }
      dragStateRef.current = null;
      setDragging(false);
    },
    [setDragging],
  );

  // --- Rack-card drag-and-drop -------------------------------------
  // Same chain-reorder as the tile strip, but applied to the stacked
  // slot cards. Drag is gated to begin only from each card's handle
  // (cardHandleRef) so dragging a slider inside a plugin body never
  // tears the slot out of the rack. Separate drag state from the tile
  // strip so the two never cross-highlight.
  const [cardDragOver, setCardDragOver] = useState<number | null>(null);
  const [cardDragFrom, setCardDragFrom] = useState<number | null>(null);
  const cardDragFromRef = useRef<number | null>(null);
  const cardHandleRef = useRef<boolean>(false);

  const onCardHandleDown = () => {
    cardHandleRef.current = true;
  };

  const onCardDragStart = (idx: number) => (e: React.DragEvent) => {
    if (!cardHandleRef.current) {
      // Drag did not originate on the handle — cancel it so the
      // operator can still select text / drag sliders in the body.
      e.preventDefault();
      return;
    }
    cardDragFromRef.current = idx;
    setCardDragFrom(idx);
    e.dataTransfer.effectAllowed = 'move';
    e.dataTransfer.setData('text/plain', String(idx));
  };

  const onCardDragOver = (idx: number) => (e: React.DragEvent) => {
    if (cardDragFromRef.current === null) return; // ignore foreign drags
    e.preventDefault();
    e.dataTransfer.dropEffect = 'move';
    if (cardDragOver !== idx) setCardDragOver(idx);
  };

  const onCardDragLeave = () => setCardDragOver(null);

  const onCardDrop = (idx: number) => (e: React.DragEvent) => {
    e.preventDefault();
    const from = cardDragFromRef.current;
    setCardDragOver(null);
    setCardDragFrom(null);
    cardDragFromRef.current = null;
    cardHandleRef.current = false;
    if (from === null || from === idx) return;
    void reorderChain(from, idx);
  };

  const onCardDragEnd = () => {
    setCardDragOver(null);
    setCardDragFrom(null);
    cardDragFromRef.current = null;
    cardHandleRef.current = false;
  };

  // --- Profiles ----------------------------------------------------
  const [selectedProfile, setSelectedProfile] = useState('');
  // Drop a stale selection if the profile was deleted elsewhere.
  useEffect(() => {
    if (selectedProfile && !profiles.some((p) => p.name === selectedProfile)) {
      setSelectedProfile('');
    }
  }, [profiles, selectedProfile]);

  const onSelectProfile = (name: string) => {
    setSelectedProfile(name);
    if (name) void applyProfile(name);
  };
  const onSaveProfile = () => {
    const suggested = selectedProfile || '';
    const name = window.prompt(
      'Save current chain as profile (name):',
      suggested,
    );
    if (name && name.trim()) {
      const trimmed = name.trim();
      setSelectedProfile(trimmed);
      void saveProfile(trimmed);
    }
  };
  const onDeleteProfile = () => {
    if (!selectedProfile) return;
    if (window.confirm(`Delete profile "${selectedProfile}"?`)) {
      void deleteProfile(selectedProfile);
      setSelectedProfile('');
    }
  };

  // --- Add VST directory (scan) ------------------------------------
  const [scanning, setScanning] = useState(false);
  const onScanVstDirectory = async () => {
    const dir = window.prompt(
      'Scan a folder for VST3 plugins — each .vst3 is registered into the rack:',
      'C:\\VST PLUGINS',
    );
    if (!dir || !dir.trim()) return;
    setScanning(true);
    const result = await scanVstDirectory(dir.trim());
    setScanning(false);
    if (!result.ok) {
      window.alert(`VST scan failed:\n${result.error ?? 'unknown error'}`);
      return;
    }
    const lines = [
      `Scanned: ${result.directory}`,
      `Registered: ${result.registered.length}`,
      `Already present: ${result.skipped.length}`,
      `Failed: ${result.errors.length}`,
    ];
    if (result.errors.length > 0) {
      lines.push('', ...result.errors.slice(0, 6).map((e) => `• ${e.message}`));
    }
    window.alert(lines.join('\n'));
  };

  // --- Sidebar → rack drag (add a parked plugin) -------------------
  const [rackDropActive, setRackDropActive] = useState(false);

  const onParkedDragStart = (pluginId: string) => (e: React.DragEvent) => {
    e.dataTransfer.effectAllowed = 'copy';
    e.dataTransfer.setData(PARK_DRAG_MIME, pluginId);
  };
  const onParkedDragEnd = () => setRackDropActive(false);

  const onRackDragOver = (e: React.DragEvent) => {
    // Only react to sidebar "add" drags, not card reorder drags.
    if (!e.dataTransfer.types.includes(PARK_DRAG_MIME)) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = 'copy';
    if (!rackDropActive) setRackDropActive(true);
  };
  const onRackDragLeave = () => setRackDropActive(false);
  const onRackDrop = (e: React.DragEvent) => {
    const id = e.dataTransfer.getData(PARK_DRAG_MIME);
    setRackDropActive(false);
    if (!id) return;
    e.preventDefault();
    void setChainMembership(id, true); // un-park → add to chain
  };

  const chainPluginIds = useMemo(
    () => chainPanels.map((p) => p.pluginId),
    [chainPanels],
  );
  // Chips+detail: which chip's plugin is shown in the detail pane. The
  // stored selection wins while it's still in the chain; otherwise fall
  // back to the first chip (so parking the selected plugin, or a fresh
  // load, always lands on something valid without a sync effect).
  const effectiveSelectedId = useMemo(() => {
    if (selectedChainId && chainPluginIds.includes(selectedChainId)) {
      return selectedChainId;
    }
    return chainPluginIds[0] ?? null;
  }, [selectedChainId, chainPluginIds]);
  const selectedPanel = useMemo(
    () => chainPanels.find((p) => p.pluginId === effectiveSelectedId) ?? null,
    [chainPanels, effectiveSelectedId],
  );
  const SelectedComponent = selectedPanel?.component ?? null;

  // Embedded mode (rendered inline inside TX Audio Tools) is always
  // visible; the floating window only renders when opened.
  if (!embedded && !isOpen) return null;

  // Outer container differs by mode: a fixed, draggable, resizable
  // floating window vs. a normal-flow block that fills the host panel's
  // width (so plugin GUIs get real room instead of being clipped).
  const containerStyle: React.CSSProperties = embedded
    ? {
        // Flow at natural height inside the settings pane (which is the
        // scroll container). The old fixed 76vh + inner scroll nested two
        // scroll regions and clipped the lower rack slots ("chopped off");
        // flowing lets the pane scroll the whole rack as one surface.
        position: 'relative',
        width: '100%',
        display: 'flex',
        flexDirection: 'column',
        background: 'linear-gradient(180deg, var(--panel-top), var(--panel-bot))',
        border: '1px solid var(--line)',
        borderRadius: 8,
        boxShadow: 'inset 0 1px 0 rgba(255, 255, 255, 0.04)',
        color: 'var(--fg-0)',
        fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)',
        overflow: 'visible',
      }
    : {
        position: 'fixed',
        left: x,
        // Render-time clamp on top so a persisted y < 64 (e.g. from a
        // session before the topbar-clearance fix) doesn't ship the
        // window under the topbar. The drag clamp prevents new drags
        // from going there; this self-heals stuck stored positions on
        // first render after upgrade.
        top: Math.max(64, y),
        width,
        height,
        // Above the Zeus topbar (zIndex 300) so the operator's window
        // is never hidden by app chrome. Below modal dialogs
        // (AddPanelModal etc at zIndex 10000) so critical overlays
        // still win.
        zIndex: 400,
        display: 'flex',
        flexDirection: 'column',
        background: 'linear-gradient(180deg, var(--panel-top), var(--panel-bot))',
        border: '1px solid var(--line)',
        borderRadius: 8,
        boxShadow: '0 12px 32px rgba(0, 0, 0, 0.45), inset 0 1px 0 rgba(255, 255, 255, 0.04)',
        color: 'var(--fg-0)',
        fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)',
        overflow: 'hidden',
      };

  return (
    <div role={embedded ? 'group' : 'dialog'} aria-label="Audio Suite" style={containerStyle}>
      {/* Resize handles — floating mode only (the embedded host resizes
          with its container). */}
      {!embedded && RESIZE_EDGES.map((e) => <ResizeHandle key={e} edge={e} />)}

      {/* Header — drag handle. Brass-plate styling per the v3 Lifted
          Dark spec ([[project_audio_chain_visual_direction]]). */}
      <div
        onPointerDown={embedded ? undefined : onHeaderPointerDown}
        onPointerMove={embedded ? undefined : onHeaderPointerMove}
        onPointerUp={embedded ? undefined : onHeaderPointerUp}
        onPointerCancel={embedded ? undefined : onHeaderPointerUp}
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: 12,
          padding: '8px 12px',
          background: 'linear-gradient(180deg, var(--panel-top), var(--panel-bot))',
          borderBottom: '1px solid var(--line)',
          boxShadow: 'inset 0 2px 0 var(--power), inset 0 3px 8px rgba(255, 201, 58, 0.08)',
          cursor: embedded ? 'default' : 'grab',
          userSelect: 'none',
        }}
      >
        <span
          style={{
            color: 'var(--fg-1)',
            fontSize: 12,
            fontWeight: 600,
            letterSpacing: 1.4,
            textTransform: 'uppercase',
          }}
        >
          Audio Suite
        </span>

        {/* Audition toggle. Disabled when host mode is server (audition
            sink is a no-op in browser mode v1 per Phase 1 ADR). */}
        <button
          type="button"
          data-no-drag
          disabled={!auditionSupported}
          onClick={() => setAuditionEnabled(!auditionEnabled)}
          title={
            auditionSupported
              ? auditionEnabled
                ? 'Audition is ON — chain output is mixed into your RX playback'
                : 'Audition is OFF — click to hear the chain on your headphones'
              : 'Audition is desktop-only in this version'
          }
          style={{
            marginLeft: 'auto',
            padding: '4px 12px',
            borderRadius: 4,
            border: '1px solid ' + (auditionEnabled ? 'var(--tx)' : 'var(--line)'),
            background: auditionEnabled ? 'var(--tx)' : 'var(--bg-2)',
            color: auditionEnabled ? 'var(--fg-0)' : 'var(--fg-2)',
            cursor: auditionSupported ? 'pointer' : 'not-allowed',
            opacity: auditionSupported ? 1 : 0.5,
            fontSize: 11,
            fontWeight: 600,
            letterSpacing: 1,
            textTransform: 'uppercase',
            fontFamily: 'inherit',
          }}
        >
          Audition {auditionEnabled ? 'ON' : 'OFF'}
        </button>

        {!embedded && (
          <button
            type="button"
            data-no-drag
            onClick={close}
            aria-label="Close Audio Suite window"
            title="Close"
            style={{
              padding: '2px 10px',
              borderRadius: 4,
              border: '1px solid var(--line)',
              background: 'var(--bg-2)',
              color: 'var(--fg-2)',
              cursor: 'pointer',
              fontSize: 14,
              fontWeight: 600,
              fontFamily: 'inherit',
              lineHeight: 1,
            }}
          >
            ×
          </button>
        )}
      </div>

      {/* Profiles bar — named snapshots of the chain config. Choosing
          one applies it; Save snapshots the current chain. */}
      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: 6,
          padding: '6px 12px',
          background: 'var(--bg-1)',
          borderBottom: '1px solid var(--line)',
        }}
      >
        <span
          style={{
            color: 'var(--fg-3)',
            fontSize: 10,
            fontWeight: 600,
            letterSpacing: 1,
            textTransform: 'uppercase',
          }}
        >
          Profile
        </span>
        <select
          value={selectedProfile}
          onChange={(e) => onSelectProfile(e.target.value)}
          title="Apply a saved profile"
          style={{
            flex: 1,
            minWidth: 0,
            padding: '3px 6px',
            borderRadius: 3,
            border: '1px solid var(--line)',
            background: 'var(--bg-2)',
            color: 'var(--fg-1)',
            fontSize: 11,
            fontFamily: 'inherit',
          }}
        >
          <option value="">
            {profiles.length ? 'Select profile…' : 'No profiles saved'}
          </option>
          {profiles.map((p) => (
            <option key={p.name} value={p.name}>
              {p.name}
            </option>
          ))}
        </select>
        <button
          type="button"
          onClick={onSaveProfile}
          title="Save the current chain as a profile"
          style={profileBtnStyle(false)}
        >
          Save
        </button>
        <button
          type="button"
          onClick={onDeleteProfile}
          disabled={!selectedProfile}
          title="Delete the selected profile"
          style={profileBtnStyle(!selectedProfile)}
        >
          Delete
        </button>
      </div>

      {/* Body — plugin browser sidebar + main rack column. */}
      <div
        style={{
          display: 'flex',
          flex: embedded ? 'none' : 1,
          minHeight: 0,
          alignItems: 'stretch',
        }}
      >
        <PluginSidebar
          collapsed={sidebarCollapsed}
          onToggle={toggleSidebar}
          parked={parkedPanels}
          onAdd={(id) => void setChainMembership(id, true)}
          onRemove={(id) => void setChainMembership(id, false)}
          onParkedDragStart={onParkedDragStart}
          onParkedDragEnd={onParkedDragEnd}
          onScanDirectory={() => void onScanVstDirectory()}
          scanning={scanning}
          embedded={embedded}
        />

        <div
          style={{
            display: 'flex',
            flexDirection: 'column',
            flex: 1,
            minWidth: 0,
          }}
        >
        {/* Centred rack column. Caps the working width so plugin editors
            read as rack modules instead of sprawling across the full
            settings pane; chrome strips (meters / toolbar) align to the
            same column so the whole stack shares one tidy gutter. */}
        <div
          style={{
            width: '100%',
            maxWidth: RACK_MAX_WIDTH,
            margin: '0 auto',
            display: 'flex',
            flexDirection: 'column',
            flex: embedded ? 'none' : 1,
            minHeight: 0,
          }}
        >
      {/* Chain IN / OUT signal meters (poll while window open). */}
      <AudioChainMeters />

      {/* Chain chips — the signal chain as a compact, reorderable strip.
          Click a chip to load its plugin into the detail pane below; drag
          a chip's handle to reorder (the chip order IS the signal chain).
          Also a drop zone: dragging a plugin from the sidebar's Available
          list here un-parks (adds) it. */}
      <div
        onDragOver={onRackDragOver}
        onDragLeave={onRackDragLeave}
        onDrop={onRackDrop}
        style={{
          display: 'flex',
          alignItems: 'center',
          flexWrap: 'wrap',
          gap: 7,
          padding: '8px 12px',
          background: 'var(--bg-1)',
          borderBottom: '1px solid var(--line)',
          outline: rackDropActive ? '2px dashed var(--accent)' : 'none',
          outlineOffset: -3,
        }}
      >
        <span
          style={{
            color: 'var(--fg-3)',
            fontSize: 10,
            fontWeight: 600,
            letterSpacing: 1.2,
            textTransform: 'uppercase',
            marginRight: 2,
          }}
        >
          Chain
        </span>
        {chainPanels.length === 0 && (
          <span style={{ color: 'var(--fg-3)', fontSize: 11, fontStyle: 'italic' }}>
            {parkedPanels.length > 0
              ? 'Empty — add a plugin from the Available list, or drag it here.'
              : 'No audio plugins installed — use Download Audio Suite on the TX Audio Tools panel.'}
          </span>
        )}
        {chainPanels.map((panel, idx) => (
          <ChainChip
            key={`${panel.pluginId}::${panel.panelId}`}
            panel={panel}
            index={idx}
            selected={panel.pluginId === effectiveSelectedId}
            isDragTarget={cardDragOver === idx}
            isDragSource={cardDragFrom === idx}
            onSelect={() => setSelectedChainId(panel.pluginId)}
            onRemove={() => void setChainMembership(panel.pluginId, false)}
            onHandleDown={onCardHandleDown}
            onDragStart={onCardDragStart(idx)}
            onDragOver={onCardDragOver(idx)}
            onDragLeave={onCardDragLeave}
            onDrop={onCardDrop(idx)}
            onDragEnd={onCardDragEnd}
          />
        ))}
      </div>

      {/* Detail pane — the selected chip's plugin UI. Exactly one plugin
          is mounted at a time (no tall stack of inline panels). A VST
          shows its identity + Open Editor here; its real GUI is a
          separate native window opened by the bridge. */}
      <div
        style={{
          // Floating window: bounded, scrolls internally. Embedded: flows
          // at natural height and lets the settings pane scroll it.
          flex: embedded ? 'none' : 1,
          overflowY: embedded ? 'visible' : 'auto',
          padding: '14px 16px',
          minHeight: embedded ? 160 : 0,
        }}
      >
        {SelectedComponent ? (
          <SelectedComponent />
        ) : (
          <span
            style={{ color: 'var(--fg-3)', fontSize: 12, fontStyle: 'italic' }}
          >
            {chainPanels.length > 0
              ? 'Select a plugin above to configure it.'
              : 'Nothing to configure yet.'}
          </span>
        )}
      </div>
        </div>
        </div>
      </div>
    </div>
  );
}
