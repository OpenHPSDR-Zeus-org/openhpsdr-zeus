// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// Saved-layouts library store. A per-radio POOL of reusable layout presets,
// kept deliberately separate from the working tabs in `layout-store`. The
// operator snapshots a good workspace arrangement into a saved layout so they
// can:
//   • restore it onto the current tab if they mess the live arrangement up,
//   • seed a brand-new workspace from it,
//   • keep a named library of arrangements (back up / replace / rename / delete).
//
// Saved layouts are never "active" — they are templates. Applying one is an
// explicit action that copies its arrangement into a live tab via the
// layout-store. This store owns only the library CRUD + server sync; the
// apply/seed glue lives with the consumer (LeftLayoutBar) which already drives
// the layout-store.

import { create } from 'zustand';
import { useLayoutStore } from './layout-store';
import type { WorkspaceLayout } from '../layout/workspace';

export interface SavedLayout {
  id: string;
  name: string;
  /** Serialized WorkspaceLayout (parseLayoutOrDefault-ready). */
  layoutJson: string;
  icon?: string;
  description?: string;
  updatedUtc: number;
}

interface SavedLayoutsResponse {
  radioKey: string;
  savedLayouts: Array<{
    id: string;
    name: string;
    layoutJson: string;
    updatedUtc: number;
    icon?: string | null;
    description?: string | null;
  }>;
}

export interface SavedLayoutMetaPatch {
  name?: string;
  icon?: string;
  description?: string;
}

interface SavedLayoutsState {
  /** Per-radio key the current `savedLayouts` list belongs to. */
  radioKey: string;
  /** Every saved layout (preset) for `radioKey`, newest-updated last. */
  savedLayouts: SavedLayout[];
  /** True after the first loadForRadio() resolves (success or failure). */
  isLoaded: boolean;

  /** Switch the radio key and reload the library. No-op when the key already
   *  matches and the library has loaded. */
  loadForRadio: (radioKey: string) => Promise<void>;
  /** Snapshot the given workspace arrangement into a NEW saved layout. Returns
   *  the new saved-layout id. */
  saveWorkspaceAs: (
    name: string,
    workspace: WorkspaceLayout,
    meta?: { icon?: string; description?: string },
  ) => Promise<string>;
  /** Overwrite an existing saved layout's arrangement with `workspace`,
   *  keeping its name/icon/description unless overridden. The "replace" /
   *  "re-snapshot" affordance. */
  replaceWorkspace: (id: string, workspace: WorkspaceLayout) => Promise<void>;
  /** Edit a saved layout's presentation metadata (rename, change icon/desc).
   *  Leaves the stored arrangement untouched. */
  updateMeta: (id: string, patch: SavedLayoutMetaPatch) => Promise<void>;
  /** Remove a saved layout from the library. */
  deleteSavedLayout: (id: string) => Promise<void>;
}

function newSavedId(): string {
  return `saved-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
}

function mapResponse(dto: SavedLayoutsResponse): SavedLayout[] {
  return (dto.savedLayouts ?? []).map((l) => ({
    id: l.id,
    name: l.name,
    layoutJson: l.layoutJson,
    updatedUtc: l.updatedUtc,
    ...(l.icon ? { icon: l.icon } : {}),
    ...(l.description ? { description: l.description } : {}),
  }));
}

function putSavedLayout(
  radioKey: string,
  saved: { id: string; name: string; layoutJson: string; icon?: string; description?: string },
): Promise<SavedLayoutsResponse | null> {
  if (!radioKey) return Promise.resolve(null);
  return fetch('/api/ui/saved-layouts', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      radioKey,
      savedId: saved.id,
      name: saved.name,
      layoutJson: saved.layoutJson,
      icon: saved.icon ?? '',
      description: saved.description ?? '',
    }),
  })
    .then((res) => (res.ok ? (res.json() as Promise<SavedLayoutsResponse>) : null))
    .catch(() => null);
}

export const useSavedLayoutsStore = create<SavedLayoutsState>((set, get) => ({
  radioKey: '',
  savedLayouts: [],
  isLoaded: false,

  loadForRadio: async (radioKey) => {
    const safeKey = radioKey || 'default';
    if (get().radioKey === safeKey && get().isLoaded) return;
    try {
      const res = await fetch(
        `/api/ui/saved-layouts?radio=${encodeURIComponent(safeKey)}`,
      );
      if (!res.ok) throw new Error(`status ${res.status}`);
      const dto = (await res.json()) as SavedLayoutsResponse;
      set({ radioKey: safeKey, savedLayouts: mapResponse(dto), isLoaded: true });
    } catch {
      set({ radioKey: safeKey, savedLayouts: [], isLoaded: true });
    }
  },

  saveWorkspaceAs: async (name, workspace, meta) => {
    const radioKey = get().radioKey || useLayoutStore.getState().radioKey || 'default';
    const id = newSavedId();
    const saved: SavedLayout = {
      id,
      name: name.trim() || 'Untitled',
      layoutJson: JSON.stringify(workspace),
      updatedUtc: Date.now(),
      ...(meta?.icon ? { icon: meta.icon } : {}),
      ...(meta?.description ? { description: meta.description } : {}),
    };
    // Optimistic insert so the manager updates immediately.
    set({ savedLayouts: [...get().savedLayouts, saved] });
    const dto = await putSavedLayout(radioKey, saved);
    if (dto) set({ savedLayouts: mapResponse(dto) });
    return id;
  },

  replaceWorkspace: async (id, workspace) => {
    const radioKey = get().radioKey || useLayoutStore.getState().radioKey || 'default';
    const existing = get().savedLayouts.find((l) => l.id === id);
    if (!existing) return;
    const json = JSON.stringify(workspace);
    set({
      savedLayouts: get().savedLayouts.map((l) =>
        l.id === id ? { ...l, layoutJson: json, updatedUtc: Date.now() } : l,
      ),
    });
    const dto = await putSavedLayout(radioKey, { ...existing, layoutJson: json });
    if (dto) set({ savedLayouts: mapResponse(dto) });
  },

  updateMeta: async (id, patch) => {
    const radioKey = get().radioKey || useLayoutStore.getState().radioKey || 'default';
    const existing = get().savedLayouts.find((l) => l.id === id);
    if (!existing) return;
    const next: SavedLayout = { ...existing };
    if (patch.name !== undefined) next.name = patch.name.trim() || existing.name;
    if (patch.icon !== undefined) {
      const trimmed = patch.icon.trim();
      if (trimmed) next.icon = trimmed;
      else delete next.icon;
    }
    if (patch.description !== undefined) {
      const trimmed = patch.description.trim();
      if (trimmed) next.description = trimmed;
      else delete next.description;
    }
    set({ savedLayouts: get().savedLayouts.map((l) => (l.id === id ? next : l)) });
    const dto = await putSavedLayout(radioKey, {
      id: next.id,
      name: next.name,
      layoutJson: next.layoutJson,
      icon: next.icon ?? '',
      description: next.description ?? '',
    });
    if (dto) set({ savedLayouts: mapResponse(dto) });
  },

  deleteSavedLayout: async (id) => {
    const radioKey = get().radioKey || useLayoutStore.getState().radioKey || 'default';
    set({ savedLayouts: get().savedLayouts.filter((l) => l.id !== id) });
    try {
      const res = await fetch(
        `/api/ui/saved-layouts?radio=${encodeURIComponent(radioKey)}&id=${encodeURIComponent(id)}`,
        { method: 'DELETE' },
      );
      if (res.ok) {
        const dto = (await res.json()) as SavedLayoutsResponse;
        set({ savedLayouts: mapResponse(dto) });
      }
    } catch {
      // Optimistic removal already applied; a reload will reconcile.
    }
  },
}));
