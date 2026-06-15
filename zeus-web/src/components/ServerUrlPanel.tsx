// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
// See LICENSE / ATTRIBUTIONS.md at the repository root.

import { useEffect, useState } from 'react';
import {
  getServerBaseUrl,
  isCapacitorRuntime,
  setServerBaseUrl,
} from '../serverUrl';
import { useCapabilitiesStore } from '../state/capabilities-store';

// Settings tab: lets the operator point a Capacitor / standalone build at a
// specific Zeus.Server on their LAN (e.g. http://192.168.1.23:6060). Browser
// users on the bundled deploy normally leave this blank — relative paths
// already reach the same-origin server.

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
          http://192.168.1.23:6060
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
          placeholder="http://192.168.1.23:6060"
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
