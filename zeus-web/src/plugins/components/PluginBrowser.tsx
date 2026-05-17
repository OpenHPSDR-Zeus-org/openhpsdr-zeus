// SPDX-License-Identifier: GPL-2.0-or-later
//
// PluginBrowser — registry catalog browser. Lists entries from
// /api/plugins/registry; each card shows the latest version, license,
// categories and verified-by-Zeus badge. The Install button posts
// { source: "registry", id, version } to the install endpoint.

import { useEffect, useMemo } from 'react';

import { usePluginsStore } from '../state/plugins-store';
import type {
  RegistryPluginEntry,
  RegistryPluginVersion,
} from '../api/plugins';

function latestVersion(
  entry: RegistryPluginEntry,
): RegistryPluginVersion | null {
  if (entry.versions.length === 0) return null;
  // The registry is curator-controlled — schema guarantees `versions`
  // are sorted newest-first when published, but we don't rely on that.
  // Pick the highest SemVer triple to be safe.
  const sorted = [...entry.versions].sort((a, b) =>
    compareSemver(b.version, a.version),
  );
  return sorted[0] ?? null;
}

function compareSemver(a: string, b: string): number {
  const ap = a.split('.').map((x) => parseInt(x, 10) || 0);
  const bp = b.split('.').map((x) => parseInt(x, 10) || 0);
  const n = Math.max(ap.length, bp.length);
  for (let i = 0; i < n; i++) {
    const av = ap[i] ?? 0;
    const bv = bp[i] ?? 0;
    if (av !== bv) return av - bv;
  }
  return 0;
}

function VerifiedBadge() {
  return (
    <span
      title="Verified by the Zeus maintainers"
      style={{
        fontSize: 10,
        padding: '2px 6px',
        borderRadius: 'var(--r-sm)',
        background: 'var(--ok-soft)',
        border: '1px solid var(--ok)',
        color: 'var(--ok)',
        fontWeight: 700,
        letterSpacing: '0.08em',
        textTransform: 'uppercase',
      }}
    >
      VERIFIED
    </span>
  );
}

function CategoryChip({ label }: { label: string }) {
  return (
    <span
      style={{
        fontSize: 10,
        padding: '2px 6px',
        borderRadius: 'var(--r-sm)',
        background: 'var(--bg-2)',
        border: '1px solid var(--line)',
        color: 'var(--fg-2)',
        fontFamily: 'var(--font-mono)',
      }}
    >
      {label}
    </span>
  );
}

function RegistryCard({
  entry,
  installedIds,
}: {
  entry: RegistryPluginEntry;
  installedIds: Set<string>;
}) {
  const install = usePluginsStore((s) => s.install);
  const installing = usePluginsStore((s) => s.installInflight);
  const latest = useMemo(() => latestVersion(entry), [entry]);
  const alreadyInstalled = installedIds.has(entry.id);

  const onInstall = () => {
    if (!latest || installing) return;
    void install({
      source: 'registry',
      id: entry.id,
      version: latest.version,
    });
  };

  return (
    <div
      style={{
        background: 'var(--bg-2)',
        border: '1px solid var(--panel-border)',
        borderRadius: 'var(--r-md)',
        padding: 12,
        display: 'flex',
        flexDirection: 'column',
        gap: 8,
      }}
    >
      <div
        style={{
          display: 'flex',
          alignItems: 'baseline',
          justifyContent: 'space-between',
          gap: 12,
        }}
      >
        <div>
          <div
            style={{
              fontWeight: 700,
              color: 'var(--fg-0)',
              fontSize: 13,
              display: 'flex',
              alignItems: 'center',
              gap: 8,
            }}
          >
            {entry.name}
            {entry.verified && <VerifiedBadge />}
          </div>
          <div style={{ color: 'var(--fg-2)', fontSize: 11 }}>
            <span style={{ fontFamily: 'var(--font-mono)' }}>{entry.id}</span>
            {latest ? ` · latest v${latest.version}` : ' · no versions'}
            {entry.author ? ` · ${entry.author}` : ''}
            {entry.license ? ` · ${entry.license}` : ''}
          </div>
        </div>
        <button
          type="button"
          className="btn sm"
          onClick={onInstall}
          disabled={!latest || installing || alreadyInstalled}
          aria-label={
            alreadyInstalled
              ? `${entry.name} already installed`
              : `Install ${entry.name}`
          }
        >
          {alreadyInstalled
            ? 'INSTALLED'
            : installing
              ? 'WORKING…'
              : 'INSTALL'}
        </button>
      </div>

      {entry.description && (
        <div style={{ color: 'var(--fg-1)', lineHeight: 1.5 }}>
          {entry.description}
        </div>
      )}

      {entry.categories.length > 0 && (
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4 }}>
          {entry.categories.map((c) => (
            <CategoryChip key={c} label={c} />
          ))}
        </div>
      )}

      {latest && (
        <div
          style={{
            color: 'var(--fg-3)',
            fontFamily: 'var(--font-mono)',
            fontSize: 10,
          }}
        >
          SDK ABI v{latest.sdkAbi} · min v{latest.sdkMinVersion} · platforms{' '}
          {latest.platforms.join(', ')}
        </div>
      )}
    </div>
  );
}

export function PluginBrowser() {
  const registry = usePluginsStore((s) => s.registry);
  const sourceUrl = usePluginsStore((s) => s.registrySourceUrl);
  const load = usePluginsStore((s) => s.registryLoad);
  const refresh = usePluginsStore((s) => s.refreshRegistry);
  const installed = usePluginsStore((s) => s.installed);
  const installError = usePluginsStore((s) => s.lastInstallError);
  const installOk = usePluginsStore((s) => s.lastInstallOk);
  const clearInstall = usePluginsStore((s) => s.clearInstallFeedback);

  useEffect(() => {
    if (!load.loaded && !load.inflight) {
      void refresh();
    }
  }, [load.loaded, load.inflight, refresh]);

  const installedIds = useMemo(
    () => new Set(installed.map((p) => p.id)),
    [installed],
  );

  return (
    <div
      data-testid="plugins-browser"
      style={{ display: 'flex', flexDirection: 'column', gap: 12 }}
    >
      <div
        style={{
          display: 'flex',
          alignItems: 'baseline',
          justifyContent: 'space-between',
          gap: 12,
        }}
      >
        <div style={{ color: 'var(--fg-2)', fontSize: 11 }}>
          Source:{' '}
          <span style={{ fontFamily: 'var(--font-mono)', color: 'var(--fg-1)' }}>
            {sourceUrl || '—'}
          </span>
        </div>
        <button
          type="button"
          className="btn sm"
          onClick={() => void refresh()}
          disabled={load.inflight}
        >
          {load.inflight ? 'LOADING…' : 'RELOAD'}
        </button>
      </div>

      {installOk && (
        <div
          role="status"
          style={{
            padding: 10,
            borderRadius: 'var(--r-sm)',
            background: 'var(--ok-soft)',
            border: '1px solid var(--ok)',
            color: 'var(--fg-0)',
            display: 'flex',
            justifyContent: 'space-between',
            gap: 8,
          }}
        >
          <span>{installOk}</span>
          <button
            type="button"
            className="btn sm"
            onClick={clearInstall}
            aria-label="Dismiss notice"
          >
            ×
          </button>
        </div>
      )}

      {installError && (
        <div
          role="alert"
          style={{
            padding: 10,
            borderRadius: 'var(--r-sm)',
            background: 'var(--tx-soft)',
            border: '1px solid var(--tx)',
            color: 'var(--fg-0)',
            display: 'flex',
            justifyContent: 'space-between',
            gap: 8,
          }}
        >
          <span>Install failed: {installError}</span>
          <button
            type="button"
            className="btn sm"
            onClick={clearInstall}
            aria-label="Dismiss install error"
          >
            ×
          </button>
        </div>
      )}

      {load.loadError && (
        <div
          role="alert"
          style={{
            padding: 10,
            borderRadius: 'var(--r-sm)',
            background: 'var(--tx-soft)',
            border: '1px solid var(--tx)',
            color: 'var(--fg-0)',
          }}
        >
          Couldn’t reach the registry: {load.loadError}
        </div>
      )}

      {!load.loaded && load.inflight && (
        <div style={{ color: 'var(--fg-2)' }}>Loading registry catalog…</div>
      )}

      {load.loaded && registry && registry.plugins.length === 0 && (
        <div
          style={{
            padding: 16,
            background: 'var(--bg-2)',
            border: '1px dashed var(--line-strong)',
            borderRadius: 'var(--r-md)',
            color: 'var(--fg-2)',
            textAlign: 'center',
          }}
        >
          Registry is empty — no plugins to show.
        </div>
      )}

      {registry?.plugins.map((entry) => (
        <RegistryCard
          key={entry.id}
          entry={entry}
          installedIds={installedIds}
        />
      ))}
    </div>
  );
}
