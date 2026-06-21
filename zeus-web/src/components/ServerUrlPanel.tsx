// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
// See LICENSE / ATTRIBUTIONS.md at the repository root.

import { useEffect, useState } from 'react';
import {
  getServerBaseUrl,
  isCapacitorRuntime,
  setServerBaseUrl,
} from '../serverUrl';
import { useCapabilitiesStore } from '../state/capabilities-store';
import { useQrzStore } from '../state/qrz-store';

// Settings tab: lets the operator point a Capacitor / standalone build at a
// specific Zeus.Server on their LAN (e.g. https://192.168.1.23:6443). Browser
// users on the bundled deploy normally leave this blank — relative paths
// already reach the same-origin server.

// Origin that hosts the operator's /go/<callsign> remote vanity address.
const REMOTE_GO_ORIGIN = 'https://openhpsdrzeus.com';

export function ServerUrlPanel() {
  const [value, setValue] = useState(() => getServerBaseUrl());
  const [touched, setTouched] = useState(false);
  const [savedAt, setSavedAt] = useState<number | null>(null);
  const isCapacitor = isCapacitorRuntime();
  const capabilities = useCapabilitiesStore((s) => s.capabilities);
  const capabilitiesLoaded = useCapabilitiesStore((s) => s.loaded);
  const capabilitiesInflight = useCapabilitiesStore((s) => s.inflight);
  const refreshCapabilities = useCapabilitiesStore((s) => s.refresh);
  const mobileHttpsUrls = prioritizeCurrentHost(capabilities?.lanHttpsUrls ?? []);

  useEffect(() => {
    if (!savedAt) return;
    const t = setTimeout(() => setSavedAt(null), 2000);
    return () => clearTimeout(t);
  }, [savedAt]);

  useEffect(() => {
    if (capabilitiesLoaded || capabilitiesInflight) return;
    void refreshCapabilities();
  }, [capabilitiesLoaded, capabilitiesInflight, refreshCapabilities]);

  const trimmed = value.trim();
  const error = trimmed === '' ? null : validateUrl(trimmed);
  const dirty = trimmed !== getServerBaseUrl();

  const handleSave = () => {
    if (error) return;
    setServerBaseUrl(trimmed);
    setSavedAt(Date.now());
    setTouched(false);
    // Reload so all in-flight subscribers (WS, polling timers, store hydration)
    // pick up the new base URL cleanly. This matches the connect-panel
    // expectations and avoids half-routed traffic.
    if (trimmed !== '' || isCapacitor) {
      setTimeout(() => window.location.reload(), 250);
    }
  };

  const handleClear = () => {
    setServerBaseUrl('');
    setValue('');
    setSavedAt(Date.now());
    setTouched(false);
    setTimeout(() => window.location.reload(), 250);
  };

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
        SERVER URL
      </h3>

      <p style={{ fontSize: 12, color: 'var(--fg-2)', lineHeight: 1.5, marginTop: 0 }}>
        Address of the Zeus.Server you want to control. Leave blank when the
        web UI is being served by Zeus.Server itself (the typical browser
        deploy). On native mobile / desktop wrappers, point this at the LAN
        host running Zeus.Server, e.g.{' '}
        <code style={{ fontFamily: 'monospace', color: 'var(--fg-1)' }}>
          https://192.168.1.23:6443
        </code>
        .
      </p>

      <div
        style={{
          marginTop: 16,
          padding: 10,
          fontSize: 11,
          lineHeight: 1.5,
          color: 'var(--fg-2)',
          background: 'var(--bg-2)',
          border: '1px solid var(--panel-border)',
          borderRadius: 'var(--r-sm)',
        }}
      >
        <div
          style={{
            marginBottom: 6,
            fontSize: 11,
            fontWeight: 700,
            letterSpacing: '0.1em',
            textTransform: 'uppercase',
            color: 'var(--fg-1)',
          }}
        >
          Mobile browser HTTPS
        </div>
        {mobileHttpsUrls.length > 0 ? (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
            {mobileHttpsUrls.map((url) => (
              <a
                key={url}
                href={url}
                style={{
                  fontFamily: 'monospace',
                  fontSize: 12,
                  color: 'var(--accent)',
                  wordBreak: 'break-all',
                }}
              >
                {url}
              </a>
            ))}
          </div>
        ) : (
          <span>
            No HTTPS LAN address was reported by this Zeus.Server. Start Zeus
            with LAN HTTPS enabled to use phone microphone access from a
            browser.
          </span>
        )}
      </div>

      <label style={{ display: 'flex', flexDirection: 'column', gap: 6, marginTop: 16 }}>
        <span
          style={{
            fontSize: 11,
            fontWeight: 600,
            letterSpacing: '0.1em',
            textTransform: 'uppercase',
            color: 'var(--fg-2)',
          }}
        >
          Base URL
        </span>
        <input
          type="url"
          autoCapitalize="off"
          autoCorrect="off"
          spellCheck={false}
          inputMode="url"
          placeholder="https://192.168.1.23:6443"
          value={value}
          onChange={(e) => {
            setValue(e.target.value);
            setTouched(true);
          }}
          style={{
            padding: '8px 10px',
            fontFamily: 'monospace',
            fontSize: 13,
            color: 'var(--fg-0)',
            background: 'var(--bg-2)',
            border: '1px solid var(--panel-border)',
            borderRadius: 'var(--r-sm)',
            outline: 'none',
          }}
        />
        {touched && error && (
          <span style={{ fontSize: 11, color: 'var(--tx)' }}>{error}</span>
        )}
      </label>

      <div style={{ display: 'flex', gap: 8, marginTop: 18, alignItems: 'center' }}>
        <button
          type="button"
          onClick={handleSave}
          disabled={!dirty || !!error}
          style={{
            padding: '8px 16px',
            fontSize: 11,
            fontWeight: 700,
            letterSpacing: '0.1em',
            textTransform: 'uppercase',
            color: dirty && !error ? 'var(--fg-0)' : 'var(--fg-2)',
            background: dirty && !error ? 'var(--accent)' : 'var(--bg-2)',
            border: '1px solid var(--panel-border)',
            borderRadius: 'var(--r-sm)',
            cursor: dirty && !error ? 'pointer' : 'not-allowed',
            opacity: dirty && !error ? 1 : 0.6,
          }}
        >
          Save & reload
        </button>
        <button
          type="button"
          onClick={handleClear}
          disabled={getServerBaseUrl() === ''}
          style={{
            padding: '8px 16px',
            fontSize: 11,
            fontWeight: 700,
            letterSpacing: '0.1em',
            textTransform: 'uppercase',
            color: 'var(--fg-2)',
            background: 'var(--bg-2)',
            border: '1px solid var(--panel-border)',
            borderRadius: 'var(--r-sm)',
            cursor: getServerBaseUrl() === '' ? 'not-allowed' : 'pointer',
            opacity: getServerBaseUrl() === '' ? 0.5 : 1,
          }}
        >
          Clear
        </button>
        {savedAt && (
          <span style={{ fontSize: 11, color: 'var(--accent)' }}>Saved.</span>
        )}
      </div>

      {isCapacitor && (
        <div
          style={{
            marginTop: 22,
            padding: 10,
            fontSize: 11,
            lineHeight: 1.5,
            color: 'var(--fg-2)',
            background: 'var(--bg-2)',
            border: '1px solid var(--panel-border)',
            borderRadius: 'var(--r-sm)',
          }}
        >
          <strong style={{ color: 'var(--fg-1)' }}>Native shell detected.</strong>{' '}
          Cleartext HTTP to RFC1918 / link-local addresses is permitted; iOS
          may show a "Find devices on local network" prompt the first time
          the app reaches a 192.168.* / 10.* host.
        </div>
      )}

      <RemotePasswordSection />

      <RemoteQrSection />
    </div>
  );
}

// Remote-access session password (ADR-0008). Mandatory: remote access does
// nothing until the right password is entered. Set/change/clear hit the
// SPAKE2+ verifier endpoints — the password is never stored, only its verifier.
function RemotePasswordSection() {
  const [hasPassword, setHasPassword] = useState<boolean | null>(null);
  const [pw, setPw] = useState('');
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);

  const refresh = async () => {
    try {
      const r = await fetch('/api/remote/password/status');
      const j = await r.json();
      setHasPassword(!!j.hasPassword);
    } catch {
      setHasPassword(null);
    }
  };

  useEffect(() => {
    void refresh();
  }, []);

  const save = async () => {
    if (pw.length < 8) {
      setNotice('Password must be at least 8 characters.');
      return;
    }
    setBusy(true);
    setNotice(null);
    try {
      const r = await fetch('/api/remote/password', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ password: pw }),
      });
      if (!r.ok) throw new Error((await r.json().catch(() => ({})))?.error ?? 'Failed to save');
      setPw('');
      setNotice('Password saved.');
      await refresh();
    } catch (e) {
      setNotice((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  const clear = async () => {
    setBusy(true);
    setNotice(null);
    try {
      await fetch('/api/remote/password', { method: 'DELETE' });
      setNotice('Password cleared — remote access is disabled.');
      await refresh();
    } finally {
      setBusy(false);
    }
  };

  return (
    <div style={{ marginTop: 28 }}>
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
        REMOTE ACCESS PASSWORD
      </h3>

      <p style={{ fontSize: 12, color: 'var(--fg-2)', lineHeight: 1.5, marginTop: 0 }}>
        Required for remote access. Nothing — no audio, no control, not even a
        connection — happens until this password is entered correctly. It is
        verified end-to-end at your radio (SPAKE2+); the server stores only a
        verifier, never the password itself.
      </p>

      <div
        style={{
          marginTop: 12,
          fontSize: 12,
          fontWeight: 700,
          color: hasPassword ? 'var(--accent)' : 'var(--tx)',
        }}
      >
        {hasPassword === null
          ? '…'
          : hasPassword
            ? '● Password set — remote access can be enabled.'
            : '○ No password — remote access is disabled.'}
      </div>

      <label style={{ display: 'flex', flexDirection: 'column', gap: 6, marginTop: 14 }}>
        <span
          style={{
            fontSize: 11,
            fontWeight: 600,
            letterSpacing: '0.1em',
            textTransform: 'uppercase',
            color: 'var(--fg-2)',
          }}
        >
          {hasPassword ? 'Change password' : 'Set password'}
        </span>
        <input
          type="password"
          autoComplete="new-password"
          placeholder="at least 8 characters"
          value={pw}
          onChange={(e) => setPw(e.target.value)}
          style={{
            padding: '8px 10px',
            fontFamily: 'monospace',
            fontSize: 13,
            color: 'var(--fg-0)',
            background: 'var(--bg-2)',
            border: '1px solid var(--panel-border)',
            borderRadius: 'var(--r-sm)',
            outline: 'none',
          }}
        />
      </label>

      <div style={{ display: 'flex', gap: 8, marginTop: 14, alignItems: 'center' }}>
        <button
          type="button"
          onClick={save}
          disabled={busy || pw.length < 8}
          style={{
            padding: '8px 16px',
            fontSize: 11,
            fontWeight: 700,
            letterSpacing: '0.1em',
            textTransform: 'uppercase',
            color: !busy && pw.length >= 8 ? 'var(--fg-0)' : 'var(--fg-2)',
            background: !busy && pw.length >= 8 ? 'var(--accent)' : 'var(--bg-2)',
            border: '1px solid var(--panel-border)',
            borderRadius: 'var(--r-sm)',
            cursor: !busy && pw.length >= 8 ? 'pointer' : 'not-allowed',
            opacity: !busy && pw.length >= 8 ? 1 : 0.6,
          }}
        >
          {hasPassword ? 'Change' : 'Set password'}
        </button>
        <button
          type="button"
          onClick={clear}
          disabled={busy || !hasPassword}
          style={{
            padding: '8px 16px',
            fontSize: 11,
            fontWeight: 700,
            letterSpacing: '0.1em',
            textTransform: 'uppercase',
            color: 'var(--fg-2)',
            background: 'var(--bg-2)',
            border: '1px solid var(--panel-border)',
            borderRadius: 'var(--r-sm)',
            cursor: !busy && hasPassword ? 'pointer' : 'not-allowed',
            opacity: !busy && hasPassword ? 1 : 0.5,
          }}
        >
          Clear
        </button>
        {notice && <span style={{ fontSize: 11, color: 'var(--fg-2)' }}>{notice}</span>}
      </div>
    </div>
  );
}

// Remote-access QR: encodes the operator's openhpsdrzeus.com/go/<callsign>
// address so a phone camera opens the remote client directly. The callsign comes
// from the QRZ sign-in (Settings → QRZ); access still requires the session
// password — the link alone is useless (ADR-0007/0008).
function RemoteQrSection() {
  const home = useQrzStore((s) => s.home);
  const callsign = home?.callsign?.trim().toUpperCase() ?? '';
  const remoteUrl = callsign
    ? `${REMOTE_GO_ORIGIN}/go/${encodeURIComponent(callsign)}`
    : null;
  const qrSrc = remoteUrl
    ? `${getServerBaseUrl()}/api/remote/qr.svg?data=${encodeURIComponent(remoteUrl)}`
    : null;

  return (
    <div style={{ marginTop: 28 }}>
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
        REMOTE ACCESS QR
      </h3>

      <p style={{ fontSize: 12, color: 'var(--fg-2)', lineHeight: 1.5, marginTop: 0 }}>
        Scan with your phone to open your radio from anywhere at your personal
        address. The link is safe to share — access still requires the session
        password, so the address alone reaches nothing.
      </p>

      {remoteUrl ? (
        <div style={{ display: 'flex', gap: 16, alignItems: 'center', marginTop: 14 }}>
          <img
            src={qrSrc!}
            alt={`Remote access QR for ${remoteUrl}`}
            width={148}
            height={148}
            style={{ background: '#fff', padding: 8, borderRadius: 'var(--r-sm)', flex: '0 0 auto' }}
          />
          <div style={{ display: 'flex', flexDirection: 'column', gap: 6, minWidth: 0 }}>
            <a
              href={remoteUrl}
              target="_blank"
              rel="noreferrer"
              style={{
                fontFamily: 'monospace',
                fontSize: 13,
                color: 'var(--accent)',
                wordBreak: 'break-all',
              }}
            >
              {remoteUrl}
            </a>
            <span style={{ fontSize: 11, color: 'var(--fg-2)' }}>
              Point your phone camera at the code.
            </span>
          </div>
        </div>
      ) : (
        <div
          style={{
            marginTop: 14,
            padding: 10,
            fontSize: 11,
            lineHeight: 1.5,
            color: 'var(--fg-2)',
            background: 'var(--bg-2)',
            border: '1px solid var(--panel-border)',
            borderRadius: 'var(--r-sm)',
          }}
        >
          Sign in to QRZ (Settings → QRZ) so Zeus knows your callsign. Your remote
          address will be{' '}
          <code style={{ fontFamily: 'monospace', color: 'var(--fg-1)' }}>
            openhpsdrzeus.com/go/&lt;your-callsign&gt;
          </code>
          .
        </div>
      )}
    </div>
  );
}

function validateUrl(raw: string): string | null {
  try {
    const u = new URL(raw);
    if (u.protocol !== 'http:' && u.protocol !== 'https:') {
      return 'Use http:// or https://';
    }
    if (!u.host) return 'Missing host';
    return null;
  } catch {
    return 'Invalid URL';
  }
}

function prioritizeCurrentHost(urls: string[]): string[] {
  const unique = Array.from(new Set(urls));
  if (typeof window === 'undefined') return unique;

  try {
    const currentHost = window.location.hostname.toLowerCase();
    const matching = unique.find((url) => new URL(url).hostname.toLowerCase() === currentHost);
    if (!matching) return unique;
    return [matching, ...unique.filter((url) => url !== matching)];
  } catch {
    return unique;
  }
}
