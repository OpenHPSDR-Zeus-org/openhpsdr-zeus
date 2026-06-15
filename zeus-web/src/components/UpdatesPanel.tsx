// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// UpdatesPanel — Settings → Updates. Shows how far the running git checkout is
// behind its configured upstream and offers a one-click fast-forward pull
// (GET/POST /api/system/update*, backed by RepoUpdateService). A pull updates
// source only: the panel makes the required rebuild + restart explicit and
// points at scripts/update.* rather than trying to rebuild a running binary.

import { useCallback, useEffect, useState } from 'react';
import {
  fetchUpdateStatus,
  pullUpdate,
  type RepoUpdateStatus,
  type RepoUpdateResult,
} from '../api/client';

const labelStyle: React.CSSProperties = { fontSize: 11, fontWeight: 600, letterSpacing: '0.06em', color: 'var(--fg-2)' };
const valueStyle: React.CSSProperties = { fontSize: 12, color: 'var(--fg-1)', fontFamily: 'monospace' };
const hintStyle: React.CSSProperties = { fontSize: 10, lineHeight: 1.4, color: 'var(--fg-3)' };

function Row({ label, children }: { label: string; children: React.ReactNode }) {
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

export function UpdatesPanel() {
  const [status, setStatus] = useState<RepoUpdateStatus | null>(null);
  const [checking, setChecking] = useState(false);
  const [pulling, setPulling] = useState(false);
  const [result, setResult] = useState<RepoUpdateResult | null>(null);
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
  // so the panel paints instantly but still reflects the real upstream state.
  useEffect(() => {
    void (async () => {
      await check(false);
      await check(true);
    })();
  }, [check]);

  const doPull = async () => {
    setPulling(true);
    setResult(null);
    try {
      const r = await pullUpdate();
      setResult(r);
      await check(false);
    } catch (err) {
      setResult({
        ok: false,
        newSha: null,
        requiresRebuild: false,
        message: err instanceof Error ? err.message : 'Update failed',
      });
    } finally {
      setPulling(false);
    }
  };

  const behind = status?.behind ?? 0;
  const upToDate = status?.isGitRepo && behind === 0 && !status.error;

  return (
    <div style={{ maxWidth: 600 }}>
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

      {status && !status.isGitRepo && (
        <div style={{ fontSize: 12, color: 'var(--fg-1)', lineHeight: 1.6 }}>
          Zeus isn’t running from a git checkout, so in-app updates aren’t available. To update,
          re-clone or pull the repository and rebuild — see the “Updating” section of the README.
        </div>
      )}

      {status?.isGitRepo && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
          <section style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
            <Row label="Branch">{status.branch ?? '—'}</Row>
            <Row label="Installed">
              {status.currentShortSha ?? '—'}
              {status.currentSubject ? (
                <span style={{ color: 'var(--fg-3)', fontFamily: 'inherit' }}> · {status.currentSubject}</span>
              ) : null}
            </Row>
            <Row label="Upstream">{status.upstreamRef ?? '—'}</Row>
            {status.latestRemoteSha && (
              <Row label="Available">
                {status.latestRemoteSha}
                {status.latestRemoteSubject ? (
                  <span style={{ color: 'var(--fg-3)', fontFamily: 'inherit' }}> · {status.latestRemoteSubject}</span>
                ) : null}
              </Row>
            )}
          </section>

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
                color: upToDate ? 'var(--fg-1)' : 'var(--power)',
              }}
            >
              {upToDate
                ? 'Up to date'
                : behind > 0
                  ? `${behind} update${behind === 1 ? '' : 's'} available`
                  : 'Status unknown'}
            </span>
            {status.ahead > 0 && (
              <span style={hintStyle}>· {status.ahead} local commit{status.ahead === 1 ? '' : 's'} ahead</span>
            )}
            {status.checkedUtc && (
              <span style={{ ...hintStyle, marginLeft: 'auto' }}>checked {fmtChecked(status.checkedUtc)}</span>
            )}
          </section>

          {status.dirty && (
            <div style={{ fontSize: 11, color: 'var(--tx)' }}>
              Working tree has uncommitted changes — commit or stash them before updating.
            </div>
          )}
          {status.error && (
            <div style={{ fontSize: 11, color: 'var(--tx)' }}>{status.error}</div>
          )}

          <div style={{ display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
            <button
              type="button"
              className="btn sm"
              disabled={checking || pulling}
              onClick={() => void check(true)}
            >
              {checking ? 'CHECKING…' : 'CHECK FOR UPDATES'}
            </button>
            <button
              type="button"
              className="btn sm active"
              disabled={!status.canFastForward || pulling || checking}
              onClick={() => void doPull()}
              title={
                status.canFastForward
                  ? 'Fast-forward the checkout to the latest upstream commit'
                  : status.dirty
                    ? 'Commit or stash local changes first'
                    : behind === 0
                      ? 'Already up to date'
                      : 'Cannot fast-forward (local commits diverge from upstream)'
              }
            >
              {pulling ? 'UPDATING…' : 'UPDATE NOW'}
            </button>
          </div>

          {result && (
            <div
              style={{
                fontSize: 12,
                color: result.ok ? 'var(--fg-1)' : 'var(--tx)',
                lineHeight: 1.5,
              }}
            >
              {result.message}
              {result.requiresRebuild && (
                <div style={{ ...hintStyle, marginTop: 6 }}>
                  Source updated — the running app is still on the old build. Rebuild and restart
                  Zeus to apply: run <code>scripts/update.ps1</code> (Windows) or{' '}
                  <code>scripts/update.sh</code> (macOS/Linux), then relaunch.
                </div>
              )}
            </div>
          )}

          <div style={hintStyle}>
            “Update now” fast-forwards your local checkout to the latest upstream commit. It changes
            source only — Zeus keeps running the previously-built binaries until you rebuild and
            restart. The update script handles the rebuild for both the backend and the web UI.
          </div>
        </div>
      )}
    </div>
  );
}
