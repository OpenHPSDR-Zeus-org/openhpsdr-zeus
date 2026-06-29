// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.

import { useEffect, useState } from 'react';
import { useQrzStore } from '../state/qrz-store';

export function QrzSettingsPanel() {
  const connected = useQrzStore((s) => s.connected);
  const hasXml = useQrzStore((s) => s.hasXmlSubscription);
  const hasApiKey = useQrzStore((s) => s.hasApiKey);
  const home = useQrzStore((s) => s.home);
  const rememberedUsername = useQrzStore((s) => s.rememberedUsername);
  const loginInFlight = useQrzStore((s) => s.loginInFlight);
  const loginError = useQrzStore((s) => s.loginError);
  const login = useQrzStore((s) => s.login);
  const logout = useQrzStore((s) => s.logout);
  const setApiKey = useQrzStore((s) => s.setApiKey);

  const [username, setUsername] = useState(rememberedUsername);
  const [password, setPassword] = useState('');
  const [apiKeyInput, setApiKeyInput] = useState('');
  const [showApiKeyInput, setShowApiKeyInput] = useState(false);

  // Keep the form's username in sync when the store hydrates from localStorage
  // after first render (initial value of rememberedUsername may have been '').
  useEffect(() => {
    if (!username && rememberedUsername) setUsername(rememberedUsername);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [rememberedUsername]);

  async function onSubmit(e: React.FormEvent) {
    e.preventDefault();
    const ok = await login(username.trim(), password);
    if (ok) {
      setPassword('');
    }
  }

  async function onSaveApiKey() {
    await setApiKey(apiKeyInput.trim() || null);
    setShowApiKeyInput(false);
    setApiKeyInput('');
  }

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
        QRZ.COM INTEGRATION
      </h3>

      {connected ? (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
            <div style={{ fontSize: 13, color: 'var(--fg-1)' }}>
              Signed in as{' '}
              <span style={{ fontWeight: 600, color: 'var(--accent)' }}>{home?.callsign}</span>
            </div>
            {home?.grid && (
              <div style={{ fontSize: 12, color: 'var(--fg-2)' }}>
                Home grid <span style={{ fontFamily: 'monospace' }}>{home.grid}</span>
                {home.lat != null && home.lon != null && (
                  <>
                    {' · '}
                    {home.lat.toFixed(2)}, {home.lon.toFixed(2)}
                  </>
                )}
              </div>
            )}
            <div
              style={{
                fontSize: 12,
                color: hasXml ? 'var(--accent)' : 'var(--power)',
                fontWeight: 500,
              }}
            >
              {hasXml ? '✓ XML subscription active' : '⚠ No XML subscription — lookups disabled'}
            </div>
          </div>

          <div
            style={{
              padding: 14,
              background: 'var(--bg-0)',
              borderRadius: 'var(--r-sm)',
              border: '1px solid var(--panel-border)',
            }}
          >
            <div style={{ marginBottom: 8, fontSize: 11, fontWeight: 600, color: 'var(--fg-2)' }}>
              QRZ API Key {hasApiKey && <span style={{ color: 'var(--accent)' }}>●</span>}
            </div>
            {showApiKeyInput ? (
              <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
                <input
                  type="password"
                  value={apiKeyInput}
                  onChange={(e) => setApiKeyInput(e.target.value)}
                  placeholder="Enter API key"
                  style={{
                    padding: '6px 8px',
                    fontSize: 12,
                    fontFamily: 'monospace',
                    background: 'var(--bg-2)',
                    border: '1px solid var(--panel-border)',
                    borderRadius: 'var(--r-sm)',
                    color: 'var(--fg-0)',
                  }}
                />
                <div style={{ display: 'flex', gap: 6 }}>
                  <button type="button" className="btn sm active" onClick={onSaveApiKey}>
                    SAVE
                  </button>
                  <button
                    type="button"
                    className="btn sm"
                    onClick={() => {
                      setShowApiKeyInput(false);
                      setApiKeyInput('');
                    }}
                  >
                    CANCEL
                  </button>
                </div>
              </div>
            ) : (
              <button
                type="button"
                className="btn sm"
                onClick={() => setShowApiKeyInput(true)}
              >
                {hasApiKey ? 'UPDATE API KEY' : 'SET API KEY'}
              </button>
            )}
            <div
              style={{
                marginTop: 8,
                fontSize: 10,
                lineHeight: 1.4,
                color: 'var(--fg-3)',
              }}
            >
              Required for publishing QSOs to QRZ logbook.{' '}
              <a
                href="https://www.qrz.com/docs/logbook30/api"
                target="_blank"
                rel="noopener noreferrer"
                style={{ color: 'var(--accent)' }}
              >
                Find your API key
              </a>
            </div>
          </div>

          <button type="button" className="btn sm" onClick={() => logout()}>
            SIGN OUT
          </button>
        </div>
      ) : (
        <form onSubmit={onSubmit} style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
          <label style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
            <span style={{ fontSize: 11, fontWeight: 600, color: 'var(--fg-2)' }}>
              QRZ Username
            </span>
            <input
              type="text"
              value={username}
              onChange={(e) => setUsername(e.target.value)}
              autoComplete="username"
              spellCheck={false}
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
          <label style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
            <span style={{ fontSize: 11, fontWeight: 600, color: 'var(--fg-2)' }}>Password</span>
            <input
              type="password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              autoComplete="current-password"
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
          {loginError && (
            <div style={{ fontSize: 12, color: 'var(--tx)', fontWeight: 500 }}>{loginError}</div>
          )}
          <button
            type="submit"
            disabled={loginInFlight || !username || !password}
            className="btn sm active"
            style={{ alignSelf: 'flex-start' }}
          >
            {loginInFlight ? 'SIGNING IN…' : 'SIGN IN'}
          </button>
          <div
            style={{
              fontSize: 10,
              lineHeight: 1.4,
              color: 'var(--fg-3)',
            }}
          >
            Credentials are sent to the Zeus backend and used to fetch a QRZ session key. Username
            is remembered locally; the password is not stored.
          </div>
        </form>
      )}
    </div>
  );
}
