// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// LayoutSettingsModal — edit the presentation metadata of a NamedLayout.
// Replaces the double-click-to-rename interaction in the LeftLayoutBar with
// a small modal that captures: short label (what shows under the icon), the
// icon itself (an emoji selected from a quick palette or freely typed), and
// a longer description used as the hover tooltip.
//
// The same modal is reused for "create new layout" — when no layout id is
// supplied the parent treats Save as addLayout(name, {icon, description}).

import { useId, useRef, useState } from 'react';
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

interface LayoutSettingsModalProps {
  /** Modal title. "Layout settings" for edit, "New layout" for create. */
  title: string;
  initial: LayoutSettingsValue;
  onSave: (value: LayoutSettingsValue) => void;
  onClose: () => void;
}

export function LayoutSettingsModal({
  title,
  initial,
  onSave,
  onClose,
}: LayoutSettingsModalProps) {
  const titleId = useId();
  const [name, setName] = useState(initial.name);
  const [icon, setIcon] = useState(initial.icon);
  const [description, setDescription] = useState(initial.description);
  const [locked, setLocked] = useState(initial.locked);
  const dialogRef = useRef<HTMLDivElement | null>(null);
  const nameRef = useRef<HTMLInputElement | null>(null);

  useDialogFocusTrap({
    dialogRef,
    initialFocusRef: nameRef,
    onClose,
  });

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
              Short — appears below the icon.
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
        </div>

        <div className="layout-settings-actions">
          <button
            type="button"
            className="btn ghost"
            onClick={onClose}
          >
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
