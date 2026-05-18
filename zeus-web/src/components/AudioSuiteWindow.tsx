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
import { useAudioSuiteStore } from '../state/audio-suite-store';

const CHAIN_SLOT = 'tx-audio-tools.chain';

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
    default:
      if (panelTitle && panelTitle.length > 0) return panelTitle.toUpperCase();
      const seg = pluginId.split('.').pop() ?? pluginId;
      return seg.toUpperCase().slice(0, 8);
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

export function AudioSuiteWindow() {
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
  const loadChainOrderFromServer = useAudioSuiteStore(
    (s) => s.loadChainOrderFromServer,
  );
  const auditionSupported = useAudioSuiteStore((s) => s.auditionSupported);
  const auditionEnabled = useAudioSuiteStore((s) => s.auditionEnabled);
  const setAuditionEnabled = useAudioSuiteStore((s) => s.setAuditionEnabled);
  const loadAuditionState = useAudioSuiteStore((s) => s.loadAuditionState);

  const allPanels = usePluginPanels();
  const chainPanels = useMemo(
    () => sortChainPanels(
      allPanels.filter((p) => p.slot === CHAIN_SLOT),
      chainOrder,
    ),
    [allPanels, chainOrder],
  );

  // Fetch server-side state on first open. Subsequent updates arrive
  // via the AudioChainOrder WS broadcast handler in ws-client.ts.
  useEffect(() => {
    if (!isOpen) return;
    loadChainOrderFromServer();
    loadAuditionState();
  }, [isOpen, loadChainOrderFromServer, loadAuditionState]);

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
      const minX = -width + 80;
      const minY = 0;
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
      try { e.currentTarget.releasePointerCapture(e.pointerId); } catch {}
      dragStateRef.current = null;
      setDragging(false);
    },
    [setDragging],
  );

  // --- Tile drag-and-drop (HTML5 d&d) -----------------------------
  const [dragOverIndex, setDragOverIndex] = useState<number | null>(null);
  const draggedFromRef = useRef<number | null>(null);

  const onTileDragStart = (idx: number) => (e: React.DragEvent) => {
    draggedFromRef.current = idx;
    e.dataTransfer.effectAllowed = 'move';
    // Some browsers require a payload to start a drag.
    e.dataTransfer.setData('text/plain', String(idx));
  };

  const onTileDragOver = (idx: number) => (e: React.DragEvent) => {
    e.preventDefault();
    e.dataTransfer.dropEffect = 'move';
    if (dragOverIndex !== idx) setDragOverIndex(idx);
  };

  const onTileDragLeave = () => setDragOverIndex(null);

  const onTileDrop = (idx: number) => (e: React.DragEvent) => {
    e.preventDefault();
    const from = draggedFromRef.current;
    setDragOverIndex(null);
    draggedFromRef.current = null;
    if (from === null || from === idx) return;
    void reorderChain(from, idx);
  };

  const onTileDragEnd = () => {
    setDragOverIndex(null);
    draggedFromRef.current = null;
  };

  if (!isOpen) return null;

  return (
    <div
      role="dialog"
      aria-label="Audio Suite"
      style={{
        position: 'fixed',
        left: x,
        top: y,
        width,
        height,
        zIndex: 60,
        display: 'flex',
        flexDirection: 'column',
        background: 'linear-gradient(180deg, var(--panel-top), var(--panel-bot))',
        border: '1px solid var(--line-1)',
        borderRadius: 8,
        boxShadow: '0 12px 32px rgba(0, 0, 0, 0.45), inset 0 1px 0 rgba(255, 255, 255, 0.04)',
        color: 'var(--fg-0)',
        fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)',
        overflow: 'hidden',
      }}
    >
      {/* Header — drag handle. Brass-plate styling per the v3 Lifted
          Dark spec ([[project_audio_chain_visual_direction]]). */}
      <div
        onPointerDown={onHeaderPointerDown}
        onPointerMove={onHeaderPointerMove}
        onPointerUp={onHeaderPointerUp}
        onPointerCancel={onHeaderPointerUp}
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: 12,
          padding: '8px 12px',
          background: 'linear-gradient(180deg, var(--panel-top), var(--panel-bot))',
          borderBottom: '1px solid var(--line-1)',
          boxShadow: 'inset 0 2px 0 var(--power), inset 0 3px 8px rgba(255, 201, 58, 0.08)',
          cursor: 'grab',
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
            border: '1px solid ' + (auditionEnabled ? 'var(--tx)' : 'var(--line-1)'),
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

        <button
          type="button"
          data-no-drag
          onClick={close}
          aria-label="Close Audio Suite window"
          title="Close"
          style={{
            padding: '2px 10px',
            borderRadius: 4,
            border: '1px solid var(--line-1)',
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
      </div>

      {/* Tile strip — drag to reorder. Vanilla HTML5 d&d. */}
      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: 6,
          padding: '8px 12px',
          background: 'var(--bg-1)',
          borderBottom: '1px solid var(--line-1)',
          flexWrap: 'wrap',
          fontSize: 10,
          letterSpacing: 1.2,
          textTransform: 'uppercase',
        }}
      >
        <span style={{ color: 'var(--fg-3)', marginRight: 4, fontWeight: 500 }}>
          Chain
        </span>
        {chainPanels.length === 0 && (
          <span style={{ color: 'var(--fg-3)', fontStyle: 'italic', textTransform: 'none' }}>
            No audio plugins installed — use Download Audio Suite on the TX Audio Tools panel.
          </span>
        )}
        {chainPanels.map((panel, idx) => {
          const label = shortLabelFor(panel.pluginId, panel.title);
          const isDragTarget = dragOverIndex === idx;
          return (
            <div key={panel.pluginId} style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
              {idx > 0 && (
                <span aria-hidden style={{ color: 'var(--fg-3)' }}>›</span>
              )}
              <div
                draggable
                onDragStart={onTileDragStart(idx)}
                onDragOver={onTileDragOver(idx)}
                onDragLeave={onTileDragLeave}
                onDrop={onTileDrop(idx)}
                onDragEnd={onTileDragEnd}
                title={`${panel.pluginId} — drag to reorder`}
                style={{
                  padding: '4px 10px',
                  borderRadius: 3,
                  background: isDragTarget ? 'var(--accent)' : 'var(--bg-2)',
                  border: '1px solid ' + (isDragTarget ? 'var(--accent)' : 'var(--line-1)'),
                  color: isDragTarget ? 'var(--fg-0)' : 'var(--fg-1)',
                  cursor: 'grab',
                  fontSize: 10,
                  fontWeight: 500,
                  userSelect: 'none',
                }}
              >
                {label}
              </div>
            </div>
          );
        })}
      </div>

      {/* Plugin panels stacked vertically in chain order. */}
      <div
        style={{
          flex: 1,
          overflowY: 'auto',
          padding: 12,
          display: 'flex',
          flexDirection: 'column',
          gap: 12,
        }}
      >
        {chainPanels.map((panel) => {
          const Component = panel.component;
          return (
            <div
              key={`${panel.pluginId}::${panel.panelId}`}
              data-plugin-id={panel.pluginId}
            >
              <Component />
            </div>
          );
        })}
      </div>
    </div>
  );
}
