// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

import { useEffect, useState } from 'react';
import type { RepoUpdateStatus } from '../api/client';

type Props = {
  status: RepoUpdateStatus | null;
  onDismiss: () => void;
  onOpenSettings: () => void;
};

function updateUrl(status: RepoUpdateStatus): string | null {
  return status.releaseDownloadUrl ?? status.releaseUrl;
}

export function StartupUpdatePrompt({ status, onDismiss, onOpenSettings }: Props) {
  const [visible, setVisible] = useState(false);

  useEffect(() => {
    if (!status) return;
    const timer = setTimeout(() => setVisible(true), 100);
    return () => clearTimeout(timer);
  }, [status]);

  if (!status) return null;

  const url = updateUrl(status);
  const latest = status.latestVersion ?? status.releaseTag ?? 'latest';

  const openUpdate = () => {
    if (url) window.open(url, '_blank', 'noopener,noreferrer');
    onDismiss();
  };

  return (
    <div
      role="status"
      aria-live="polite"
      style={{
        position: 'fixed',
        top: 16,
        right: 16,
        zIndex: 10000,
        maxWidth: 420,
        width: 'calc(100% - 32px)',
        transform: `translateY(${visible ? '0' : '-120%'})`,
        transition: 'transform 0.25s ease-out',
      }}
    >
      <div
        style={{
          display: 'flex',
          flexDirection: 'column',
          gap: 10,
          padding: 14,
          borderRadius: 'var(--r-md)',
          border: '1px solid var(--panel-border)',
          background: 'var(--panel-top)',
          color: 'var(--fg-1)',
          boxShadow: '0 10px 28px rgba(0, 0, 0, 0.35)',
        }}
      >
        <div>
          <div
            style={{
              fontSize: 11,
              fontWeight: 700,
              letterSpacing: '0.12em',
              textTransform: 'uppercase',
              color: 'var(--power)',
              marginBottom: 4,
            }}
          >
            UPDATE AVAILABLE
          </div>
          <div style={{ fontSize: 13, fontWeight: 700 }}>
            Zeus {latest} is ready
          </div>
          <div style={{ fontSize: 11, color: 'var(--fg-3)', lineHeight: 1.4, marginTop: 3 }}>
            Installed {status.installedVersion ?? 'unknown'}
            {status.releaseAssetName ? ` - ${status.releaseAssetName}` : ''}
          </div>
        </div>

        <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 8, flexWrap: 'wrap' }}>
          <button type="button" className="btn sm" onClick={onDismiss}>
            LATER
          </button>
          <button
            type="button"
            className="btn sm"
            onClick={() => {
              onOpenSettings();
              onDismiss();
            }}
          >
            DETAILS
          </button>
          <button type="button" className="btn sm active" onClick={openUpdate} disabled={!url}>
            UPDATE NOW
          </button>
        </div>
      </div>
    </div>
  );
}
