// SPDX-License-Identifier: GPL-2.0-or-later
//
// InstalledPlugins — card list of currently loaded plugins, with an
// uninstall control on each card. The "deferred uninstall" 202 case is
// surfaced inline so the operator knows a restart is still required.

import { useEffect } from 'react';

import { usePluginsStore } from '../state/plugins-store';
import type { PluginDto } from '../api/plugins';

function CapabilityChips({ caps }: { caps: string[] }) {
  if (caps.length === 0) {
    return (
      <span style={{ color: 'var(--fg-3)', fontStyle: 'italic' }}>
        no host capabilities granted
      </span>
    );
  }
  return (
    <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4 }}>
      {caps.map((c) => (
        <span
          key={c}
          style={{
            fontFamily: 'var(--font-mono)',
            fontSize: 10,
            padding: '2px 6px',
            borderRadius: 'var(--r-sm)',
            background: 'var(--bg-2)',
            border: '1px solid var(--line)',
            color: 'var(--fg-1)',
          }}
        >
          {c}
        </span>
      ))}
    </div>
  );
}

function PluginCard({ p }: { p: PluginDto }) {
  const uninstall = usePluginsStore((s) => s.uninstall);
  const inflight = usePluginsStore((s) => s.uninstallInflight);

  const onUninstall = () => {
    if (inflight) return;
    const ok = window.confirm(
      `Uninstall plugin "${p.name}" (${p.id})?\n` +
        'The plugin will be removed from the host. A restart may be ' +
        'required to fully unload the assembly.',
    );
    if (!ok) return;
    void uninstall(p.id);
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
          <div style={{ fontWeight: 700, color: 'var(--fg-0)', fontSize: 13 }}>
            {p.name}
          </div>
          <div style={{ color: 'var(--fg-2)', fontSize: 11 }}>
            <span style={{ fontFamily: 'var(--font-mono)' }}>{p.id}</span>
            {' · '}v{p.version}
            {p.author ? ` · ${p.author}` : ''}
            {p.license ? ` · ${p.license}` : ''}
          </div>
        </div>
        <button
          type="button"
          className="btn sm"
          onClick={onUninstall}
          disabled={inflight}
          aria-label={`Uninstall ${p.name}`}
        >
          {inflight ? 'WORKING…' : 'UNINSTALL'}
        </button>
      </div>

      {p.description && (
        <div style={{ color: 'var(--fg-1)', lineHeight: 1.5 }}>
          {p.description}
        </div>
      )}

      <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap' }}>
        {p.ui && (
          <span
            style={{
              fontSize: 10,
              padding: '2px 6px',
              borderRadius: 'var(--r-sm)',
              background: 'var(--accent-soft)',
              border: '1px solid var(--accent-line)',
              color: 'var(--accent-bright)',
              fontWeight: 600,
              letterSpacing: '0.08em',
              textTransform: 'uppercase',
            }}
          >
            UI · {p.ui.panels.length} panel
            {p.ui.panels.length === 1 ? '' : 's'}
          </span>
        )}
        {p.audio && (
          <span
            style={{
              fontSize: 10,
              padding: '2px 6px',
              borderRadius: 'var(--r-sm)',
              background: 'var(--amber-soft)',
              border: '1px solid var(--amber)',
              color: 'var(--amber)',
              fontWeight: 600,
              letterSpacing: '0.08em',
              textTransform: 'uppercase',
            }}
          >
            AUDIO · {p.audio.slot}
          </span>
        )}
        {p.homepage && (
          <a
            href={p.homepage}
            target="_blank"
            rel="noopener noreferrer"
            style={{
              fontSize: 10,
              padding: '2px 6px',
              borderRadius: 'var(--r-sm)',
              border: '1px solid var(--line)',
              color: 'var(--accent-bright)',
              textDecoration: 'none',
              fontWeight: 600,
              letterSpacing: '0.08em',
              textTransform: 'uppercase',
            }}
          >
            HOMEPAGE
          </a>
        )}
      </div>

      <CapabilityChips caps={p.capabilities} />
    </div>
  );
}

export function InstalledPlugins() {
  const installed = usePluginsStore((s) => s.installed);
  const load = usePluginsStore((s) => s.installedLoad);
  const sdkAbi = usePluginsStore((s) => s.sdkAbi);
  const sdkVersion = usePluginsStore((s) => s.sdkVersion);
  const refresh = usePluginsStore((s) => s.refreshInstalled);
  const uninstallError = usePluginsStore((s) => s.lastUninstallError);
  const uninstallNotice = usePluginsStore((s) => s.lastUninstallNotice);
  const clearUninstall = usePluginsStore((s) => s.clearUninstallFeedback);

  useEffect(() => {
    if (!load.loaded && !load.inflight) {
      void refresh();
    }
  }, [load.loaded, load.inflight, refresh]);

  return (
    <div
      data-testid="plugins-installed"
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
          SDK ABI v{sdkAbi || '?'} · runtime {sdkVersion || '—'}
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

      {uninstallError && (
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
          <span>Uninstall failed: {uninstallError}</span>
          <button
            type="button"
            className="btn sm"
            onClick={clearUninstall}
            aria-label="Dismiss uninstall error"
          >
            ×
          </button>
        </div>
      )}

      {uninstallNotice && (
        <div
          role="status"
          style={{
            padding: 10,
            borderRadius: 'var(--r-sm)',
            background: 'var(--accent-soft)',
            border: '1px solid var(--accent-line)',
            color: 'var(--fg-0)',
            display: 'flex',
            justifyContent: 'space-between',
            gap: 8,
          }}
        >
          <span>{uninstallNotice}</span>
          <button
            type="button"
            className="btn sm"
            onClick={clearUninstall}
            aria-label="Dismiss notice"
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
          Couldn’t load plugins: {load.loadError}
        </div>
      )}

      {!load.loaded && load.inflight && (
        <div style={{ color: 'var(--fg-2)' }}>Loading installed plugins…</div>
      )}

      {load.loaded && installed.length === 0 && (
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
          No plugins installed yet. Browse the registry or install from a URL.
        </div>
      )}

      {installed.map((p) => (
        <PluginCard key={p.id} p={p} />
      ))}
    </div>
  );
}
