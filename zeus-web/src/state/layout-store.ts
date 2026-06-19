// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Multi-layout client store (issue #241). One radio holds a list of named
// layouts; the operator picks the active one from the LeftLayoutBar. The
// underlying workspace tile mutators (addTile / removeTile / …) operate on
// whichever layout is active and debounce a PUT through to
// `/api/ui/layouts`.

import { create } from 'zustand';
import {
  EMPTY_WORKSPACE_LAYOUT,
  newTileUid,
  parseWorkspaceLayout,
  placeTileInGrid,
  type WorkspaceLayout,
  type WorkspaceTile,
} from '../layout/workspace';
import { DEFAULT_WORKSPACE_LAYOUT } from '../layout/defaultLayout';

export interface NamedLayout {
  id: string;
  name: string;
  /** Serialized WorkspaceLayout (parseWorkspaceLayout-ready). */
  layoutJson: string;
  /** Optional emoji or short glyph rendered above the label in the
   *  LeftLayoutBar. Empty/undefined → letter-fallback badge. */
  icon?: string;
  /** Optional longer description shown as the hover tooltip. */
  description?: string;
}

interface RadioLayoutsResponse {
  radioKey: string;
  layouts: Array<{
    id: string;
    name: string;
    layoutJson: string;
    updatedUtc: number;
    icon?: string | null;
    description?: string | null;
  }>;
  activeLayoutId: string;
}

export interface LayoutMetaUpdate {
  name?: string;
  icon?: string;
  description?: string;
}

interface LayoutState {
  /** Per-radio key the current `layouts` list belongs to. "default" while
   *  no radio is connected. Empty string before the first load. */
  radioKey: string;
  /** All named layouts for `radioKey`. May be empty until the server load
   *  resolves and the Default seed lands. */
  layouts: NamedLayout[];
  /** Id of the active layout. The `workspace` field below mirrors this
   *  layout's parsed WorkspaceLayout so existing FlexWorkspace consumers
   *  don't need to re-parse on every render. */
  activeLayoutId: string;
  /** The active layout's parsed WorkspaceLayout. Always non-null — falls
   *  back to DEFAULT_WORKSPACE_LAYOUT when the server returns invalid data. */
  workspace: WorkspaceLayout;
  /** True after loadFromServer() has run (success or 404 / network error). */
  isLoaded: boolean;
  // Add-Panel modal visibility — lifted into the store so the trigger
  // button can live wherever (LeftLayoutBar, FlexWorkspace) while the modal
  // itself still renders inside the workspace.
  addPanelOpen: boolean;
  setAddPanelOpen: (open: boolean) => void;

  // Settings is rendered as a workspace-replacing view (not a popover).
  // While settingsViewOpen is true the App renders <SettingsView /> in
  // place of <FlexWorkspace />. settingsInitialTab seeds the active tab
  // when the view opens (used by hash deeplinks like #qrz, #server).
  // setActiveLayout(...) clears this so picking a layout returns to the
  // workspace.
  settingsViewOpen: boolean;
  settingsInitialTab?: string;
  setSettingsView: (open: boolean, tab?: string) => void;

  /** Switch the radio key and reload the layouts list from the server.
   *  No-op when the key already matches and isLoaded is true. */
  loadForRadio: (radioKey: string) => Promise<void>;
  /** sendBeacon-with-fetch-fallback for page-unload persistence. */
  syncToServerBeforeUnload: () => void;

  // Layout-level mutators (LeftLayoutBar API):
  /** Create a new blank layout for the current radio and switch to it.
   *  Callers may pass an explicit workspace seed for special-purpose tabs.
   *  Returns the new id. */
  addLayout: (
    name: string,
    meta?: { icon?: string; description?: string; workspace?: WorkspaceLayout },
  ) => string;
  /** Delete a layout. If it was active, the server promotes the first
   *  remaining layout to active; if there are zero remaining the client
   *  re-seeds Default. */
  removeLayout: (id: string) => void;
  /** Rename an existing layout. Shim over updateLayoutMeta. */
  renameLayout: (id: string, name: string) => void;
  /** Update presentation metadata (name / icon / description) for a layout.
   *  Pass undefined for fields you do not want to change. Persists via PUT. */
  updateLayoutMeta: (id: string, patch: LayoutMetaUpdate) => void;
  /** Switch the active layout. Re-parses the layout's JSON into `workspace`
   *  and POSTs the new active id to the server. */
  setActiveLayout: (id: string) => void;
  /** Reset the active layout's tiles to DEFAULT_WORKSPACE_LAYOUT. The
   *  layout itself (id + name) is preserved. */
  resetActiveLayout: () => void;

  // Tile mutators — operate on the active layout. Persisted via the same
  // debounced PUT to /api/ui/layouts.
  /** Append a fresh tile for `panelId`. */
  addTile: (panelId: string, opts?: { instanceConfig?: unknown }) => string;
  /** Append a fresh tile to a specific layout without making it active. */
  addTileToLayout: (
    layoutId: string,
    panelId: string,
    opts?: { instanceConfig?: unknown },
  ) => string | null;
  /** Remove the tile with the given uid. */
  removeTile: (uid: string) => void;
  /** Remove a tile from a specific layout without making it active. */
  removeTileFromLayout: (layoutId: string, uid: string) => void;
  /** Replace a tile's grid placement (x/y/w/h). */
  updateTilePlacement: (
    uid: string,
    layout: Pick<WorkspaceTile, 'x' | 'y' | 'w' | 'h'>,
  ) => void;
  /** Replace a tile's grid placement in a specific layout. */
  updateTilePlacementInLayout: (
    layoutId: string,
    uid: string,
    layout: Pick<WorkspaceTile, 'x' | 'y' | 'w' | 'h'>,
  ) => void;
  /** Replace multiple tile grid placements in a specific layout atomically. */
  updateTilePlacementsInLayout: (
    layoutId: string,
    placements: Array<
      Pick<WorkspaceTile, 'uid' | 'x' | 'y' | 'w' | 'h'>
    >,
  ) => void;
  /** Toggle whether every tile in a layout is pinned in place. */
  setWorkspaceLockedInLayout: (layoutId: string, locked: boolean) => void;
  /** Toggle whether one tile is pinned to its current grid space. */
  setTileLockedInLayout: (layoutId: string, uid: string, locked: boolean) => void;
  /** Replace a tile's instanceConfig blob. */
  updateTileInstanceConfig: (uid: string, instanceConfig: unknown) => void;
  /** Replace a tile's instanceConfig blob in a specific layout. */
  updateTileInstanceConfigInLayout: (
    layoutId: string,
    uid: string,
    instanceConfig: unknown,
  ) => void;

  // Back-compat surface (still used by SettingsMenu before #241 lands the
  // LeftLayoutBar). resetLayout calls resetActiveLayout.
  resetLayout: () => void;
}

const DEFAULT_LAYOUT_ID = 'default';
const DEFAULT_LAYOUT_NAME = 'Default';

interface PendingLayoutSave {
  radioKey: string;
  layout: NamedLayout;
}

const saveTimers = new Map<string, ReturnType<typeof setTimeout>>();
const pendingLayoutSaves = new Map<string, PendingLayoutSave>();

function serializeWorkspace(ws: WorkspaceLayout): string {
  return JSON.stringify(ws);
}

function defaultSeedLayout(): NamedLayout {
  return {
    id: DEFAULT_LAYOUT_ID,
    name: DEFAULT_LAYOUT_NAME,
    layoutJson: serializeWorkspace(DEFAULT_WORKSPACE_LAYOUT),
  };
}

export function parseLayoutOrDefault(json: string): WorkspaceLayout {
  const parsed = parseLayoutJson(json);
  return parsed ?? DEFAULT_WORKSPACE_LAYOUT;
}

function parseLayoutJson(json: string): WorkspaceLayout | null {
  try {
    const raw = JSON.parse(json);
    if (!hasRecognizedWorkspaceSchema(raw)) return null;
    return parseWorkspaceLayout(raw);
  } catch {
    return null;
  }
}

function hasRecognizedWorkspaceSchema(raw: unknown): boolean {
  if (!raw || typeof raw !== 'object') return false;
  const obj = raw as { schemaVersion?: unknown; tiles?: unknown };
  return (obj.schemaVersion === 7 || obj.schemaVersion === 8) && Array.isArray(obj.tiles);
}

function findActive(layouts: NamedLayout[], id: string): NamedLayout | undefined {
  return layouts.find((l) => l.id === id);
}

export const useLayoutStore = create<LayoutState>((set, get) => ({
  radioKey: '',
  layouts: [],
  activeLayoutId: DEFAULT_LAYOUT_ID,
  workspace: DEFAULT_WORKSPACE_LAYOUT,
  isLoaded: false,
  addPanelOpen: false,
  setAddPanelOpen: (open) => set({ addPanelOpen: open }),

  settingsViewOpen: false,
  setSettingsView: (open, tab) =>
    set({
      settingsViewOpen: open,
      settingsInitialTab: open ? tab : undefined,
    }),

  loadForRadio: async (radioKey) => {
    const safeKey = radioKey || 'default';
    if (get().radioKey === safeKey && get().isLoaded) return;
    try {
      const res = await fetch(`/api/ui/layouts?radio=${encodeURIComponent(safeKey)}`);
      if (!res.ok) throw new Error(`status ${res.status}`);
      const dto = (await res.json()) as RadioLayoutsResponse;
      let layouts: NamedLayout[] = (dto.layouts ?? []).map((l) => ({
        id: l.id,
        name: l.name,
        layoutJson: l.layoutJson,
        ...(l.icon ? { icon: l.icon } : {}),
        ...(l.description ? { description: l.description } : {}),
      }));
      let activeId = dto.activeLayoutId || layouts[0]?.id || DEFAULT_LAYOUT_ID;
      // Empty radio → seed a Default and persist.
      if (layouts.length === 0) {
        const seed = defaultSeedLayout();
        layouts = [seed];
        activeId = seed.id;
        void fetch('/api/ui/layouts', {
          method: 'PUT',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            radioKey: safeKey,
            layoutId: seed.id,
            name: seed.name,
            layoutJson: seed.layoutJson,
          }),
        });
      }
      const active = findActive(layouts, activeId) ?? layouts[0]!;
      set({
        radioKey: safeKey,
        layouts,
        activeLayoutId: active.id,
        workspace: parseLayoutOrDefault(active.layoutJson),
        isLoaded: true,
      });
    } catch {
      // Network or parse failure — render defaults so the UI still works.
      const seed = defaultSeedLayout();
      set({
        radioKey: safeKey,
        layouts: [seed],
        activeLayoutId: seed.id,
        workspace: DEFAULT_WORKSPACE_LAYOUT,
        isLoaded: true,
      });
    }
  },

  syncToServerBeforeUnload: () => {
    const { radioKey, activeLayoutId, layouts, workspace } = get();
    const saves = new Map(pendingLayoutSaves);
    if (radioKey && activeLayoutId) {
      const active = findActive(layouts, activeLayoutId);
      if (active) {
        saves.set(saveKey(radioKey, active.id), {
          radioKey,
          layout: { ...active, layoutJson: serializeWorkspace(workspace) },
        });
      }
    }
    clearScheduledSaves();
    for (const pending of saves.values()) {
      sendLayoutBeforeUnload(pending.radioKey, pending.layout);
    }
  },

  addLayout: (name, meta) => {
    const id = `layout-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
    const initialWorkspace = meta?.workspace ?? EMPTY_WORKSPACE_LAYOUT;
    const json = serializeWorkspace(initialWorkspace);
    const next: NamedLayout = {
      id,
      name: name || 'Untitled',
      layoutJson: json,
      ...(meta?.icon ? { icon: meta.icon } : {}),
      ...(meta?.description ? { description: meta.description } : {}),
    };
    const layouts = [...get().layouts, next];
    set({
      layouts,
      activeLayoutId: id,
      workspace: parseLayoutOrDefault(json),
    });
    void putNamedLayout(get().radioKey, next);
    void postActiveLayout(get().radioKey, id);
    return id;
  },

  removeLayout: (id) => {
    const { radioKey, layouts, activeLayoutId } = get();
    if (!radioKey) return;
    if (layouts.length <= 1) return; // never delete the last one
    cancelScheduledSave(radioKey, id);
    const remaining = layouts.filter((l) => l.id !== id);
    let nextActive = activeLayoutId;
    let nextWorkspace = get().workspace;
    if (activeLayoutId === id) {
      nextActive = remaining[0]!.id;
      nextWorkspace = parseLayoutOrDefault(remaining[0]!.layoutJson);
    }
    set({ layouts: remaining, activeLayoutId: nextActive, workspace: nextWorkspace });
    void fetch(`/api/ui/layouts?radio=${encodeURIComponent(radioKey)}&id=${encodeURIComponent(id)}`, {
      method: 'DELETE',
    });
    if (activeLayoutId === id) {
      void postActiveLayout(radioKey, nextActive);
    }
  },

  renameLayout: (id, name) => {
    get().updateLayoutMeta(id, { name });
  },

  updateLayoutMeta: (id, patch) => {
    const layouts = get().layouts.map((l) => {
      if (l.id !== id) return l;
      const next: NamedLayout = { ...l };
      if (patch.name !== undefined) next.name = patch.name || l.name;
      if (patch.icon !== undefined) {
        const trimmed = patch.icon.trim();
        if (trimmed.length === 0) delete next.icon;
        else next.icon = trimmed;
      }
      if (patch.description !== undefined) {
        const trimmed = patch.description.trim();
        if (trimmed.length === 0) delete next.description;
        else next.description = trimmed;
      }
      return next;
    });
    set({ layouts });
    const updated = findActive(layouts, id);
    if (updated) {
      const radioKey = get().radioKey;
      cancelScheduledSave(radioKey, updated.id);
      void putNamedLayout(radioKey, updated);
    }
  },

  setActiveLayout: (id) => {
    const { layouts, radioKey } = get();
    const target = findActive(layouts, id);
    if (!target) return;
    set({
      activeLayoutId: id,
      workspace: parseLayoutOrDefault(target.layoutJson),
      // Picking a layout always returns to the workspace view — clearing
      // any active settings overlay keeps the operator's mental model
      // consistent.
      settingsViewOpen: false,
      settingsInitialTab: undefined,
    });
    void postActiveLayout(radioKey, id);
  },

  resetActiveLayout: () => {
    const { layouts, activeLayoutId, radioKey } = get();
    const active = findActive(layouts, activeLayoutId);
    if (!active) {
      set({ workspace: DEFAULT_WORKSPACE_LAYOUT });
      return;
    }
    const json = serializeWorkspace(DEFAULT_WORKSPACE_LAYOUT);
    const updated: NamedLayout = { ...active, layoutJson: json };
    const next = layouts.map((l) => (l.id === activeLayoutId ? updated : l));
    set({ layouts: next, workspace: DEFAULT_WORKSPACE_LAYOUT });
    cancelScheduledSave(radioKey, updated.id);
    void putNamedLayout(radioKey, updated);
  },

  addTile: (panelId, opts) => {
    return get().addTileToLayout(get().activeLayoutId, panelId, opts) ?? '';
  },

  addTileToLayout: (layoutId, panelId, opts) => {
    const target = findActive(get().layouts, layoutId);
    if (!target && layoutId !== get().activeLayoutId) return null;
    const workspace = layoutId === get().activeLayoutId
      ? get().workspace
      : parseLayoutOrDefault(target!.layoutJson);
    const placement = placeTileInGrid(panelId, workspace.tiles);
    const uid = newTileUid();
    const tile: WorkspaceTile = {
      uid,
      panelId,
      ...placement,
      ...(opts?.instanceConfig !== undefined
        ? { instanceConfig: opts.instanceConfig }
        : {}),
    };
    const next: WorkspaceLayout = {
      ...workspace,
      tiles: [...workspace.tiles, tile],
    };
    applyWorkspaceMutationForLayout(set, get, layoutId, next);
    return uid;
  },

  removeTile: (uid) => {
    get().removeTileFromLayout(get().activeLayoutId, uid);
  },

  removeTileFromLayout: (layoutId, uid) => {
    const target = findActive(get().layouts, layoutId);
    if (!target && layoutId !== get().activeLayoutId) return;
    const workspace = layoutId === get().activeLayoutId
      ? get().workspace
      : parseLayoutOrDefault(target!.layoutJson);
    if (!workspace.tiles.some((t) => t.uid === uid)) return;
    const next: WorkspaceLayout = {
      ...workspace,
      tiles: workspace.tiles.filter((t) => t.uid !== uid),
    };
    applyWorkspaceMutationForLayout(set, get, layoutId, next);
  },

  updateTilePlacement: (uid, layout) => {
    get().updateTilePlacementInLayout(get().activeLayoutId, uid, layout);
  },

  updateTilePlacementInLayout: (layoutId, uid, layout) => {
    const target = findActive(get().layouts, layoutId);
    if (!target && layoutId !== get().activeLayoutId) return;
    const workspace = layoutId === get().activeLayoutId
      ? get().workspace
      : parseLayoutOrDefault(target!.layoutJson);
    if (workspace.locked) return;
    let changed = false;
    const tiles = workspace.tiles.map((t) => {
      if (t.uid !== uid) return t;
      if (t.locked) return t;
      if (
        t.x === layout.x &&
        t.y === layout.y &&
        t.w === layout.w &&
        t.h === layout.h
      ) {
        return t;
      }
      changed = true;
      return { ...t, ...layout };
    });
    if (!changed) return;
    applyWorkspaceMutationForLayout(set, get, layoutId, { ...workspace, tiles });
  },

  updateTilePlacementsInLayout: (layoutId, placements) => {
    const target = findActive(get().layouts, layoutId);
    if (!target && layoutId !== get().activeLayoutId) return;
    if (placements.length === 0) return;
    const workspace = layoutId === get().activeLayoutId
      ? get().workspace
      : parseLayoutOrDefault(target!.layoutJson);
    if (workspace.locked) return;
    const nextPlacements = new Map(
      placements.map((p) => [
        p.uid,
        { x: p.x, y: p.y, w: p.w, h: p.h },
      ]),
    );
    let changed = false;
    const tiles = workspace.tiles.map((t) => {
      const next = nextPlacements.get(t.uid);
      if (!next) return t;
      if (t.locked) return t;
      if (
        t.x === next.x &&
        t.y === next.y &&
        t.w === next.w &&
        t.h === next.h
      ) {
        return t;
      }
      changed = true;
      return { ...t, ...next };
    });
    if (!changed) return;
    applyWorkspaceMutationForLayout(set, get, layoutId, { ...workspace, tiles });
  },

  setWorkspaceLockedInLayout: (layoutId, locked) => {
    const target = findActive(get().layouts, layoutId);
    if (!target && layoutId !== get().activeLayoutId) return;
    const workspace = layoutId === get().activeLayoutId
      ? get().workspace
      : parseLayoutOrDefault(target!.layoutJson);
    if ((workspace.locked === true) === locked) return;
    applyWorkspaceMutationForLayout(set, get, layoutId, withWorkspaceLocked(workspace, locked));
  },

  setTileLockedInLayout: (layoutId, uid, locked) => {
    const target = findActive(get().layouts, layoutId);
    if (!target && layoutId !== get().activeLayoutId) return;
    const workspace = layoutId === get().activeLayoutId
      ? get().workspace
      : parseLayoutOrDefault(target!.layoutJson);
    let changed = false;
    const tiles = workspace.tiles.map((t) => {
      if (t.uid !== uid) return t;
      if ((t.locked === true) === locked) return t;
      changed = true;
      return withTileLocked(t, locked);
    });
    if (!changed) return;
    applyWorkspaceMutationForLayout(set, get, layoutId, { ...workspace, tiles });
  },

  updateTileInstanceConfig: (uid, instanceConfig) => {
    get().updateTileInstanceConfigInLayout(get().activeLayoutId, uid, instanceConfig);
  },

  updateTileInstanceConfigInLayout: (layoutId, uid, instanceConfig) => {
    const target = findActive(get().layouts, layoutId);
    if (!target && layoutId !== get().activeLayoutId) return;
    const workspace = layoutId === get().activeLayoutId
      ? get().workspace
      : parseLayoutOrDefault(target!.layoutJson);
    if (!workspace.tiles.some((t) => t.uid === uid)) return;
    const tiles = workspace.tiles.map((t) =>
      t.uid === uid ? { ...t, instanceConfig } : t,
    );
    applyWorkspaceMutationForLayout(set, get, layoutId, { ...workspace, tiles });
  },

  resetLayout: () => get().resetActiveLayout(),
}));

function applyWorkspaceMutationForLayout(
  set: (partial: Partial<LayoutState>) => void,
  get: () => LayoutState,
  layoutId: string,
  next: WorkspaceLayout,
) {
  // Mirror the new tiles into the target NamedLayout's serialized JSON so
  // switching/focusing that layout later doesn't regress the change.
  const { layouts, activeLayoutId, radioKey } = get();
  const json = serializeWorkspace(next);
  const target = findActive(layouts, layoutId);
  if (target) {
    const updated: NamedLayout = { ...target, layoutJson: json };
    const newLayouts = layouts.map((l) => (l.id === layoutId ? updated : l));
    set({
      layouts: newLayouts,
      ...(layoutId === activeLayoutId ? { workspace: next } : {}),
    });
    scheduleSave(radioKey, updated);
  } else {
    // Test / unhydrated state — just update workspace.
    set({ workspace: next });
  }
}

function withWorkspaceLocked(
  workspace: WorkspaceLayout,
  locked: boolean,
): WorkspaceLayout {
  if (locked) return { ...workspace, locked: true };
  const next = { ...workspace };
  delete next.locked;
  return next;
}

function withTileLocked(tile: WorkspaceTile, locked: boolean): WorkspaceTile {
  if (locked) return { ...tile, locked: true };
  const next = { ...tile };
  delete next.locked;
  return next;
}

function saveKey(radioKey: string, layoutId: string): string {
  return `${radioKey}\u0000${layoutId}`;
}

function scheduleSave(radioKey: string, layout: NamedLayout) {
  if (!radioKey) return;
  const key = saveKey(radioKey, layout.id);
  const existing = saveTimers.get(key);
  if (existing) clearTimeout(existing);
  const snapshot: PendingLayoutSave = {
    radioKey,
    layout: { ...layout },
  };
  pendingLayoutSaves.set(key, snapshot);
  saveTimers.set(key, setTimeout(() => {
    saveTimers.delete(key);
    pendingLayoutSaves.delete(key);
    void putNamedLayout(snapshot.radioKey, snapshot.layout);
  }, 1000));
}

function cancelScheduledSave(radioKey: string, layoutId: string) {
  if (!radioKey || !layoutId) return;
  const key = saveKey(radioKey, layoutId);
  const existing = saveTimers.get(key);
  if (existing) clearTimeout(existing);
  saveTimers.delete(key);
  pendingLayoutSaves.delete(key);
}

function clearScheduledSaves() {
  for (const timer of saveTimers.values()) clearTimeout(timer);
  saveTimers.clear();
  pendingLayoutSaves.clear();
}

function layoutPayload(radioKey: string, layout: NamedLayout): string {
  return JSON.stringify({
    radioKey,
    layoutId: layout.id,
    name: layout.name,
    layoutJson: layout.layoutJson,
    icon: layout.icon ?? '',
    description: layout.description ?? '',
  });
}

function putNamedLayout(radioKey: string, layout: NamedLayout): Promise<unknown> {
  if (!radioKey) return Promise.resolve();
  return fetch('/api/ui/layouts', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: layoutPayload(radioKey, layout),
  });
}

function sendLayoutBeforeUnload(radioKey: string, layout: NamedLayout): void {
  if (!radioKey) return;
  const body = layoutPayload(radioKey, layout);
  const blob = new Blob([body], { type: 'application/json' });
  if (!navigator.sendBeacon('/api/ui/layout-beacon', blob)) {
    void fetch('/api/ui/layouts', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body,
      keepalive: true,
    });
  }
}

function postActiveLayout(radioKey: string, layoutId: string): Promise<unknown> {
  if (!radioKey || !layoutId) return Promise.resolve();
  return fetch('/api/ui/layouts/active', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ radioKey, layoutId }),
  });
}

// Re-export EMPTY_WORKSPACE_LAYOUT so existing import sites don't need to
// reach into ../layout/workspace separately.
export { EMPTY_WORKSPACE_LAYOUT };
