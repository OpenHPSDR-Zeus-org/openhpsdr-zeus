// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// UpdatesPanel — Settings -> Updates. Reports the latest PRODUCTION build from
// the Zeus download domain (downloads.openhpsdrzeus.com, published from `main`
// only) and opens the matching installer / DMG / AppImage / tarball for this
// platform. Source checkouts see the same domain status; they update their
// source manually with scripts/update.*.

import { useCallback, useEffect, useState, type CSSProperties, type ReactNode } from 'react';
import { fetchUpdateStatus, type RepoUpdateStatus } from '../api/client';

const labelStyle: CSSProperties = { fontSize: 11, fontWeight: 600, letterSpacing: '0.06em', color: 'var(--fg-2)' };
const valueStyle: CSSProperties = { fontSize: 12, color: 'var(--fg-1)', fontFamily: 'monospace' };
const hintStyle: CSSProperties = { fontSize: 10, lineHeight: 1.4, color: 'var(--fg-3)' };
const linkStyle: CSSProperties = { color: 'var(--accent)', textDecoration: 'none' };

function Row({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div style={{ display: 'flex', gap: 10, alignItems: 'baseline' }}>
      <span style={{ ...labelStyle, minWidth: 92 }}>{label}</span>
      <span style={valueStyle}>{children}</span>
    </div>
  );
}

function fmtChecked(iso: string | null): string {
  if (!iso) return '';
  const t = Date.parse(iso);
  if (Number.isNaN(t)) return '';
  const secs = Math.max(0, Math.round((Date.now() - t) / 1000));
  if (secs < 60) return `${secs}s ago`;
  const mins = Math.round(secs / 60);
  return mins < 60 ? `${mins}m ago` : `${Math.round(mins / 60)}h ago`;
}

function fmtBytes(bytes: number | null): string {
  if (bytes == null || !Number.isFinite(bytes) || bytes <= 0) return '';
  const mib = bytes / (1024 * 1024);
  return `${mib.toFixed(mib >= 10 ? 0 : 1)} MB`;
}

function openExternal(url: string | null | undefined) {
  if (!url) return;
  window.open(url, '_blank', 'noopener,noreferrer');
}

function statusLabel(status: RepoUpdateStatus): string {
  if (status.updateAvailable && status.latestVersion) {
    return `Version ${status.latestVersion} available`;
  }
  if (!status.latestVersion && !status.error) {
    return 'Checking releases';
  }
  if (!status.error) {
    return 'Up to date';
  }
  return 'Status unknown';
}

export function UpdatesPanel() {
  const [status, setStatus] = useState<RepoUpdateStatus | null>(null);
  const [checking, setChecking] = useState(false);
  const [result, setResult] = useState<string | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);

  const check = useCallback(async (fetch: boolean) => {
    setChecking(true);
    setLoadError(null);
    try {
      setStatus(await fetchUpdateStatus(fetch));
    } catch (err) {
      setLoadError(err instanceof Error ? err.message : 'Failed to query update status');
    } finally {
      setChecking(false);
    }
  }, []);

  // Quick local-only read on mount (no network), then a fetch in the background
  // so the panel paints instantly but still reflects the real release state.
  useEffect(() => {
    void (async () => {
      await check(false);
      await check(true);
    })();
  }, [check]);

  const doUpdate = () => {
    if (!status) return;
    const url = status.releaseDownloadUrl ?? status.releaseUrl;
    openExternal(url);
    setResult(
      url
        ? status.releaseDownloadUrl
          ? `Opened ${status.releaseAssetName ?? 'the latest Zeus download'}.`
          : 'Opened the Zeus downloads page.'
        : 'No download is available for this platform.',
    );
  };

  const action = status?.updateAction ?? 'none';
  const updateAvailable = status?.updateAvailable ?? false;
  const canUpdate = action === 'download' || action === 'openRelease';
  const assetSize = fmtBytes(status?.releaseAssetSizeBytes ?? null);

  return (
    <div style={{ maxWidth: 640 }}>
      <h3
        style={{
          margin: '0 0 14px',
          fontSize: 11,
          fontWeight: 700,
          letterSpacing: '0.12em',
          textTransform: 'uppercase',
          color: 'var(--fg-2)',
        }}
      >
        SOFTWARE UPDATES
      </h3>

      {loadError && (
        <div style={{ fontSize: 12, color: 'var(--tx)', marginBottom: 12 }}>{loadError}</div>
      )}

      {status && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
          <section style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
            <Row label="Installed">{status.installedVersion ?? 'unknown'}</Row>
            <Row label="Latest">{status.latestVersion ?? (checking ? 'checking...' : '-')}</Row>
            <Row label="Platform">
              {(status.runtimePlatform ?? 'unknown')}/{status.runtimeArchitecture ?? 'unknown'}
            </Row>
            {status.releaseUrl && (
              <Row label="Release">
                <a href={status.releaseUrl} target="_blank" rel="noreferrer" style={linkStyle}>
                  {status.releaseName ?? 'openhpsdrzeus.com'}
                </a>
              </Row>
            )}
            {status.releaseAssetName && (
              <Row label="Download">
                {status.releaseAssetName}
                {assetSize && (
                  <span style={{ color: 'var(--fg-3)', fontFamily: 'inherit' }}> - {assetSize}</span>
                )}
              </Row>
            )}
          </section>

          {status.isGitRepo && (
            <div style={hintStyle}>
              Running from source{status.branch ? ` — ${status.branch}` : ''}
              {status.currentShortSha ? ` @ ${status.currentShortSha}` : ''}. Update the source
              with <code>scripts/update.ps1</code> (Windows) or <code>scripts/update.sh</code>{' '}
              (macOS/Linux), then restart Zeus.
            </div>
          )}

          <section
            style={{
              display: 'flex',
              alignItems: 'center',
              gap: 10,
              padding: '10px 12px',
              borderRadius: 'var(--r-sm)',
              border: '1px solid var(--panel-border)',
              background: 'var(--panel-top)',
            }}
          >
            <span
              style={{
                fontSize: 13,
                fontWeight: 700,
                color: updateAvailable ? 'var(--power)' : 'var(--fg-1)',
              }}
            >
              {statusLabel(status)}
            </span>
            {status.checkedUtc && (
              <span style={{ ...hintStyle, marginLeft: 'auto' }}>checked {fmtChecked(status.checkedUtc)}</span>
            )}
          </section>

          {status.error && (
            <div style={{ fontSize: 11, color: 'var(--tx)' }}>{status.error}</div>
          )}

          <div style={{ display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
            <button
              type="button"
              className="btn sm"
              disabled={checking}
              onClick={() => void check(true)}
            >
              {checking ? 'CHECKING...' : 'CHECK FOR UPDATES'}
            </button>
            <button
              type="button"
              className="btn sm active"
              disabled={!canUpdate || checking}
              onClick={() => doUpdate()}
              title={
                action === 'download'
                  ? 'Open the latest Zeus download for this platform'
                  : action === 'openRelease'
                    ? 'Open the Zeus downloads page'
                    : 'Already up to date'
              }
            >
              UPDATE NOW
            </button>
          </div>

          {result && (
            <div style={{ fontSize: 12, color: 'var(--fg-1)', lineHeight: 1.5 }}>{result}</div>
          )}

          <div style={hintStyle}>
            Update now opens the latest installer or package for this platform. Run the downloaded
            update and restart Zeus to apply it.
          </div>
        </div>
      )}
    </div>
  );
}
