// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

import { useId, useRef, type ReactNode } from 'react';
import { X } from 'lucide-react';
import { useDialogFocusTrap } from './useDialogFocusTrap';

interface ConfirmDialogProps {
  title: string;
  children: ReactNode;
  confirmLabel: string;
  cancelLabel?: string;
  intent?: 'danger' | 'primary';
  onConfirm: () => void;
  onCancel: () => void;
}

export function ConfirmDialog({
  title,
  children,
  confirmLabel,
  cancelLabel = 'Cancel',
  intent = 'danger',
  onConfirm,
  onCancel,
}: ConfirmDialogProps) {
  const titleId = useId();
  const bodyId = useId();
  const dialogRef = useRef<HTMLDivElement | null>(null);
  const cancelRef = useRef<HTMLButtonElement | null>(null);

  useDialogFocusTrap({
    dialogRef,
    initialFocusRef: cancelRef,
    onClose: onCancel,
  });

  return (
    <div className="modal-backdrop confirm-dialog-backdrop">
      <div
        ref={dialogRef}
        className={`confirm-dialog confirm-dialog--${intent}`}
        role={intent === 'danger' ? 'alertdialog' : 'dialog'}
        aria-modal="true"
        aria-labelledby={titleId}
        aria-describedby={bodyId}
        tabIndex={-1}
        onClick={(e) => e.stopPropagation()}
      >
        <div className="confirm-dialog-header">
          <h2 id={titleId}>{title}</h2>
          <button
            type="button"
            className="workspace-tile-close"
            aria-label="Close dialog"
            title="Close (Esc)"
            onClick={onCancel}
            style={{ width: 22, height: 22 }}
          >
            <X size={12} aria-hidden />
          </button>
        </div>
        <div id={bodyId} className="confirm-dialog-body">{children}</div>
        <div className="confirm-dialog-actions">
          <button
            ref={cancelRef}
            type="button"
            className="btn ghost"
            onClick={onCancel}
          >
            {cancelLabel}
          </button>
          <button
            type="button"
            className={`btn ${intent === 'danger' ? 'danger' : 'active'}`}
            onClick={onConfirm}
          >
            {confirmLabel}
          </button>
        </div>
      </div>
    </div>
  );
}
