// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

import { useEffect, useId, useRef, useState } from 'react';
import { X } from 'lucide-react';
import { useDialogFocusTrap } from '../layout/useDialogFocusTrap';
import {
  resolveTxBandWarning,
  subscribeTxBandWarning,
  type TxBandWarningRequest,
} from './tx-band-preflight';

export function TxBandSafetyDialog() {
  const titleId = useId();
  const bodyId = useId();
  const dialogRef = useRef<HTMLDivElement | null>(null);
  const cancelRef = useRef<HTMLButtonElement | null>(null);
  const [request, setRequest] = useState<TxBandWarningRequest | null>(null);
  const [dontShowAgain, setDontShowAgain] = useState(false);

  useEffect(() => subscribeTxBandWarning((next) => {
    setRequest(next);
    setDontShowAgain(false);
  }), []);

  useDialogFocusTrap({
    dialogRef,
    initialFocusRef: cancelRef,
    onClose: () => resolveTxBandWarning(false),
  });

  if (!request) return null;

  return (
    <div className="modal-backdrop confirm-dialog-backdrop">
      <div
        ref={dialogRef}
        className="confirm-dialog confirm-dialog--primary"
        role="alertdialog"
        aria-modal="true"
        aria-labelledby={titleId}
        aria-describedby={bodyId}
        tabIndex={-1}
        onClick={(e) => e.stopPropagation()}
      >
        <div className="confirm-dialog-header">
          <h2 id={titleId}>Confirm new TX band</h2>
          <button
            type="button"
            className="workspace-tile-close"
            aria-label="Close dialog"
            title="Close (Esc)"
            onClick={() => resolveTxBandWarning(false)}
            style={{ width: 22, height: 22 }}
          >
            <X size={12} aria-hidden />
          </button>
        </div>
        <div id={bodyId} className="confirm-dialog-body">
          <p>
            TX is moving to {request.band} on VFO {request.txVfo} at{' '}
            {(request.freqHz / 1e6).toFixed(6)} MHz.
          </p>
          <p>
            If you are using an amplifier, make sure the amp and antenna system
            are tuned for this band before transmitting.
          </p>
          <label className="chip" style={{ display: 'inline-flex', marginTop: 6 }}>
            <input
              type="checkbox"
              checked={dontShowAgain}
              onChange={(e) => setDontShowAgain(e.currentTarget.checked)}
            />
            <span className="k">Don&apos;t show again</span>
          </label>
        </div>
        <div className="confirm-dialog-actions">
          <button
            ref={cancelRef}
            type="button"
            className="btn ghost"
            onClick={() => resolveTxBandWarning(false)}
          >
            Cancel
          </button>
          <button
            type="button"
            className="btn active"
            onClick={() => resolveTxBandWarning(true, dontShowAgain)}
          >
            Continue TX
          </button>
        </div>
      </div>
    </div>
  );
}
