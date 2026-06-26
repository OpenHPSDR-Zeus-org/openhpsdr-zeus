// SPDX-License-Identifier: GPL-2.0-or-later
//
// KiwiSettingsPanel — Settings → KIWI SDR tab. Configures the KiwiSDR slice
// receiver: enable/disable, the KiwiSDR base URL or host:port, an optional
// password, and the live connection status. The Kiwi appears alongside the
// hardware RXs (reserved index WireContract.KiwiReceiverIndex) and renders its
// own panadapter/waterfall/audio like a hardware DDC.
//
// Wires to GET/POST /api/kiwi via getKiwiConfig()/setKiwiConfig(). Mirrors the
// RotatorSettingsPanel card idiom (tokens only — no raw hex).

import { useEffect, useState } from 'react';
import { ApiError, getKiwiConfig, setKiwiConfig, type KiwiConfigDto } from '../api/client';
import { KiwiMapPicker } from './KiwiMapPicker';

export function KiwiSettingsPanel() {
  const [config, setConfig] = useState<KiwiConfigDto | null>(null);
  const [enabled, setEnabled] = useState(false);
  const [url, setUrl] = useState('');
  // The password is write-only from the client's view — the server returns only
  // `hasPassword`, never the secret. Empty input + hasPassword means "keep the
  // stored password"; the operator types a new one only to change/clear it.
  const [password, setPassword] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [showMap, setShowMap] = useState(false);

  // Adopt a server snapshot as the form baseline.
  function adopt(cfg: KiwiConfigDto): void {
    setConfig(cfg);
    setEnabled(cfg.enabled);
    setUrl(cfg.url ?? '');
    setPassword('');
  }

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const cfg = await getKiwiConfig();
        if (!cancelled) adopt(cfg);
      } catch {
        // Read-only probe — leave the form empty on a transient fetch failure.
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  async function onSave(): Promise<void> {
    setSaving(true);
    setError(null);
    try {
      // Only send the password when the operator typed something — an empty
      // input keeps the stored password (the server clears it only on an
      // explicit empty string, which we never send unless cleared deliberately).
      const req: { enabled: boolean; url: string; password?: string } = {
        enabled,
        url: url.trim(),
      };
      if (password !== '') req.password = password;
      const cfg = await setKiwiConfig(req);
      adopt(cfg);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : String(err));
    } finally {
      setSaving(false);
    }
  }

  // Map pick: pass the chosen receiver's address into the URL field AND persist
  // it immediately (the operator "selects from the map" and Zeus stores it until
  // the next selection). Keeps the current enabled state, so picking a new Kiwi
  // while enabled switches the live connection.
  async function onPickFromMap(pickedUrl: string): Promise<void> {
    setUrl(pickedUrl);
    setSaving(true);
    setError(null);
    try {
      const cfg = await setKiwiConfig({ enabled, url: pickedUrl });
      adopt(cfg);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : String(err));
    } finally {
      setSaving(false);
    }
  }

  async function onClearPassword(): Promise<void> {
    setSaving(true);
    setError(null);
    try {
      const cfg = await setKiwiConfig({ password: '' });
      adopt(cfg);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : String(err));
    } finally {
      setSaving(false);
    }
  }

  return (
    <div style={{ maxWidth: 720 }}>
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
        KIWI SDR (REMOTE SLICE RECEIVER)
      </h3>

      <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
        <div
          style={{
            padding: 12,
            background: 'var(--bg-1)',
            border: '1px solid var(--panel-border)',
            borderRadius: 'var(--r-md)',
            display: 'flex',
            flexDirection: 'column',
            gap: 10,
          }}
        >
          <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
            <label
              style={{ display: 'flex', alignItems: 'center', gap: 6, fontSize: 11, color: 'var(--fg-2)' }}
            >
              <input
                type="checkbox"
                checked={enabled}
                onChange={(e) => setEnabled(e.target.checked)}
                style={{ accentColor: 'var(--accent)' }}
              />
              ENABLED
            </label>
            <span style={{ flex: 1 }} />
            <StatusPill status={config?.status ?? 'disabled'} />
          </div>

          <label style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
            <span style={{ fontSize: 11, fontWeight: 600, color: 'var(--fg-2)' }}>KiwiSDR URL</span>
            <input
              type="text"
              value={url}
              onChange={(e) => setUrl(e.target.value)}
              placeholder="sdr.example.org:8073"
              spellCheck={false}
              autoComplete="off"
              style={{
                padding: '6px 8px',
                fontSize: 12,
                fontFamily: 'monospace',
                background: 'var(--bg-0)',
                border: '1px solid var(--panel-border)',
                borderRadius: 'var(--r-sm)',
                color: 'var(--fg-0)',
              }}
            />
          </label>

          <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
            <button
              type="button"
              onClick={() => setShowMap((v) => !v)}
              className={`btn sm${showMap ? ' active' : ''}`}
              style={{ alignSelf: 'flex-start' }}
              aria-expanded={showMap}
            >
              {showMap ? '▾ HIDE MAP' : '🌐 SELECT FROM MAP'}
            </button>
            {showMap && <KiwiMapPicker selectedUrl={url.trim()} onSelect={(u) => void onPickFromMap(u)} />}
          </div>

          <label style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
            <span style={{ fontSize: 11, fontWeight: 600, color: 'var(--fg-2)' }}>
              Password{' '}
              <span style={{ fontWeight: 400, color: 'var(--fg-3)' }}>
                {config?.hasPassword
                  ? '(stored — leave blank to keep)'
                  : '(optional — only for password-protected KiwiSDRs)'}
              </span>
            </span>
            <input
              type="password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              placeholder={config?.hasPassword ? '••••••••' : ''}
              spellCheck={false}
              autoComplete="off"
              style={{
                padding: '6px 8px',
                fontSize: 12,
                fontFamily: 'monospace',
                background: 'var(--bg-0)',
                border: '1px solid var(--panel-border)',
                borderRadius: 'var(--r-sm)',
                color: 'var(--fg-0)',
              }}
            />
          </label>

          {config?.statusDetail && (
            <div
              style={{
                padding: 8,
                fontSize: 12,
                color: config.status === 'error' ? 'var(--tx)' : 'var(--fg-1)',
                background: config.status === 'error' ? 'rgba(230, 58, 43, 0.1)' : 'var(--bg-0)',
                border: `1px solid ${config.status === 'error' ? 'var(--tx)' : 'var(--panel-border)'}`,
                borderRadius: 'var(--r-sm)',
              }}
            >
              {config.statusDetail}
            </div>
          )}

          {error && (
            <div
              style={{
                padding: 8,
                fontSize: 12,
                color: 'var(--tx)',
                background: 'rgba(230, 58, 43, 0.1)',
                border: '1px solid var(--tx)',
                borderRadius: 'var(--r-sm)',
              }}
            >
              {error}
            </div>
          )}

          <div style={{ display: 'flex', gap: 6 }}>
            <button
              type="button"
              onClick={() => void onSave()}
              disabled={saving}
              className="btn sm active"
            >
              {saving ? 'SAVING…' : 'SAVE'}
            </button>
            {config?.hasPassword && (
              <button
                type="button"
                onClick={() => void onClearPassword()}
                disabled={saving}
                className="btn sm"
                style={{ borderColor: 'var(--tx)', color: 'var(--tx)' }}
              >
                CLEAR PASSWORD
              </button>
            )}
          </div>
        </div>

        <div style={{ fontSize: 10, lineHeight: 1.4, color: 'var(--fg-3)' }}>
          The KiwiSDR slice receiver streams a remote KiwiSDR&apos;s audio + spectrum into Zeus as
          an extra receiver alongside RX1–RX6. Enter the KiwiSDR&apos;s URL or{' '}
          <span style={{ fontFamily: 'monospace' }}>host:port</span> (e.g.{' '}
          <span style={{ fontFamily: 'monospace' }}>sdr.example.org:8073</span>), enable it, and
          save — the Kiwi pane renders its own panadapter and waterfall. Tune it like any other RX.
          Settings are stored server-side in zeus-prefs.db.
        </div>
      </div>
    </div>
  );
}

function StatusPill({ status }: { status: string }) {
  const map: Record<string, { color: string; bg: string; label: string }> = {
    connected: { color: 'var(--accent)', bg: 'rgba(74,158,255,0.12)', label: 'CONNECTED' },
    connecting: { color: 'var(--power)', bg: 'rgba(255,201,58,0.12)', label: 'CONNECTING' },
    error: { color: 'var(--tx)', bg: 'rgba(230,58,43,0.12)', label: 'ERROR' },
    closed: { color: 'var(--fg-3)', bg: 'var(--bg-2)', label: 'CLOSED' },
    disabled: { color: 'var(--fg-3)', bg: 'var(--bg-2)', label: 'DISABLED' },
  };
  const s = map[status] ?? map.disabled!;
  return (
    <span
      style={{
        padding: '2px 8px',
        borderRadius: 10,
        fontSize: 11,
        fontWeight: 600,
        color: s.color,
        background: s.bg,
        border: `1px solid ${s.color}`,
      }}
    >
      {s.label}
    </span>
  );
}
