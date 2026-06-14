// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// Add Panel modal — categorized version. Left rail of category chips, right
// pane of panel cards filtered by (selectedCategory, searchTerm). Replaces
// the previous flat-list modal that came in via `feature/meters-panel`.
//
// Category drill UX (locked decision per task #6 prompt): clicking a
// category just FILTERS the right pane; it does NOT push a second modal.
// Clicking a card adds the panel and closes the modal. For multi-instance
// panels (Meters today), duplicates are allowed and a "+ Add another" badge
// labels the card when an instance already exists.

import { useId, useRef, useState } from 'react';
import { X } from 'lucide-react';
import {
  getAllPanels,
  PANEL_CATEGORIES,
  PANEL_CATEGORY_LABELS,
  type PanelCategory,
} from './panels';
import { usePluginPanels } from '../plugins/runtime/usePluginPanels';
import { useDialogFocusTrap } from './useDialogFocusTrap';

interface AddPanelModalProps {
  /** Set of panelIds currently in the workspace (one entry per id, regardless
   *  of how many tiles use it). Drives the "+ Add another" multi-instance
   *  badge and the "already added" filtering for single-instance panels. */
  existingPanels: Set<string>;
  onAdd: (panelId: string) => void;
  onClose: () => void;
}

type CategoryFilter = PanelCategory | 'all';

export function AddPanelModal({ existingPanels, onAdd, onClose }: AddPanelModalProps) {
  const titleId = useId();
  const dialogRef = useRef<HTMLDivElement | null>(null);
  const searchRef = useRef<HTMLInputElement | null>(null);
  const [searchTerm, setSearchTerm] = useState('');
  const [selectedCategory, setSelectedCategory] =
    useState<CategoryFilter>('all');
  // Subscribe to plugin runtime so the picker re-renders when plugin
  // panels load (or change after install/uninstall). Return value is
  // unused — getAllPanels() reads the same registry directly.
  usePluginPanels();
  useDialogFocusTrap({
    dialogRef,
    initialFocusRef: searchRef,
    onClose,
  });

  const availablePanels = getAllPanels().filter((panel) => {
    // Single-instance panels disappear from the list once added; multi-
    // instance panels stay visible (the badge changes to "+ Add another").
    if (existingPanels.has(panel.id) && !panel.multiInstance) return false;

    if (selectedCategory !== 'all' && panel.category !== selectedCategory)
      return false;

    if (searchTerm) {
      const term = searchTerm.toLowerCase();
      return (
        panel.name.toLowerCase().includes(term) ||
        panel.tags.some((tag) => tag.toLowerCase().includes(term))
      );
    }
    return true;
  });

  return (
    <div
      className="modal-backdrop"
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
        className="add-panel-modal"
        onClick={(e) => e.stopPropagation()}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        tabIndex={-1}
      >
        <div className="add-panel-modal-header">
          <h2 id={titleId}>Add Panel</h2>
          <button
            type="button"
            className="workspace-tile-close"
            aria-label="Close add-panel modal"
            title="Close (Esc)"
            onClick={onClose}
            style={{ width: 22, height: 22 }}
          >
            <X size={12} aria-hidden />
          </button>
        </div>

        <div className="add-panel-modal-rail" data-testid="add-panel-rail">
          <button
            type="button"
            className="add-panel-category-btn"
            aria-pressed={selectedCategory === 'all'}
            onClick={() => setSelectedCategory('all')}
            data-testid="add-panel-category-all"
          >
            All
          </button>
          {PANEL_CATEGORIES.map((cat) => (
            <button
              key={cat}
              type="button"
              className="add-panel-category-btn"
              aria-pressed={selectedCategory === cat}
              onClick={() => setSelectedCategory(cat)}
              data-testid={`add-panel-category-${cat}`}
            >
              {PANEL_CATEGORY_LABELS[cat]}
            </button>
          ))}
        </div>

        <div className="add-panel-modal-body">
          <input
            ref={searchRef}
            type="text"
            className="add-panel-search"
            placeholder="Search panels…"
            value={searchTerm}
            onChange={(e) => setSearchTerm(e.target.value)}
            aria-label="Search panels"
          />

          <div className="add-panel-cards" data-testid="add-panel-cards">
            {availablePanels.length === 0 ? (
              <div className="add-panel-empty">
                {selectedCategory !== 'all' || searchTerm
                  ? 'No panels match'
                  : 'All panels are already in the layout'}
              </div>
            ) : (
              availablePanels.map((panel) => {
                const showMultiBadge =
                  panel.multiInstance && existingPanels.has(panel.id);
                return (
                  <button
                    key={panel.id}
                    type="button"
                    className="add-panel-card"
                    data-panel-id={panel.id}
                    onClick={() => {
                      onAdd(panel.id);
                      onClose();
                    }}
                  >
                    <span className="add-panel-card-title">
                      {panel.name}
                      {showMultiBadge && (
                        <span className="add-panel-card-title-multi">
                          + Add another
                        </span>
                      )}
                    </span>
                    <span className="add-panel-card-tags">
                      {panel.tags.join(' · ')}
                    </span>
                  </button>
                );
              })
            )}
          </div>
        </div>
      </div>
    </div>
  );
}
