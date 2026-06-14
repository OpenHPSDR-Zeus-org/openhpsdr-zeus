// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// "Download Audio Suite" — one-click install of the six audio chain
// plugins (Noise Gate → EQ → Compressor → Exciter → Bass → Reverb) from
// the Kb2uka/openhpsdr-zeus-plugins GitHub releases. The plugin host can't
// register new endpoints / load new assemblies into the live process, so
// after install finishes we show a modal telling the operator to restart
// Zeus. No auto-restart — operator decides.

import { useCallback, useMemo, useState } from 'react';
import { installPlugin } from '../plugins/api/plugins';
import { fetchInstalledPlugins } from '../plugins/api/plugins';
import type { PluginDto } from '../plugins/api/plugins';
import { RestartRequiredModal } from './RestartRequiredModal';

interface SuitePlugin {
  /** Manifest id — used to detect "already installed". */
  id: string;
  /** Human-readable display name shown in progress + result rows. */
  label: string;
  /** Release zip URL on Kb2uka/openhpsdr-zeus-plugins. */
  url: string;
}

const AUDIO_SUITE: ReadonlyArray<SuitePlugin> = [
  {
    id: 'com.openhpsdr.zeus.samples.noisegate',
    label: 'Noise Gate',
    url: 'https://github.com/Kb2uka/openhpsdr-zeus-plugins/releases/download/noisegate-v0.1.0/noisegate-0.1.0.zip',
  },
  {
    id: 'com.openhpsdr.zeus.samples.eq',
    label: 'EQ (10-band parametric)',
    url: 'https://github.com/Kb2uka/openhpsdr-zeus-plugins/releases/download/eq-v0.2.0/eq-0.2.0.zip',
  },
  {
    id: 'com.openhpsdr.zeus.samples.compressor',
    label: 'Compressor',
    url: 'https://github.com/Kb2uka/openhpsdr-zeus-plugins/releases/download/compressor-v0.1.2/compressor-0.1.2.zip',
  },
  {
    id: 'com.openhpsdr.zeus.samples.exciter',
    label: 'Aural-Exciter',
    url: 'https://github.com/Kb2uka/openhpsdr-zeus-plugins/releases/download/exciter-v0.2.0/exciter-0.2.0.zip',
  },
  {
    id: 'com.openhpsdr.zeus.samples.bass',
    label: 'Bass Enhancer',
    url: 'https://github.com/Kb2uka/openhpsdr-zeus-plugins/releases/download/bass-v0.2.0/bass-0.2.0.zip',
  },
  {
    id: 'com.openhpsdr.zeus.samples.reverb',
    label: 'Reverb',
    url: 'https://github.com/Kb2uka/openhpsdr-zeus-plugins/releases/download/reverb-v0.2.0/reverb-0.2.0.zip',
  },
];

type RowState = 'pending' | 'installing' | 'ok' | 'error' | 'skipped';

interface ProgressRow {
  id: string;
  label: string;
  state: RowState;
  message?: string;
}

function rowDot(state: RowState): { color: string; glow: string; symbol: string } {
  switch (state) {
    case 'installing': return { color: 'var(--accent)',  glow: 'rgba(74, 158, 255, 0.55)', symbol: '…' };
    case 'ok':         return { color: 'var(--accent)',  glow: 'rgba(74, 158, 255, 0.55)', symbol: '✓' };
    case 'skipped':    return { color: 'var(--fg-2)',    glow: 'transparent',              symbol: '↺' };
    case 'error':      return { color: 'var(--tx)',      glow: 'rgba(230, 58, 43, 0.55)',  symbol: '!' };
    case 'pending':
    default:           return { color: 'var(--fg-3)',    glow: 'transparent',              symbol: '·' };
  }
}

export function DownloadAudioSuiteButton() {
  const [busy, setBusy] = useState(false);
  const [rows, setRows] = useState<ProgressRow[]>([]);
  const [restartModalOpen, setRestartModalOpen] = useState(false);
  const [showProgress, setShowProgress] = useState(false);

  const totals = useMemo(() => {
    let ok = 0; let skipped = 0; let errors = 0;
    for (const r of rows) {
      if (r.state === 'ok') ok++;
      else if (r.state === 'skipped') skipped++;
      else if (r.state === 'error') errors++;
    }
    return { ok, skipped, errors };
  }, [rows]);

  const runInstall = useCallback(async () => {
    setBusy(true);
    setShowProgress(true);

    // Seed the progress list with one row per suite member.
    let working: ProgressRow[] = AUDIO_SUITE.map((p) => ({
      id: p.id, label: p.label, state: 'pending' as RowState,
    }));
    setRows(working);

    // Probe what's already installed so we can skip those (Reinstall via
    // the regular Plugins admin if you need to overwrite).
    let installedIds = new Set<string>();
    try {
      const listing = await fetchInstalledPlugins();
      installedIds = new Set(listing.plugins.map((p: PluginDto) => p.id));
    } catch {
      // Listing failed — proceed anyway; install will still validate.
    }

    for (let i = 0; i < AUDIO_SUITE.length; i++) {
      const plugin = AUDIO_SUITE[i];
      if (!plugin) continue;

      if (installedIds.has(plugin.id)) {
        working = working.map((r, idx) => idx === i
          ? { ...r, state: 'skipped', message: 'already installed' }
          : r);
        setRows(working);
        continue;
      }

      working = working.map((r, idx) => idx === i
        ? { ...r, state: 'installing' }
        : r);
      setRows(working);

      try {
        await installPlugin({ source: 'url', url: plugin.url });
        working = working.map((r, idx) => idx === i
          ? { ...r, state: 'ok' }
          : r);
        setRows(working);
      } catch (err) {
        const message = err instanceof Error ? err.message : 'install failed';
        working = working.map((r, idx) => idx === i
          ? { ...r, state: 'error', message }
          : r);
        setRows(working);
      }
    }

    setBusy(false);
    // Open the restart modal as long as at least one install actually
    // landed — skipped-only runs don't need a restart.
    const anyInstalled = working.some((r) => r.state === 'ok');
    if (anyInstalled) setRestartModalOpen(true);
  }, []);

  return (
    <>
      <button
        type="button"
        className="btn sm active"
        disabled={busy}
        onClick={runInstall}
        title="Install the six audio chain plugins (Noise Gate, EQ, Compressor, Exciter, Bass, Reverb) from the official Zeus plugin repo"
        style={{ whiteSpace: 'nowrap' }}
      >
        {busy ? 'Installing…' : 'Download Audio Suite'}
      </button>

      {showProgress && (
        <div
          role="status"
          aria-live="polite"
          style={{
            marginTop: 8,
            padding: '10px 12px',
            background: 'var(--bg-1)',
            border: '1px solid var(--line)',
            borderRadius: 6,
            fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)',
            fontSize: 11,
            color: 'var(--fg-1)',
            display: 'flex',
            flexDirection: 'column',
            gap: 6,
            width: '100%',
          }}
        >
          <div style={{
            display: 'flex',
            justifyContent: 'space-between',
            alignItems: 'baseline',
            color: 'var(--fg-2)',
            letterSpacing: 0.8,
            textTransform: 'uppercase',
          }}>
            <span>Audio Suite install</span>
            <span style={{ fontFamily: 'var(--font-mono, JetBrains Mono, ui-monospace, monospace)' }}>
              {totals.ok} installed · {totals.skipped} already present · {totals.errors} failed
            </span>
          </div>

          {rows.map((r) => {
            const dot = rowDot(r.state);
            return (
              <div key={r.id} style={{
                display: 'flex',
                alignItems: 'center',
                gap: 8,
                fontFamily: 'var(--font-mono, JetBrains Mono, ui-monospace, monospace)',
              }}>
                <span
                  aria-hidden
                  style={{
                    width: 16, height: 16,
                    borderRadius: 3,
                    display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
                    background: 'var(--bg-2)',
                    color: dot.color,
                    border: '1px solid var(--line)',
                    boxShadow: dot.glow !== 'transparent' ? `0 0 6px ${dot.glow}` : 'none',
                    fontSize: 11, lineHeight: 1, fontWeight: 600,
                  }}
                >{dot.symbol}</span>
                <span style={{ flex: 1, color: 'var(--fg-0)' }}>{r.label}</span>
                {r.message && (
                  <span style={{
                    color: r.state === 'error' ? 'var(--tx)' : 'var(--fg-3)',
                    fontSize: 10,
                  }}>{r.message}</span>
                )}
              </div>
            );
          })}
        </div>
      )}

      {restartModalOpen && (
        <RestartRequiredModal
          installed={totals.ok}
          skipped={totals.skipped}
          errors={totals.errors}
          onClose={() => setRestartModalOpen(false)}
        />
      )}
    </>
  );
}

// (RestartRequiredModal extracted to ./RestartRequiredModal.tsx so the
// Plugins panel's single-plugin install flow can show the same dialog.)
