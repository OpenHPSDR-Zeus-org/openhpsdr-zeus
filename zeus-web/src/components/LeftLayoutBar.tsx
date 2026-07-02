// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// LeftLayoutBar — vertical bar listing the current radio's named layouts.
// Each item shows a large emoji icon with a small label beneath. Clicking
// switches to that layout; the gear opens LayoutSettingsModal to edit the
// label, icon, and tooltip description.
//
// Layout-list anatomy (top → bottom):
//   • One tab per saved NamedLayout (icon + label, optional gear/✕).
//   • A trailing dashed "+" placeholder tab — always present — that opens
//     LayoutSettingsModal in create mode. The "+" is a slot, not a button:
//     adding a layout slides it down so the next "+" sits below the new
//     layout. This replaces the earlier separate "+" / "⟳" actions row.
//   • A horizontal divider, then the bottom-pinned Settings slot. Clicking
//     it flips layout-store.settingsViewOpen so App swaps the workspace
//     for SettingsView. Picking any layout tab clears that flag.
//
// The "Reset to default" affordance lives on the bottom transport bar
// alongside "+ Add Panel" — both act on the active layout's tile
// arrangement, so they read naturally as a pair there.
//
// Issue #241: visual chrome reuses tokens.css; no new colors are introduced.

import {
  useMemo,
  useRef,
  useState,
  type CSSProperties,
  type DragEvent,
} from 'react';
import { LockKeyhole } from 'lucide-react';
import { parseLayoutOrDefault, useLayoutStore } from '../state/layout-store';
import { useSavedLayoutsStore } from '../state/saved-layouts-store';
import { useDisplaySettingsStore } from '../state/display-settings-store';
import {
  LayoutSettingsModal,
  type LayoutSettingsValue,
} from '../layout/LayoutSettingsModal';
import { EMPTY_WORKSPACE_LAYOUT } from '../layout/workspace';
import { ConfirmDialog } from '../layout/ConfirmDialog';
import { openSettingsWindow, openWorkspaceWindow } from '../layout/workspace-windows';

type ModalState =
  | { kind: 'closed' }
  | { kind: 'create' }
  | { kind: 'edit'; id: string }
  | { kind: 'delete'; id: string; name: string };

export function LeftLayoutBar() {
  const barRef = useRef<HTMLElement | null>(null);
  const layouts = useLayoutStore((s) => s.layouts);
  const activeLayoutId = useLayoutStore((s) => s.activeLayoutId);
  const setActiveLayout = useLayoutStore((s) => s.setActiveLayout);
  const addLayout = useLayoutStore((s) => s.addLayout);
  const removeLayout = useLayoutStore((s) => s.removeLayout);
  const updateLayoutMeta = useLayoutStore((s) => s.updateLayoutMeta);
  const replaceActiveWorkspace = useLayoutStore((s) => s.replaceActiveWorkspace);
  const setWorkspaceLockedInLayout = useLayoutStore(
    (s) => s.setWorkspaceLockedInLayout,
  );
  // Saved-layouts library (reusable presets, separate from the tabs).
  const savedLayouts = useSavedLayoutsStore((s) => s.savedLayouts);
  const saveWorkspaceToLibrary = useSavedLayoutsStore((s) => s.saveWorkspaceAs);
  const replaceSavedLayout = useSavedLayoutsStore((s) => s.replaceWorkspace);
  const updateSavedLayoutMeta = useSavedLayoutsStore((s) => s.updateMeta);
  const deleteSavedLayout = useSavedLayoutsStore((s) => s.deleteSavedLayout);
  const isLoaded = useLayoutStore((s) => s.isLoaded);
  const settingsViewOpen = useLayoutStore((s) => s.settingsViewOpen);
  const setSettingsView = useLayoutStore((s) => s.setSettingsView);
  // The bar's blue gradient + dot wash follows the operator's panadapter
  // trace colour from the Display tab. CLAUDE.md flags trace amber as
  // "panadapter-only", but the maintainer explicitly asked for the chrome
  // to track the trace selection — so the bar tints to whatever hue the
  // operator chose. Wash uses a 0.25× darkened variant so the original
  // top-half-fades-out gradient style is preserved at any hue.
  const rxTraceColor = useDisplaySettingsStore((s) => s.rxTraceColor);
  const tintStyle = useMemo<CSSProperties | undefined>(() => {
    const m = /^#([0-9A-Fa-f]{2})([0-9A-Fa-f]{2})([0-9A-Fa-f]{2})$/.exec(rxTraceColor);
    if (!m || !m[1] || !m[2] || !m[3]) return undefined;
    const r = parseInt(m[1], 16);
    const g = parseInt(m[2], 16);
    const b = parseInt(m[3], 16);
    const dr = Math.round(r * 0.25);
    const dg = Math.round(g * 0.25);
    const db = Math.round(b * 0.25);
    return {
      ['--lb-tint-r' as string]: r,
      ['--lb-tint-g' as string]: g,
      ['--lb-tint-b' as string]: b,
      ['--lb-wash-r' as string]: dr,
      ['--lb-wash-g' as string]: dg,
      ['--lb-wash-b' as string]: db,
    };
  }, [rxTraceColor]);

  const [modal, setModal] = useState<ModalState>({ kind: 'closed' });
  // Create-mode "Start from" selection: '' = blank, else a saved-layout id.
  const [createSourceId, setCreateSourceId] = useState('');
  const [draggingLayoutId, setDraggingLayoutId] = useState<string | null>(null);
  const [settingsDragging, setSettingsDragging] = useState(false);
  const lockedLayoutIds = useMemo(
    () =>
      new Set(
        layouts
          .filter((layout) => parseLayoutOrDefault(layout.layoutJson).locked === true)
          .map((layout) => layout.id),
      ),
    [layouts],
  );

  const handleAdd = () => {
    setCreateSourceId('');
    setModal({ kind: 'create' });
  };

  const handleDelete = (id: string, name: string) => {
    if (layouts.length <= 1) return;
    setModal({ kind: 'delete', id, name });
  };

  // Opening the manager switches the workspace to that layout so the editor's
  // "current arrangement" (used by Save / Save as new) always matches the
  // layout being edited.
  const openManage = (id: string) => {
    setActiveLayout(id);
    setModal({ kind: 'edit', id });
  };

  const handleModalSave = (value: LayoutSettingsValue) => {
    if (modal.kind === 'create') {
      // Seed the new workspace: a chosen saved layout's arrangement, or blank.
      const source = createSourceId
        ? savedLayouts.find((l) => l.id === createSourceId)
        : undefined;
      const base = source
        ? parseLayoutOrDefault(source.layoutJson)
        : EMPTY_WORKSPACE_LAYOUT;
      const seeded = value.locked || source;
      addLayout(value.name, {
        icon: value.icon || undefined,
        description: value.description || undefined,
        ...(seeded
          ? { workspace: value.locked ? { ...base, locked: true } : base }
          : {}),
      });
    } else if (modal.kind === 'edit') {
      // Save edits IN PLACE — never spawns a new tab.
      updateLayoutMeta(modal.id, {
        name: value.name,
        icon: value.icon,
        description: value.description,
      });
      setWorkspaceLockedInLayout(modal.id, value.locked);
    }
    setModal({ kind: 'closed' });
  };

  // Saved-layouts library handlers (manager mode) ----------------------------

  // Snapshot the live workspace into a NEW saved layout, seeding its icon /
  // description from the workspace being edited.
  const handleSaveToLibrary = (name: string) => {
    if (modal.kind !== 'edit') return;
    const ws = useLayoutStore.getState().workspace;
    const current = layouts.find((l) => l.id === modal.id);
    void saveWorkspaceToLibrary(name, ws, {
      ...(current?.icon ? { icon: current.icon } : {}),
      ...(current?.description ? { description: current.description } : {}),
    });
  };

  // Apply (restore) a saved layout onto the current workspace in place.
  const handleApplySaved = (id: string) => {
    const saved = savedLayouts.find((l) => l.id === id);
    if (!saved) return;
    replaceActiveWorkspace(parseLayoutOrDefault(saved.layoutJson));
  };

  // Overwrite a saved layout with the current workspace arrangement.
  const handleReplaceSaved = (id: string) => {
    void replaceSavedLayout(id, useLayoutStore.getState().workspace);
  };

  // Delete the selected workspace tab from the manager — removeLayout promotes
  // the next layout to active; follow it so the manager keeps editing a valid
  // workspace.
  const handleDeleteWorkspace = (id: string) => {
    removeLayout(id);
    const nextActive = useLayoutStore.getState().activeLayoutId;
    setModal({ kind: 'edit', id: nextActive });
  };

  const editingLayout =
    modal.kind === 'edit' ? layouts.find((l) => l.id === modal.id) : undefined;

  const handleLayoutDragStart = (e: DragEvent<HTMLButtonElement>, id: string, name: string) => {
    setDraggingLayoutId(id);
    e.dataTransfer.effectAllowed = 'copy';
    e.dataTransfer.setData('application/x-zeus-layout-id', id);
    e.dataTransfer.setData('text/plain', name);
  };

  const isDragEndOutsideDock = (e: DragEvent<HTMLButtonElement>) => {
    const rect = barRef.current?.getBoundingClientRect();
    if (!rect) return false;
    const hasClientPoint = e.clientX !== 0 || e.clientY !== 0;
    return !(
      hasClientPoint &&
      e.clientX >= rect.left &&
      e.clientX <= rect.right &&
      e.clientY >= rect.top &&
      e.clientY <= rect.bottom
    );
  };

  const handleLayoutDragEnd = (
    e: DragEvent<HTMLButtonElement>,
    layout: { id: string; name: string },
  ) => {
    setDraggingLayoutId(null);
    if (isDragEndOutsideDock(e)) {
      openWorkspaceWindow(layout.id, layout.name);
    }
  };

  const handleSettingsDragStart = (e: DragEvent<HTMLButtonElement>) => {
    setSettingsDragging(true);
    e.dataTransfer.effectAllowed = 'copy';
    e.dataTransfer.setData('application/x-zeus-settings', '1');
    e.dataTransfer.setData('text/plain', 'Settings');
  };

  const handleSettingsDragEnd = (e: DragEvent<HTMLButtonElement>) => {
    setSettingsDragging(false);
    if (isDragEndOutsideDock(e)) {
      openSettingsWindow();
    }
  };

  return (
    <aside ref={barRef} className="left-layout-bar" aria-label="Layouts" style={tintStyle}>
      <div className="lb-list" role="tablist" aria-orientation="vertical">
        {!isLoaded ? (
          <div className="lb-empty" aria-hidden>…</div>
        ) : (
          <>
            {layouts.map((l) => {
              // While the Settings view is showing no layout tab is active —
              // it's a sibling view, not a layout. The flag is cleared the
              // moment the operator clicks any layout tab (setActiveLayout).
              const active = !settingsViewOpen && l.id === activeLayoutId;
              const tooltip = l.description?.trim()
                ? `${l.name} — ${l.description}`
                : `${l.name} (gear to edit)`;
              const locked = lockedLayoutIds.has(l.id);
              return (
                <div
                  key={l.id}
                  className={`lb-item ${active ? 'active' : ''} ${draggingLayoutId === l.id ? 'dragging' : ''}`}
                >
                  <button
                    type="button"
                    className="lb-tab"
                    role="tab"
                    aria-selected={active}
                    data-layout-tab-id={l.id}
                    draggable
                    onDragStart={(e) => handleLayoutDragStart(e, l.id, l.name)}
                    onDragEnd={(e) => handleLayoutDragEnd(e, l)}
                    onClick={() => setActiveLayout(l.id)}
                    title={`${tooltip} — drag off the dock to open in a window`}
                  >
                    <span
                      className={`lb-tab-icon ${l.icon ? '' : 'lb-tab-icon-fallback'}`}
                      aria-hidden
                    >
                      {l.icon || initialLetter(l.name)}
                    </span>
                    <span className="lb-tab-name">{l.name}</span>
                    {locked ? (
                      <LockKeyhole
                        className="lb-lock-indicator"
                        size={10}
                        strokeWidth={2.2}
                        aria-hidden
                      />
                    ) : null}
                  </button>
                  <button
                    type="button"
                    className="lb-gear"
                    onClick={() => openManage(l.id)}
                    title={`Edit ${l.name}`}
                    aria-label={`Edit ${l.name}`}
                  >
                    ⚙
                  </button>
                  {active && layouts.length > 1 && (
                    <button
                      type="button"
                      className="lb-x"
                      onClick={() => handleDelete(l.id, l.name)}
                      title={`Delete ${l.name}`}
                      aria-label={`Delete ${l.name}`}
                    >
                      ✕
                    </button>
                  )}
                </div>
              );
            })}
            {/* Trailing placeholder slot — always at the end of the list.
                Adding a layout pushes this slot down one row, so the "+"
                stays the bottom-most tab. */}
            <div className="lb-item lb-item-add">
              <button
                type="button"
                className="lb-tab lb-tab-add"
                onClick={handleAdd}
                title="Add a new layout"
                aria-label="Add a new layout"
              >
                <span className="lb-tab-icon lb-tab-icon-fallback" aria-hidden>
                  +
                </span>
                <span className="lb-tab-name">Add</span>
              </button>
            </div>
          </>
        )}
      </div>

      <div className="lb-divider" aria-hidden />

      <div className={`lb-settings-slot ${settingsDragging ? 'dragging' : ''}`}>
        <button
          type="button"
          className={`lb-tab lb-tab-settings ${settingsViewOpen ? 'active' : ''}`}
          draggable
          onDragStart={handleSettingsDragStart}
          onDragEnd={handleSettingsDragEnd}
          onClick={() => setSettingsView(!settingsViewOpen)}
          title="Open settings — drag off the dock to open in a window"
          aria-pressed={settingsViewOpen}
        >
          <span className="lb-tab-icon" aria-hidden>⚙</span>
        </button>
      </div>

      {modal.kind === 'create' && (
        <LayoutSettingsModal
          title="New workspace"
          saveLabel="Create"
          initial={{
            name: `Workspace ${layouts.length + 1}`,
            icon: '',
            description: '',
            locked: false,
          }}
          createSource={{
            savedLayouts,
            sourceId: createSourceId,
            onSourceChange: setCreateSourceId,
          }}
          onSave={handleModalSave}
          onClose={() => setModal({ kind: 'closed' })}
        />
      )}
      {modal.kind === 'edit' && editingLayout && (
        <LayoutSettingsModal
          title="Layout settings"
          initial={{
            name: editingLayout.name,
            icon: editingLayout.icon ?? '',
            description: editingLayout.description ?? '',
            locked: parseLayoutOrDefault(editingLayout.layoutJson).locked === true,
          }}
          manager={{
            workspaces: layouts.map((l) => ({
              id: l.id,
              name: l.name,
              ...(l.icon ? { icon: l.icon } : {}),
              locked: lockedLayoutIds.has(l.id),
            })),
            selectedId: modal.id,
            onSelectWorkspace: openManage,
            onDeleteWorkspace: handleDeleteWorkspace,
            canDeleteWorkspace: layouts.length > 1,
            savedLayouts,
            onSaveWorkspaceToLibrary: handleSaveToLibrary,
            onApplySaved: handleApplySaved,
            onReplaceSaved: handleReplaceSaved,
            onRenameSaved: (id, name) => void updateSavedLayoutMeta(id, { name }),
            onDeleteSaved: (id) => void deleteSavedLayout(id),
          }}
          onSave={handleModalSave}
          onClose={() => setModal({ kind: 'closed' })}
        />
      )}
      {modal.kind === 'delete' && (
        <ConfirmDialog
          title="Delete layout"
          confirmLabel="Delete Layout"
          onCancel={() => setModal({ kind: 'closed' })}
          onConfirm={() => {
            removeLayout(modal.id);
            setModal({ kind: 'closed' });
          }}
        >
          <p>Delete {modal.name}?</p>
          <p>This removes the saved panel arrangement for this radio.</p>
        </ConfirmDialog>
      )}
    </aside>
  );
}

function initialLetter(name: string): string {
  const ch = name.trim().charAt(0);
  return ch ? ch.toUpperCase() : '·';
}
