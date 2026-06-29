// SPDX-License-Identifier: GPL-2.0-or-later
//
// "Reset & Uninstall Zeus" confirm dialog. Offers an inline one-click backup
// (to the browser's Downloads folder, which the wipe never touches), surfaces
// exactly what will be removed, and gates the irreversible action behind a typed
// confirmation when the operator skips the backup.

import { useEffect, useState } from 'react';
import {
  downloadBackup,
  executeUninstall,
  getUninstallPreview,
  type UninstallPreview,
} from '../api/uninstall';
import { useLoggerStore } from '../state/logger-store';

const TX = 'var(--tx)';

export function UninstallDialog({ onClose }: { onClose: () => void }) {
  const [removeBinary, setRemoveBinary] = useState(true);
  const [preview, setPreview] = useState<UninstallPreview | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [backedUp, setBackedUp] = useState(false);
  const [confirmText, setConfirmText] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState(false);

  const qsoCount = useLoggerStore((s) => s.totalCount);

  useEffect(() => {
    let cancelled = false;
    setPreview(null);
    setLoadError(null);
    getUninstallPreview(removeBinary)
      .then((p) => {
        if (!cancelled) setPreview(p);
      })
      .catch((e) => {
        if (!cancelled) setLoadError(e instanceof Error ? e.message : 'Failed to load uninstall preview');
      });
    return () => {
      cancelled = true;
    };
  }, [removeBinary]);

  const confirmRequired = !backedUp;
  const confirmOk = !confirmRequired || confirmText.trim().toUpperCase() === 'CONFIRM';
  const canUninstall = !!preview?.canProceed && confirmOk && !busy;

  const runUninstall = async () => {
    if (!preview || !canUninstall) return;
    setBusy(true);
    setError(null);
    try {
      await executeUninstall(preview.confirmToken, removeBinary);
      setDone(true);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Uninstall failed');
      setBusy(false);
    }
  };

  return (
    <div
      role="dialog"
      aria-modal="true"
      style={{
        position: 'fixed',
        inset: 0,
        zIndex: 1000,
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        background: 'rgba(0,0,0,0.6)',
      }}
      onClick={busy ? undefined : onClose}
    >
      <div
        onClick={(e) => e.stopPropagation()}
        style={{
          width: 560,
          maxWidth: '92vw',
          maxHeight: '88vh',
          overflowY: 'auto',
          background: 'var(--panel-bot)',
          border: `1px solid ${TX}`,
          borderRadius: 'var(--r-md)',
          padding: 20,
          boxShadow: '0 12px 48px rgba(0,0,0,0.5)',
        }}
      >
        {done ? (
          <div style={{ textAlign: 'center', padding: '20px 0' }}>
            <h3 style={{ color: TX, margin: '0 0 12px' }}>Uninstalling Zeus…</h3>
            <p style={{ color: 'var(--fg-1)', lineHeight: 1.6 }}>
              Zeus is shutting down and removing itself. This window will close on its own.
              You can safely quit your browser.
            </p>
          </div>
        ) : (
          <>
            <h3 style={{ color: TX, margin: '0 0 12px', letterSpacing: '0.08em', textTransform: 'uppercase', fontSize: 14 }}>
              Reset &amp; Uninstall Zeus
            </h3>

            <p style={{ color: 'var(--fg-1)', lineHeight: 1.6, margin: '0 0 12px' }}>
              This permanently deletes <strong>all Zeus data</strong> — every setting and profile,
              your QSO logbook, calibration, plugins, caches, and certificates — for a clean slate.
              <strong> This cannot be undone.</strong>
            </p>

            {/* Inline backup — saves to your Downloads folder, which is NOT wiped. */}
            <div
              style={{
                border: '1px solid var(--panel-border)',
                borderRadius: 'var(--r-sm)',
                padding: 12,
                marginBottom: 12,
                background: 'rgba(74,158,255,0.06)',
              }}
            >
              <div style={{ color: 'var(--fg-1)', marginBottom: 8, lineHeight: 1.5 }}>
                Back up your settings, profiles, and logbook first. The file saves to your{' '}
                <strong>Downloads</strong> folder (which this uninstall never touches).
              </div>
              <button
                type="button"
                className="btn sm"
                onClick={() => {
                  downloadBackup();
                  setBackedUp(true);
                }}
              >
                {backedUp ? '✓ BACKUP DOWNLOADED — DOWNLOAD AGAIN' : '⬇ BACK UP MY DATA'}
              </button>
            </div>

            <label style={{ display: 'flex', alignItems: 'center', gap: 8, color: 'var(--fg-1)', marginBottom: 12, cursor: 'pointer' }}>
              <input type="checkbox" checked={removeBinary} onChange={(e) => setRemoveBinary(e.target.checked)} />
              Also remove the Zeus application itself (full uninstall)
            </label>

            {qsoCount > 0 && !backedUp && (
              <div style={{ color: TX, marginBottom: 12, lineHeight: 1.5, fontWeight: 600 }}>
                ⚠ This will permanently delete {qsoCount.toLocaleString()} logged QSO
                {qsoCount === 1 ? '' : 's'}. Back up first, or they are gone forever.
              </div>
            )}

            {preview?.warnings && preview.warnings.length > 0 && (
              <ul style={{ color: 'var(--fg-2)', fontSize: 12, lineHeight: 1.5, margin: '0 0 12px', paddingLeft: 18 }}>
                {preview.warnings.map((w, i) => (
                  <li key={i}>{w}</li>
                ))}
              </ul>
            )}

            {preview && (
              <details style={{ marginBottom: 12 }}>
                <summary style={{ color: 'var(--fg-2)', fontSize: 12, cursor: 'pointer' }}>
                  Show what will be removed ({preview.paths.length} location{preview.paths.length === 1 ? '' : 's'})
                </summary>
                <ul style={{ color: 'var(--fg-2)', fontSize: 11, lineHeight: 1.5, margin: '8px 0 0', paddingLeft: 18, fontFamily: 'monospace' }}>
                  {preview.paths.map((p, i) => (
                    <li key={i} style={{ wordBreak: 'break-all' }}>{p}</li>
                  ))}
                </ul>
              </details>
            )}

            {loadError && <div style={{ color: TX, marginBottom: 12 }}>Could not load preview: {loadError}</div>}
            {preview && !preview.canProceed && (
              <div style={{ color: TX, marginBottom: 12 }}>
                Uninstall is blocked by a safety check; nothing will be deleted.
                {preview.abortReasons.map((r, i) => (
                  <div key={i} style={{ fontSize: 12 }}>{r}</div>
                ))}
              </div>
            )}

            {confirmRequired && (
              <div style={{ marginBottom: 12 }}>
                <label style={{ color: 'var(--fg-1)', display: 'block', marginBottom: 6 }}>
                  To proceed without a backup, type <strong>CONFIRM</strong>:
                </label>
                <input
                  className="cs-input mono"
                  value={confirmText}
                  onChange={(e) => setConfirmText(e.target.value)}
                  placeholder="CONFIRM"
                  style={{ width: 160 }}
                  autoFocus
                />
              </div>
            )}

            {error && <div style={{ color: TX, marginBottom: 12 }}>{error}</div>}

            <div style={{ display: 'flex', gap: 10, justifyContent: 'flex-end', marginTop: 8 }}>
              <button type="button" className="btn sm" onClick={onClose} disabled={busy}>
                CANCEL
              </button>
              <button
                type="button"
                className="btn sm"
                onClick={runUninstall}
                disabled={!canUninstall}
                style={{
                  borderColor: TX,
                  color: canUninstall ? '#fff' : 'var(--fg-3)',
                  background: canUninstall ? TX : 'transparent',
                }}
              >
                {busy ? 'UNINSTALLING…' : removeBinary ? 'UNINSTALL ZEUS' : 'WIPE ALL DATA'}
              </button>
            </div>
          </>
        )}
      </div>
    </div>
  );
}
