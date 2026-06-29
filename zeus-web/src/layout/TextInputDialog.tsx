// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.

import { useId, useRef, useState, type ReactNode } from 'react';
import { X } from 'lucide-react';
import { useDialogFocusTrap } from './useDialogFocusTrap';

interface TextInputDialogProps {
  title: string;
  label: string;
  initialValue?: string;
  placeholder?: string;
  confirmLabel: string;
  cancelLabel?: string;
  children?: ReactNode;
  onSubmit: (value: string) => void;
  onCancel: () => void;
}

export function TextInputDialog({
  title,
  label,
  initialValue = '',
  placeholder,
  confirmLabel,
  cancelLabel = 'Cancel',
  children,
  onSubmit,
  onCancel,
}: TextInputDialogProps) {
  const titleId = useId();
  const bodyId = useId();
  const labelId = useId();
  const [value, setValue] = useState(initialValue);
  const dialogRef = useRef<HTMLDivElement | null>(null);
  const inputRef = useRef<HTMLInputElement | null>(null);

  useDialogFocusTrap({
    dialogRef,
    initialFocusRef: inputRef,
    onClose: onCancel,
  });

  const submit = () => {
    const trimmed = value.trim();
    if (!trimmed) return;
    onSubmit(trimmed);
  };

  return (
    <div className="modal-backdrop confirm-dialog-backdrop">
      <div
        ref={dialogRef}
        className="confirm-dialog confirm-dialog--primary"
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        aria-describedby={bodyId}
        tabIndex={-1}
        onClick={(e) => e.stopPropagation()}
        onKeyDown={(e) => {
          if (e.key === 'Enter' && e.target === inputRef.current) submit();
        }}
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
        <div id={bodyId} className="confirm-dialog-body">
          {children}
          <label className="text-input-dialog-field">
            <span id={labelId}>{label}</span>
            <input
              ref={inputRef}
              type="text"
              value={value}
              placeholder={placeholder}
              aria-labelledby={labelId}
              onChange={(e) => setValue(e.target.value)}
            />
          </label>
        </div>
        <div className="confirm-dialog-actions">
          <button type="button" className="btn ghost" onClick={onCancel}>
            {cancelLabel}
          </button>
          <button
            type="button"
            className="btn active"
            onClick={submit}
            disabled={!value.trim()}
          >
            {confirmLabel}
          </button>
        </div>
      </div>
    </div>
  );
}
