// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// LayoutSettingsModal — create a workspace or manage workspaces + the
// saved-layouts library.
//
// Two concepts, deliberately separate:
//   • Workspace (tab)  — a live arrangement in the LeftLayoutBar. Switching
//                        tabs swaps the workspace. Editing one and pressing
//                        Save updates it IN PLACE; it never spawns a new tab.
//   • Saved layout     — a reusable PRESET in a per-radio library. The
//                        operator snapshots a good workspace into a saved
//                        layout so they can restore it if they mess the live
//                        tab up, or seed a brand-new workspace from it.
//
// Two shapes:
//   • Create mode (`createSource` supplied): "New workspace" — name/icon/etc.
//     plus a "Start from" picker (Blank, or copy any saved layout).
//   • Manager mode (`manager` supplied): edit the current workspace's
//     metadata + lock, switch between workspaces, and run the full
//     saved-layouts library CRUD (save current → library, apply, replace,
//     rename, delete).

import { useEffect, useId, useRef, useState } from 'react';
import { X } from 'lucide-react';
import { useDialogFocusTrap } from './useDialogFocusTrap';

const ICON_PALETTE = [
  '📡', '🎙', '📻', '🎧', '🛰', '📶',
  '⚡', '🌐', '🌍', '🌅', '🌙', '☀️',
  '⭐', '🏠', '🚗', '🏕', '⛰', '🌊',
  '🎯', '📊', '📈', '🔧', '🔬', '🎛',
  '🔵', '🟢', '🟡', '🟠', '🔴', '🟣',
];

export interface LayoutSettingsValue {
  name: string;
  icon: string;
  description: string;
  locked: boolean;
  /** When true, the operator wants this workspace to take over the screen
   *  while MOX/TUN is keyed and revert on un-key. Only one workspace per
   *  radio holds this flag — the layout-store enforces uniqueness on save. */
  autoSwitchOnTx: boolean;
}

/** One workspace tab in the manager dropdown. */
export interface LayoutManagerEntry {
  id: string;
  name: string;
  icon?: string;
  locked: boolean;
  autoSwitchOnTx: boolean;
}

/** One reusable preset in the saved-layouts library. */
export interface SavedLayoutEntry {
  id: string;
  name: string;
  icon?: string;
  description?: string;
  updatedUtc: number;
}

/** Create-mode "Start from" picker — choose a blank workspace or clone the
 *  arrangement (and metadata) of an existing saved layout. */
export interface CreateSourceControls {
  savedLayouts: SavedLayoutEntry[];
  /** '' = blank, otherwise a saved-layout id. */
  sourceId: string;
  onSourceChange: (id: string) => void;
}

/** Manager-mode controls: workspace switcher + saved-layouts library CRUD. */
export interface LayoutManagerControls {
  /** All workspace tabs for the current radio, in display order. */
  workspaces: LayoutManagerEntry[];
  /** The workspace currently being edited (also the active one). */
  selectedId: string;
  /** Switch to / edit another workspace. */
  onSelectWorkspace: (id: string) => void;
  /** Delete the selected workspace tab. */
  onDeleteWorkspace: (id: string) => void;
  /** False when only one workspace remains (the last can't be deleted). */
  canDeleteWorkspace: boolean;

  /** The reusable saved-layout presets for this radio. */
  savedLayouts: SavedLayoutEntry[];
  /** Snapshot the current workspace into a NEW saved layout. */
  onSaveWorkspaceToLibrary: (name: string) => void;
  /** Apply (restore) a saved layout onto the current workspace. */
  onApplySaved: (id: string) => void;
  /** Overwrite a saved layout with the current workspace arrangement. */
  onReplaceSaved: (id: string) => void;
  /** Rename a saved layout. */
  onRenameSaved: (id: string, name: string) => void;
  /** Delete a saved layout from the library. */
  onDeleteSaved: (id: string) => void;
}

interface LayoutSettingsModalProps {
  /** Modal title. "Layout settings" for manage, "New workspace" for create. */
  title: string;
  initial: LayoutSettingsValue;
  onSave: (value: LayoutSettingsValue) => void;
  onClose: () => void;
  /** Label for the primary action button. Defaults to "Save". */
  saveLabel?: string;
  /** When supplied, the modal is in create mode with a "Start from" picker. */
  createSource?: CreateSourceControls;
  /** When supplied, the modal is a workspace + saved-layouts manager. */
  manager?: LayoutManagerControls;
}

export function LayoutSettingsModal({
  title,
  initial,
  onSave,
  onClose,
  saveLabel = 'Save',
  createSource,
  manager,
}: LayoutSettingsModalProps) {
  const titleId = useId();
  const [name, setName] = useState(initial.name);
  const [icon, setIcon] = useState(initial.icon);
  const [description, setDescription] = useState(initial.description);
  const [locked, setLocked] = useState(initial.locked);
  const [autoSwitchOnTx, setAutoSwitchOnTx] = useState(initial.autoSwitchOnTx);
  // Manager-only transient state.
  const [confirmDelete, setConfirmDelete] = useState(false);
  const dialogRef = useRef<HTMLDivElement | null>(null);
  const nameRef = useRef<HTMLInputElement | null>(null);

  useDialogFocusTrap({
    dialogRef,
    initialFocusRef: nameRef,
    onClose,
  });

  // When the operator picks a different workspace from the dropdown, the parent
  // re-derives `initial` from the newly-selected one — resync the editable
  // fields to it. Keyed on the selection id so it never clobbers in-progress
  // typing (the stored values only change on Save).
  useEffect(() => {
    setName(initial.name);
    setIcon(initial.icon);
    setDescription(initial.description);
    setLocked(initial.locked);
    setAutoSwitchOnTx(initial.autoSwitchOnTx);
    setConfirmDelete(false);
    // Resync only when the managed selection changes, not on every keystroke.
  }, [manager?.selectedId]); // eslint-disable-line react-hooks/exhaustive-deps

  const handleSave = () => {
    const trimmedName = name.trim();
    if (!trimmedName) return;
    onSave({
      name: trimmedName,
      icon: icon.trim(),
      description: description.trim(),
      locked,
      autoSwitchOnTx,
    });
  };

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) handleSave();
  };

  // Create-mode "Start from" change: cloning a saved layout pre-fills the
  // metadata fields from it so the new workspace reads as a copy. Picking
  // "Blank" leaves whatever the operator has typed.
  const handleSourceChange = (id: string) => {
    if (!createSource) return;
    createSource.onSourceChange(id);
    const src = createSource.savedLayouts.find((l) => l.id === id);
    if (src) {
      setName(src.name);
      setIcon(src.icon ?? '');
      setDescription(src.description ?? '');
    }
  };

  return (
    <div
      className="modal-backdrop layout-settings-backdrop"
      style={{
        position: 'fixed',
        inset: 0,
        background: 'rgba(0, 0, 0, 0.7)',
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        zIndex: 10000,
      }}
      onClick={onClose}
    >
      <div
        ref={dialogRef}
        className="layout-settings-modal"
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        tabIndex={-1}
        onClick={(e) => e.stopPropagation()}
        onKeyDown={handleKeyDown}
      >
        <div className="layout-settings-header">
          <h2 id={titleId}>{title}</h2>
          <button
            type="button"
            className="workspace-tile-close"
            aria-label="Close layout settings"
            title="Close (Esc)"
            onClick={onClose}
            style={{ width: 22, height: 22 }}
          >
            <X size={12} aria-hidden />
          </button>
        </div>

        <div className="layout-settings-body">
          {createSource && (
            <label className="layout-settings-field">
              <span className="layout-settings-field-label">Start from</span>
              <select
                className="layout-settings-input layout-settings-select"
                value={createSource.sourceId}
                onChange={(e) => handleSourceChange(e.target.value)}
                aria-label="Start from"
              >
                <option value="">Blank workspace</option>
                {createSource.savedLayouts.length > 0 && (
                  <optgroup label="Copy a saved layout">
                    {createSource.savedLayouts.map((l) => (
                      <option key={l.id} value={l.id}>
                        {(l.icon ? `${l.icon}  ` : '') + l.name}
                      </option>
                    ))}
                  </optgroup>
                )}
              </select>
              <span className="layout-settings-field-hint">
                Start blank, or copy the panel arrangement of a saved layout.
              </span>
            </label>
          )}

          {manager && (
            <label className="layout-settings-field">
              <span className="layout-settings-field-label">Workspace</span>
              <select
                className="layout-settings-input layout-settings-select"
                value={manager.selectedId}
                onChange={(e) => manager.onSelectWorkspace(e.target.value)}
                aria-label="Workspace"
              >
                {manager.workspaces.map((l) => (
                  <option key={l.id} value={l.id}>
                    {(l.icon ? `${l.icon}  ` : '') + l.name}
                    {l.locked ? '  🔒' : ''}
                  </option>
                ))}
              </select>
              <span className="layout-settings-field-hint">
                Pick a workspace to switch to it and edit it below.
              </span>
            </label>
          )}

          <div className="layout-settings-preview" aria-hidden>
            <div className="layout-settings-preview-tile">
              <span className="layout-settings-preview-icon">
                {icon || initialLetter(name)}
              </span>
              <span className="layout-settings-preview-label">
                {(name || 'Layout').slice(0, 12)}
              </span>
            </div>
          </div>

          <label className="layout-settings-field">
            <span className="layout-settings-field-label">Label</span>
            <input
              ref={nameRef}
              type="text"
              className="layout-settings-input"
              value={name}
              maxLength={24}
              onChange={(e) => setName(e.target.value)}
              placeholder="e.g. SOTA"
              aria-label="Layout label"
            />
            <span className="layout-settings-field-hint">
              Short — appears below the icon. Edit it and Save to rename.
            </span>
          </label>

          <div className="layout-settings-field">
            <span className="layout-settings-field-label">Icon</span>
            <div className="layout-settings-icon-row">
              <input
                type="text"
                className="layout-settings-icon-input"
                value={icon}
                maxLength={8}
                onChange={(e) => setIcon(e.target.value)}
                placeholder="📡"
                aria-label="Layout icon (emoji)"
              />
              {icon && (
                <button
                  type="button"
                  className="btn ghost sm"
                  onClick={() => setIcon('')}
                  aria-label="Clear icon"
                  title="Clear icon"
                >
                  Clear
                </button>
              )}
            </div>
            <div
              className="layout-settings-icon-grid"
              role="listbox"
              aria-label="Suggested icons"
            >
              {ICON_PALETTE.map((emoji) => {
                const selected = emoji === icon;
                return (
                  <button
                    key={emoji}
                    type="button"
                    className={`layout-settings-icon-chip ${
                      selected ? 'selected' : ''
                    }`}
                    role="option"
                    aria-selected={selected}
                    onClick={() => setIcon(emoji)}
                    title={emoji}
                  >
                    {emoji}
                  </button>
                );
              })}
            </div>
            <span className="layout-settings-field-hint">
              Pick from the palette, or paste any emoji
              (macOS: ⌃⌘Space, Windows: Win + .)
            </span>
          </div>

          <label className="layout-settings-field">
            <span className="layout-settings-field-label">Description</span>
            <textarea
              className="layout-settings-textarea"
              value={description}
              maxLength={256}
              rows={2}
              onChange={(e) => setDescription(e.target.value)}
              placeholder="Shown on hover — e.g. Portable HF, 5 W, no rotator"
              aria-label="Layout description"
            />
          </label>

          <label className="layout-settings-lock-row">
            <input
              type="checkbox"
              checked={locked}
              aria-label="Lock panel positions"
              onChange={(e) => setLocked(e.target.checked)}
            />
            <span className="layout-settings-lock-copy">
              <span className="layout-settings-field-label">
                Lock panel positions
              </span>
              <span className="layout-settings-field-hint">
                Panels stay pinned in this workspace.
              </span>
            </span>
          </label>

          <AutoSwitchOnTxRow
            checked={autoSwitchOnTx}
            onChange={setAutoSwitchOnTx}
            currentHolder={
              manager && !autoSwitchOnTx
                ? manager.workspaces.find(
                    (w) => w.autoSwitchOnTx && w.id !== manager.selectedId,
                  )
                : undefined
            }
          />

          {manager && <SavedLayoutsLibrary manager={manager} />}
        </div>

        <div className="layout-settings-actions">
          {manager && (
            <button
              type="button"
              className="btn ghost layout-settings-delete"
              disabled={!manager.canDeleteWorkspace}
              onClick={() => {
                if (!confirmDelete) {
                  setConfirmDelete(true);
                  return;
                }
                manager.onDeleteWorkspace(manager.selectedId);
              }}
              onBlur={() => setConfirmDelete(false)}
              title={
                manager.canDeleteWorkspace
                  ? 'Delete this workspace'
                  : 'The last workspace can’t be deleted'
              }
            >
              {confirmDelete ? 'Confirm delete?' : 'Delete workspace'}
            </button>
          )}
          <span className="layout-settings-actions-spacer" />
          <button type="button" className="btn ghost" onClick={onClose}>
            Cancel
          </button>
          <button
            type="button"
            className="btn active"
            onClick={handleSave}
            disabled={!name.trim()}
          >
            {saveLabel}
          </button>
        </div>
      </div>
    </div>
  );
}

/** Saved-layouts library: snapshot the current workspace into a preset, then
 *  apply / replace / rename / delete entries. Rendered inside manager mode. */
function SavedLayoutsLibrary({ manager }: { manager: LayoutManagerControls }) {
  const [newName, setNewName] = useState('');
  // Per-row inline rename state.
  const [renamingId, setRenamingId] = useState<string | null>(null);
  const [renameText, setRenameText] = useState('');
  // Two-click confirm for the destructive row actions.
  const [confirm, setConfirm] = useState<{ id: string; action: 'apply' | 'delete' } | null>(null);

  const commitSave = () => {
    const trimmed = newName.trim();
    if (!trimmed) return;
    manager.onSaveWorkspaceToLibrary(trimmed);
    setNewName('');
  };

  const startRename = (entry: SavedLayoutEntry) => {
    setRenamingId(entry.id);
    setRenameText(entry.name);
    setConfirm(null);
  };

  const commitRename = (id: string) => {
    const trimmed = renameText.trim();
    if (trimmed) manager.onRenameSaved(id, trimmed);
    setRenamingId(null);
    setRenameText('');
  };

  const armed = (id: string, action: 'apply' | 'delete') =>
    confirm?.id === id && confirm.action === action;

  return (
    <div className="layout-settings-field layout-settings-library">
      <span className="layout-settings-field-label">Saved layouts</span>
      <span className="layout-settings-field-hint">
        Back up the current workspace as a reusable layout — restore or copy it
        any time.
      </span>

      <div className="layout-settings-icon-row layout-settings-library-save">
        <input
          type="text"
          className="layout-settings-input"
          value={newName}
          maxLength={24}
          onChange={(e) => setNewName(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter') {
              e.preventDefault();
              commitSave();
            }
          }}
          placeholder="Save current workspace as…"
          aria-label="New saved-layout name"
        />
        <button
          type="button"
          className="btn ghost sm"
          disabled={!newName.trim()}
          onClick={commitSave}
          title="Snapshot the current workspace into a new saved layout"
        >
          Save
        </button>
      </div>

      {manager.savedLayouts.length === 0 ? (
        <div className="layout-settings-library-empty">
          No saved layouts yet. Save one above to back up this workspace.
        </div>
      ) : (
        <ul className="layout-settings-library-list">
          {manager.savedLayouts.map((entry) => (
            <li key={entry.id} className="layout-settings-library-item">
              {renamingId === entry.id ? (
                <input
                  type="text"
                  className="layout-settings-input layout-settings-library-rename"
                  value={renameText}
                  maxLength={24}
                  autoFocus
                  onChange={(e) => setRenameText(e.target.value)}
                  onKeyDown={(e) => {
                    if (e.key === 'Enter') {
                      e.preventDefault();
                      commitRename(entry.id);
                    } else if (e.key === 'Escape') {
                      e.preventDefault();
                      setRenamingId(null);
                    }
                  }}
                  onBlur={() => commitRename(entry.id)}
                  aria-label={`Rename ${entry.name}`}
                />
              ) : (
                <span className="layout-settings-library-name" title={entry.description}>
                  <span className="layout-settings-library-icon" aria-hidden>
                    {entry.icon || initialLetter(entry.name)}
                  </span>
                  {entry.name}
                </span>
              )}
              <span className="layout-settings-library-actions">
                <button
                  type="button"
                  className={`btn ghost xs ${armed(entry.id, 'apply') ? 'is-confirm' : ''}`}
                  onClick={() => {
                    if (!armed(entry.id, 'apply')) {
                      setConfirm({ id: entry.id, action: 'apply' });
                      return;
                    }
                    manager.onApplySaved(entry.id);
                    setConfirm(null);
                  }}
                  onBlur={() => setConfirm((c) => (c?.id === entry.id && c.action === 'apply' ? null : c))}
                  title="Replace the current workspace with this saved layout"
                >
                  {armed(entry.id, 'apply') ? 'Replace now?' : 'Apply'}
                </button>
                <button
                  type="button"
                  className="btn ghost xs"
                  onClick={() => manager.onReplaceSaved(entry.id)}
                  title="Overwrite this saved layout with the current workspace"
                >
                  Replace
                </button>
                <button
                  type="button"
                  className="btn ghost xs"
                  onClick={() => startRename(entry)}
                  title="Rename this saved layout"
                >
                  Rename
                </button>
                <button
                  type="button"
                  className={`btn ghost xs layout-settings-delete ${armed(entry.id, 'delete') ? 'is-confirm' : ''}`}
                  onClick={() => {
                    if (!armed(entry.id, 'delete')) {
                      setConfirm({ id: entry.id, action: 'delete' });
                      return;
                    }
                    manager.onDeleteSaved(entry.id);
                    setConfirm(null);
                  }}
                  onBlur={() => setConfirm((c) => (c?.id === entry.id && c.action === 'delete' ? null : c))}
                  title="Delete this saved layout"
                >
                  {armed(entry.id, 'delete') ? 'Confirm?' : 'Delete'}
                </button>
              </span>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function AutoSwitchOnTxRow({
  checked,
  onChange,
  currentHolder,
}: {
  checked: boolean;
  onChange: (next: boolean) => void;
  currentHolder?: LayoutManagerEntry;
}) {
  return (
    <label className="layout-settings-lock-row">
      <input
        type="checkbox"
        checked={checked}
        aria-label="Auto-switch to this workspace when transmitting"
        onChange={(e) => onChange(e.target.checked)}
      />
      <span className="layout-settings-lock-copy">
        <span className="layout-settings-field-label">
          Auto-switch to this workspace when transmitting
        </span>
        <span className="layout-settings-field-hint">
          {currentHolder
            ? `Currently set on "${currentHolder.name}" — ticking this will move it here.`
            : 'On MOX/TUN/PTT, Zeus shows this workspace; on release it returns to whatever you were on.'}
        </span>
      </span>
    </label>
  );
}

function initialLetter(name: string): string {
  const ch = name.trim().charAt(0);
  return ch ? ch.toUpperCase() : '·';
}
