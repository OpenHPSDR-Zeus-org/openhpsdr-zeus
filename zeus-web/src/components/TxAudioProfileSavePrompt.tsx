// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// "TX Audio Profile '{name}' changed — save?" prompt. Shown on the in-app
// Disconnect path when the live TX-audio settings have drifted from the loaded
// profile (see tx-audio-profile-tracker / store dirty flag). Three actions:
//   Save    — overwrite the loaded profile with the live settings, then proceed
//   Discard — proceed without saving (live edits are dropped on disconnect)
//   Cancel  — abort; stay connected so the operator can keep editing
// Reuses the shared confirm-dialog chrome (token-styled) + focus trap.

import { useId, useRef } from 'react';
import { X } from 'lucide-react';

import { useDialogFocusTrap } from '../layout/useDialogFocusTrap';

interface TxAudioProfileSavePromptProps {
  profileName: string;
  busy?: boolean;
  onSave: () => void;
  onDiscard: () => void;
  onCancel: () => void;
}

export function TxAudioProfileSavePrompt({
  profileName,
  busy = false,
  onSave,
  onDiscard,
  onCancel,
}: TxAudioProfileSavePromptProps) {
  const titleId = useId();
  const bodyId = useId();
  const dialogRef = useRef<HTMLDivElement | null>(null);
  const saveRef = useRef<HTMLButtonElement | null>(null);

  useDialogFocusTrap({ dialogRef, initialFocusRef: saveRef, onClose: onCancel });

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
      >
        <div className="confirm-dialog-header">
          <h2 id={titleId}>Unsaved TX Audio Profile</h2>
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
          <p>
            TX Audio Profile <strong>{profileName}</strong> changed, do you wish
            to save?
          </p>
          <p style={{ color: 'var(--fg-2)', fontSize: 12 }}>
            Save overwrites &ldquo;{profileName}&rdquo; with the current live TX
            audio settings. Discard keeps the profile as it was last saved.
          </p>
        </div>
        <div className="confirm-dialog-actions">
          <button type="button" className="btn ghost" onClick={onCancel} disabled={busy}>
            Cancel
          </button>
          <button type="button" className="btn ghost" onClick={onDiscard} disabled={busy}>
            Discard
          </button>
          <button
            ref={saveRef}
            type="button"
            className="btn active"
            onClick={onSave}
            disabled={busy}
          >
            {busy ? 'Saving…' : 'Save'}
          </button>
        </div>
      </div>
    </div>
  );
}
