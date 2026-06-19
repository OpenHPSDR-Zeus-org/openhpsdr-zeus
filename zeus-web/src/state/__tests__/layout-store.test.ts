// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

/** @vitest-environment jsdom */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
// Pull in the act-environment + localStorage polyfill side-effects from the
// existing meters test harness before importing the store module.
import '../../components/meters/__tests__/harness';
import { parseLayoutOrDefault, useLayoutStore } from '../layout-store';
import { DEFAULT_WORKSPACE_LAYOUT } from '../../layout/defaultLayout';
import {
  parseWorkspaceLayout,
  EMPTY_WORKSPACE_LAYOUT,
  type WorkspaceLayout,
} from '../../layout/workspace';

describe('layout-store / workspace tile mutators', () => {
  beforeEach(() => {
    // Reset the store to a clean default before each test so addTile /
    // removeTile counts don't leak across cases.
    useLayoutStore.setState({
      radioKey: '',
      layouts: [],
      activeLayoutId: 'default',
      workspace: DEFAULT_WORKSPACE_LAYOUT,
      isLoaded: true,
    });
    // Stub fetch so syncToServer's debounced PUT doesn't try to reach the
    // network during tests.
    (globalThis as unknown as { fetch: typeof fetch }).fetch = vi
      .fn()
      .mockResolvedValue({ ok: true, status: 200, json: async () => ({}) });
  });

  afterEach(() => {
    vi.clearAllTimers();
    vi.useRealTimers();
  });

  it('addTile appends a new tile with a fresh uid', () => {
    const before = useLayoutStore.getState().workspace.tiles.length;
    const uid = useLayoutStore.getState().addTile('cw');
    const after = useLayoutStore.getState().workspace.tiles;
    expect(after.length).toBe(before + 1);
    const tile = after[after.length - 1];
    expect(tile?.panelId).toBe('cw');
    expect(tile?.uid).toBe(uid);
    // Default span for cw is 8×8 per workspace.ts DEFAULT_TILE_SPAN
    // (24×48 grid — 2× the legacy 4×4).
    expect(tile?.w).toBe(8);
    expect(tile?.h).toBe(8);
  });

  it('addTile places the new tile at y = max(existing y+h)', () => {
    // DEFAULT_WORKSPACE_LAYOUT's tallest existing y+h is hero/dsp at
    // y=16+32 / y=38+10 = 48 on the 24×48 grid. So the new tile lands at y=48.
    const uid = useLayoutStore.getState().addTile('cw');
    const tile = useLayoutStore
      .getState()
      .workspace.tiles.find((t) => t.uid === uid);
    expect(tile?.y).toBe(48);
    expect(tile?.x).toBe(0);
  });

  it('addTile allows multi-instance panels (meters) to be added more than once', () => {
    const a = useLayoutStore.getState().addTile('meters');
    const b = useLayoutStore.getState().addTile('meters');
    expect(a).not.toBe(b);
    const meters = useLayoutStore
      .getState()
      .workspace.tiles.filter((t) => t.panelId === 'meters');
    expect(meters.length).toBe(2);
  });

  it('removeTile drops the tile by uid', () => {
    const uid = useLayoutStore.getState().addTile('cw');
    expect(useLayoutStore.getState().workspace.tiles.some((t) => t.uid === uid)).toBe(true);
    useLayoutStore.getState().removeTile(uid);
    expect(useLayoutStore.getState().workspace.tiles.some((t) => t.uid === uid)).toBe(false);
  });

  it('updateTilePlacement mutates the matching tile only', () => {
    const target = useLayoutStore.getState().workspace.tiles[0];
    expect(target).toBeDefined();
    useLayoutStore
      .getState()
      .updateTilePlacement(target!.uid, { x: 7, y: 7, w: 5, h: 5 });
    const after = useLayoutStore
      .getState()
      .workspace.tiles.find((t) => t.uid === target!.uid);
    expect(after).toMatchObject({ x: 7, y: 7, w: 5, h: 5 });
  });

  it('updateTilePlacement is a no-op when nothing changed', () => {
    const target = useLayoutStore.getState().workspace.tiles[0]!;
    const beforeRef = useLayoutStore.getState().workspace;
    useLayoutStore.getState().updateTilePlacement(target.uid, {
      x: target.x,
      y: target.y,
      w: target.w,
      h: target.h,
    });
    const afterRef = useLayoutStore.getState().workspace;
    expect(afterRef).toBe(beforeRef);
  });

  it('updateTilePlacementsInLayout mutates changed tile placements in one pass', () => {
    const before = useLayoutStore.getState().workspace;
    const [first, second] = before.tiles;
    expect(first).toBeDefined();
    expect(second).toBeDefined();

    useLayoutStore.getState().updateTilePlacementsInLayout('default', [
      { uid: first!.uid, x: 3, y: 4, w: first!.w, h: first!.h },
      { uid: second!.uid, x: 9, y: 10, w: second!.w, h: second!.h },
    ]);

    const after = useLayoutStore.getState().workspace;
    expect(after.tiles.find((t) => t.uid === first!.uid)).toMatchObject({
      x: 3,
      y: 4,
    });
    expect(after.tiles.find((t) => t.uid === second!.uid)).toMatchObject({
      x: 9,
      y: 10,
    });
  });

  it('updateTilePlacementsInLayout is a no-op when nothing changed', () => {
    const before = useLayoutStore.getState().workspace;
    useLayoutStore.getState().updateTilePlacementsInLayout(
      'default',
      before.tiles.map((t) => ({
        uid: t.uid,
        x: t.x,
        y: t.y,
        w: t.w,
        h: t.h,
      })),
    );
    expect(useLayoutStore.getState().workspace).toBe(before);
  });

  it('updateTileInstanceConfig stores the opaque config blob', () => {
    const uid = useLayoutStore.getState().addTile('meters');
    const cfg = { schemaVersion: 1, widgets: [], title: 'My Meters' };
    useLayoutStore.getState().updateTileInstanceConfig(uid, cfg);
    const tile = useLayoutStore
      .getState()
      .workspace.tiles.find((t) => t.uid === uid);
    expect(tile?.instanceConfig).toEqual(cfg);
  });

  it('addLayout creates a blank workspace when no seed is supplied', () => {
    useLayoutStore.setState({
      radioKey: 'radio-1',
      layouts: [
        {
          id: 'default',
          name: 'Default',
          layoutJson: JSON.stringify(DEFAULT_WORKSPACE_LAYOUT),
        },
      ],
      activeLayoutId: 'default',
      workspace: DEFAULT_WORKSPACE_LAYOUT,
      isLoaded: true,
    });

    const id = useLayoutStore.getState().addLayout('Blank');
    const state = useLayoutStore.getState();
    const created = state.layouts.find((l) => l.id === id);

    expect(state.activeLayoutId).toBe(id);
    expect(state.workspace).toEqual(EMPTY_WORKSPACE_LAYOUT);
    expect(JSON.parse(created!.layoutJson)).toEqual(EMPTY_WORKSPACE_LAYOUT);
  });

  it('addLayout honors an explicit workspace seed', () => {
    const id = useLayoutStore.getState().addLayout('Seeded', {
      workspace: DEFAULT_WORKSPACE_LAYOUT,
    });

    const state = useLayoutStore.getState();
    expect(state.activeLayoutId).toBe(id);
    expect(state.workspace).toEqual(DEFAULT_WORKSPACE_LAYOUT);
  });

  it('per-layout placement updates do not switch or mutate the active workspace', () => {
    const otherWorkspace: WorkspaceLayout = {
      schemaVersion: 8,
      tiles: [{ uid: 'tile-other', panelId: 'cw', x: 0, y: 0, w: 8, h: 8 }],
    };
    useLayoutStore.setState({
      radioKey: 'radio-1',
      layouts: [
        {
          id: 'layout-a',
          name: 'A',
          layoutJson: JSON.stringify(DEFAULT_WORKSPACE_LAYOUT),
        },
        {
          id: 'layout-b',
          name: 'B',
          layoutJson: JSON.stringify(otherWorkspace),
        },
      ],
      activeLayoutId: 'layout-a',
      workspace: DEFAULT_WORKSPACE_LAYOUT,
      isLoaded: true,
    });
    const activeBefore = useLayoutStore.getState().workspace;

    useLayoutStore.getState().updateTilePlacementInLayout('layout-b', 'tile-other', {
      x: 4,
      y: 6,
      w: 10,
      h: 12,
    });

    const state = useLayoutStore.getState();
    expect(state.activeLayoutId).toBe('layout-a');
    expect(state.workspace).toBe(activeBefore);
    const layoutB = state.layouts.find((l) => l.id === 'layout-b');
    const parsed = JSON.parse(layoutB!.layoutJson) as WorkspaceLayout;
    expect(parsed.tiles[0]).toMatchObject({ x: 4, y: 6, w: 10, h: 12 });
  });

  it('addTileToLayout appends to a detached layout without changing the dock selection', () => {
    const otherWorkspace: WorkspaceLayout = {
      schemaVersion: 8,
      tiles: [{ uid: 'tile-other', panelId: 'cw', x: 0, y: 0, w: 8, h: 8 }],
    };
    useLayoutStore.setState({
      radioKey: 'radio-1',
      layouts: [
        {
          id: 'layout-a',
          name: 'A',
          layoutJson: JSON.stringify(DEFAULT_WORKSPACE_LAYOUT),
        },
        {
          id: 'layout-b',
          name: 'B',
          layoutJson: JSON.stringify(otherWorkspace),
        },
      ],
      activeLayoutId: 'layout-a',
      workspace: DEFAULT_WORKSPACE_LAYOUT,
      isLoaded: true,
    });

    const uid = useLayoutStore.getState().addTileToLayout('layout-b', 'cw');

    const state = useLayoutStore.getState();
    expect(uid).toMatch(/^tile-/);
    expect(state.activeLayoutId).toBe('layout-a');
    expect(state.workspace.tiles.some((t) => t.uid === uid)).toBe(false);
    const layoutB = state.layouts.find((l) => l.id === 'layout-b');
    const parsed = JSON.parse(layoutB!.layoutJson) as WorkspaceLayout;
    expect(parsed.tiles).toHaveLength(2);
    expect(parsed.tiles[1]).toMatchObject({ uid, panelId: 'cw' });
  });

  it('debounced save persists the mutated layout after a quick layout switch', async () => {
    vi.useFakeTimers();
    const fetchMock = vi
      .fn()
      .mockResolvedValue({ ok: true, status: 200, json: async () => ({}) });
    (globalThis as unknown as { fetch: typeof fetch }).fetch =
      fetchMock as unknown as typeof fetch;
    const layoutA = {
      id: 'layout-a',
      name: 'A',
      layoutJson: JSON.stringify(DEFAULT_WORKSPACE_LAYOUT),
    };
    const layoutB = {
      id: 'layout-b',
      name: 'B',
      layoutJson: JSON.stringify(DEFAULT_WORKSPACE_LAYOUT),
    };
    useLayoutStore.setState({
      radioKey: 'radio-1',
      layouts: [layoutA, layoutB],
      activeLayoutId: layoutA.id,
      workspace: DEFAULT_WORKSPACE_LAYOUT,
      isLoaded: true,
    });

    useLayoutStore.getState().updateTilePlacement('tile-vfo', {
      x: 18,
      y: 2,
      w: 6,
      h: 11,
    });
    useLayoutStore.getState().setActiveLayout(layoutB.id);
    vi.advanceTimersByTime(1000);
    await Promise.resolve();

    const layoutPuts = fetchMock.mock.calls.filter(
      (call) =>
        call[0] === '/api/ui/layouts' &&
        (call[1] as RequestInit | undefined)?.method === 'PUT',
    );
    expect(layoutPuts).toHaveLength(1);
    const body = JSON.parse((layoutPuts[0]![1] as RequestInit).body as string);
    const saved = JSON.parse(body.layoutJson) as typeof DEFAULT_WORKSPACE_LAYOUT;
    expect(body.layoutId).toBe(layoutA.id);
    expect(saved.tiles.find((t) => t.uid === 'tile-vfo')).toMatchObject({
      y: 2,
    });
  });
});

describe('parseWorkspaceLayout', () => {
  it('parseLayoutOrDefault preserves a valid empty layout', () => {
    expect(parseLayoutOrDefault(JSON.stringify(EMPTY_WORKSPACE_LAYOUT))).toEqual(
      EMPTY_WORKSPACE_LAYOUT,
    );
  });

  it('parseLayoutOrDefault falls back to the default layout for invalid data', () => {
    expect(parseLayoutOrDefault('')).toEqual(DEFAULT_WORKSPACE_LAYOUT);
    expect(parseLayoutOrDefault(JSON.stringify({ schemaVersion: 99, tiles: [] }))).toEqual(
      DEFAULT_WORKSPACE_LAYOUT,
    );
  });

  it('returns EMPTY_WORKSPACE_LAYOUT for non-object / missing input', () => {
    expect(parseWorkspaceLayout(null)).toEqual(EMPTY_WORKSPACE_LAYOUT);
    expect(parseWorkspaceLayout(undefined)).toEqual(EMPTY_WORKSPACE_LAYOUT);
    expect(parseWorkspaceLayout('hello')).toEqual(EMPTY_WORKSPACE_LAYOUT);
    expect(parseWorkspaceLayout(42)).toEqual(EMPTY_WORKSPACE_LAYOUT);
  });

  it('drops blobs whose schemaVersion is unknown (not 7 or 8)', () => {
    const v6 = { schemaVersion: 6, tiles: [] };
    expect(parseWorkspaceLayout(v6)).toEqual(EMPTY_WORKSPACE_LAYOUT);
    const future = { schemaVersion: 99, tiles: [] };
    expect(parseWorkspaceLayout(future)).toEqual(EMPTY_WORKSPACE_LAYOUT);
  });

  it('migrates legacy v7 layouts onto the v8 grid by scaling coords ×2', () => {
    const v7 = {
      schemaVersion: 7,
      tiles: [{ uid: 'a', panelId: 'hero', x: 3, y: 6, w: 9, h: 12 }],
    };
    const parsed = parseWorkspaceLayout(v7);
    expect(parsed.schemaVersion).toBe(8);
    expect(parsed.tiles[0]).toMatchObject({ x: 6, y: 12, w: 18, h: 24 });
  });

  it('accepts v8 layouts without rescaling', () => {
    const v8 = {
      schemaVersion: 8,
      tiles: [{ uid: 'a', panelId: 'hero', x: 6, y: 12, w: 18, h: 24 }],
    };
    const parsed = parseWorkspaceLayout(v8);
    expect(parsed.schemaVersion).toBe(8);
    expect(parsed.tiles[0]).toMatchObject({ x: 6, y: 12, w: 18, h: 24 });
  });

  it('keeps tiles whose panelId is not in the static registry (plugin-panel tiles register asynchronously)', () => {
    // Plugin panels register after the layout deserialises — if the parser
    // dropped unknown panelIds, every tab switch / reload would erase any
    // plugin tile (e.g. RF-2K, PGXL, antenna-genius). The renderer treats
    // an unresolved panelId as "render nothing until it shows up" so a
    // tile pointing at a permanently-removed panel id is harmless.
    const dirty = {
      schemaVersion: 7,
      tiles: [
        { uid: 'a', panelId: 'hero', x: 0, y: 0, w: 9, h: 12 },
        { uid: 'b', panelId: 'com.example.plugin.panel', x: 0, y: 0, w: 1, h: 1 },
      ],
    };
    const parsed = parseWorkspaceLayout(dirty);
    expect(parsed.tiles).toHaveLength(2);
    expect(parsed.tiles.map((t) => t.uid)).toEqual(['a', 'b']);
  });

  it('drops tiles missing required numeric fields', () => {
    const dirty = {
      schemaVersion: 7,
      tiles: [
        { uid: 'good', panelId: 'hero', x: 0, y: 0, w: 9, h: 12 },
        { uid: 'no-x', panelId: 'hero', y: 0, w: 9, h: 12 },
        { uid: 'nan-x', panelId: 'hero', x: 'oops', y: 0, w: 9, h: 12 },
      ],
    };
    const parsed = parseWorkspaceLayout(dirty);
    expect(parsed.tiles).toHaveLength(1);
    expect(parsed.tiles[0]?.uid).toBe('good');
  });

  it('preserves instanceConfig verbatim across a parse round-trip', () => {
    const cfg = { schemaVersion: 1, widgets: [{ uid: 'w' }] };
    const blob = {
      schemaVersion: 7,
      tiles: [
        {
          uid: 'm',
          panelId: 'metergroup',
          x: 0,
          y: 0,
          w: 6,
          h: 8,
          instanceConfig: cfg,
        },
      ],
    };
    const parsed = parseWorkspaceLayout(blob);
    expect(parsed.tiles[0]?.instanceConfig).toEqual(cfg);
  });

  it('round-trips a populated layout 5+ times unchanged', () => {
    const blob = DEFAULT_WORKSPACE_LAYOUT;
    let cur: unknown = blob;
    for (let i = 0; i < 6; i++) {
      cur = parseWorkspaceLayout(JSON.parse(JSON.stringify(cur)));
    }
    expect(cur).toEqual(DEFAULT_WORKSPACE_LAYOUT);
  });
});
