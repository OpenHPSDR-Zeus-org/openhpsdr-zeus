// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// Workspace-tile chrome: 24 px header strip with a drag-handle grip on the
// left, the panel title in the middle, and an X remove button on the right.
// The drag handle's CSS class (`.workspace-tile-drag-handle`) is the only
// element RGL listens to via `dragConfig.handle` — clicks on the title or
// inside the panel body don't initiate a drag, so the panel's own controls
// keep working.

import { GripVertical, LockKeyhole, LockKeyholeOpen, X } from 'lucide-react';
import type { ReactNode } from 'react';

export interface TileChromeProps {
  title: string;
  onRemove: () => void;
  /** Optional extra header buttons rendered between the title and the
   *  remove X (e.g. a panel-specific gear icon). */
  rightSlot?: ReactNode;
  locked?: boolean;
  workspaceLocked?: boolean;
  onToggleLock?: () => void;
}

export function TileChrome({
  title,
  onRemove,
  rightSlot,
  locked = false,
  workspaceLocked = false,
  onToggleLock,
}: TileChromeProps) {
  const effectiveLocked = locked || workspaceLocked;
  return (
    <div className="workspace-tile-header">
      <span
        className="workspace-tile-drag-handle"
        aria-hidden="true"
        title={effectiveLocked ? 'Panel position is locked' : 'Drag to reposition'}
      >
        <GripVertical size={12} />
      </span>
      <span className="workspace-tile-title" title={title}>
        {title}
      </span>
      {rightSlot}
      {onToggleLock ? (
        <TileLockButton
          locked={locked}
          workspaceLocked={workspaceLocked}
          onToggleLock={onToggleLock}
        />
      ) : null}
      <button
        type="button"
        className="workspace-tile-close"
        aria-label={`Remove ${title}`}
        title="Remove panel"
        onClick={(e) => {
          e.stopPropagation();
          onRemove();
        }}
        onPointerDown={(e) => e.stopPropagation()}
        onMouseDown={(e) => e.stopPropagation()}
      >
        <X size={12} />
      </button>
    </div>
  );
}

interface TileLockButtonProps {
  locked: boolean;
  workspaceLocked?: boolean;
  onToggleLock: () => void;
}

export function TileLockButton({
  locked,
  workspaceLocked = false,
  onToggleLock,
}: TileLockButtonProps) {
  const effectiveLocked = locked || workspaceLocked;
  const Icon = effectiveLocked ? LockKeyhole : LockKeyholeOpen;
  const title = locked
    ? 'Unlock panel position'
    : workspaceLocked
      ? 'Workspace is locked; click to also lock this panel'
      : 'Lock panel to this grid space';
  return (
    <button
      type="button"
      className={`workspace-tile-lock${locked ? ' is-locked' : ''}${
        !locked && workspaceLocked ? ' is-inherited' : ''
      }`}
      aria-label={locked ? 'Unlock panel position' : 'Lock panel position'}
      aria-pressed={locked}
      title={title}
      onClick={(e) => {
        e.stopPropagation();
        onToggleLock();
      }}
      onPointerDown={(e) => e.stopPropagation()}
      onMouseDown={(e) => e.stopPropagation()}
    >
      <Icon size={12} aria-hidden />
    </button>
  );
}
