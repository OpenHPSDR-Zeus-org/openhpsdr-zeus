// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// LayoutSettingsModal — create or manage saved layouts.
//
// Two shapes:
//   • Create mode (no `manager` prop): a simple form that captures a label,
//     icon, description, and lock state for a brand-new blank layout. Used by
//     the LeftLayoutBar "+" slot.
//   • Manager mode (`manager` prop supplied): the form is fronted by a "Saved
//     layouts" dropdown listing every layout for the radio. Picking one
//     switches the workspace to it and re-targets the editor. From here the
//     operator gets the full CRUD set:
//       - Save        → write the edited label/icon/description/lock to the
//                        selected layout (its panel arrangement is already the
//                        live one, so Save also commits the current layout).
//       - Save as new → copy the CURRENT panel arrangement into a brand-new
//                        saved layout under a new name.
//       - Delete      → remove the selected layout (two-click confirm).
//     Renaming is just editing the label and pressing Save.

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
}

/** One entry in the manager dropdown. */
export interface LayoutManagerEntry {
  id: string;
  name: string;
  icon?: string;
  locked: boolean;
}

/** Manager-mode controls. When supplied the modal renders the saved-layouts
 *  dropdown plus the Delete / Save-as-new affordances. */
export interface LayoutManagerControls {
  /** All saved layouts for the current radio, in display order. */
  layouts: LayoutManagerEntry[];
  /** The layout currently being edited (also the active workspace). */
  selectedId: string;
  /** Switch to / edit another saved layout. */
  onSelect: (id: string) => void;
  /** Copy the current panel arrangement into a new saved layout. */
  onSaveAsNew: (value: LayoutSettingsValue) => void;
  /** Delete the selected layout. */
  onDelete: (id: string) => void;
  /** False when only one layout remains (the last can't be deleted). */
  canDelete: boolean;
}

interface LayoutSettingsModalProps {
  /** Modal title. "Layout settings" for manage, "New layout" for create. */
  title: string;
  initial: LayoutSettingsValue;
  onSave: (value: LayoutSettingsValue) => void;
  onClose: () => void;
  /** When supplied, the modal is a saved-layouts manager (dropdown + CRUD). */
  manager?: LayoutManagerControls;
}

export function LayoutSettingsModal({
  title,
  initial,
  onSave,
  onClose,
  manager,
}: LayoutSettingsModalProps) {
  const titleId = useId();
  const [name, setName] = useState(initial.name);
  const [icon, setIcon] = useState(initial.icon);
  const [description, setDescription] = useState(initial.description);
  const [locked, setLocked] = useState(initial.locked);
  // Manager-only transient state.
  const [newName, setNewName] = useState('');
  const [confirmDelete, setConfirmDelete] = useState(false);
  const dialogRef = useRef<HTMLDivElement | null>(null);
  const nameRef = useRef<HTMLInputElement | null>(null);

  useDialogFocusTrap({
    dialogRef,
    initialFocusRef: nameRef,
    onClose,
  });

  // When the operator picks a different layout from the dropdown, the parent
  // re-derives `initial` from the newly-selected layout — resync the editable
  // fields to it. Keyed on the selection id so it never clobbers in-progress
  // typing (the stored values only change on Save).
  useEffect(() => {
    setName(initial.name);
    setIcon(initial.icon);
    setDescription(initial.description);
    setLocked(initial.locked);
    setNewName('');
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
    });
  };

  const commitSaveAsNew = () => {
    const trimmed = newName.trim();
    if (!trimmed || !manager) return;
    manager.onSaveAsNew({
      name: trimmed,
      icon: icon.trim(),
      description: description.trim(),
      locked,
    });
    setNewName('');
  };

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) handleSave();
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
          {manager && (
            <label className="layout-settings-field">
              <span className="layout-settings-field-label">Saved layouts</span>
              <select
                className="layout-settings-input layout-settings-select"
                value={manager.selectedId}
                onChange={(e) => manager.onSelect(e.target.value)}
                aria-label="Saved layouts"
              >
                {manager.layouts.map((l) => (
                  <option key={l.id} value={l.id}>
                    {(l.icon ? `${l.icon}  ` : '') + l.name}
                    {l.locked ? '  🔒' : ''}
                  </option>
                ))}
              </select>
              <span className="layout-settings-field-hint">
                Pick a layout to switch the workspace to it and edit it below.
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

          {manager && (
            <div className="layout-settings-field layout-settings-saveas">
              <span className="layout-settings-field-label">
                Save as new layout
              </span>
              <div className="layout-settings-icon-row">
                <input
                  type="text"
                  className="layout-settings-input"
                  value={newName}
                  maxLength={24}
                  onChange={(e) => setNewName(e.target.value)}
                  onKeyDown={(e) => {
                    if (e.key === 'Enter') {
                      e.preventDefault();
                      commitSaveAsNew();
                    }
                  }}
                  placeholder="New layout name"
                  aria-label="New layout name"
                />
                <button
                  type="button"
                  className="btn ghost sm"
                  disabled={!newName.trim()}
                  onClick={commitSaveAsNew}
                  title="Copy the current panel arrangement into a new saved layout"
                >
                  Create
                </button>
              </div>
              <span className="layout-settings-field-hint">
                Copies the current panel arrangement into a new saved layout.
              </span>
            </div>
          )}
        </div>

        <div className="layout-settings-actions">
          {manager && (
            <button
              type="button"
              className="btn ghost layout-settings-delete"
              disabled={!manager.canDelete}
              onClick={() => {
                if (!confirmDelete) {
                  setConfirmDelete(true);
                  return;
                }
                manager.onDelete(manager.selectedId);
              }}
              onBlur={() => setConfirmDelete(false)}
              title={
                manager.canDelete
                  ? 'Delete this saved layout'
                  : 'The last layout can’t be deleted'
              }
            >
              {confirmDelete ? 'Confirm delete?' : 'Delete'}
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
            Save
          </button>
        </div>
      </div>
    </div>
  );
}

function initialLetter(name: string): string {
  const ch = name.trim().charAt(0);
  return ch ? ch.toUpperCase() : '·';
}
