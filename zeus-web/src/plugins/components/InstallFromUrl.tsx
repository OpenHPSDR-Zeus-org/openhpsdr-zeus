// SPDX-License-Identifier: GPL-2.0-or-later
//
// InstallFromUrl — small form letting the operator paste an HTTPS URL
// for a plugin .zip (BYOP / "bring your own plugin"). The optional
// SHA-256 input is verified by Zeus.Plugins.Host before extraction —
// recommended for plugins downloaded outside the registry. Submission
// posts { source: "url", url, sha256? } to /api/plugins/install.

import { useState, type FormEvent } from 'react';

import { usePluginsStore } from '../state/plugins-store';

function isProbablyHttpsUrl(s: string): boolean {
  const trimmed = s.trim();
  if (!trimmed) return false;
  try {
    const u = new URL(trimmed);
    return u.protocol === 'https:' || u.protocol === 'http:';
  } catch {
    return false;
  }
}

function isHexSha256(s: string): boolean {
  const t = s.trim();
  if (t === '') return true; // optional
  return /^[0-9a-fA-F]{64}$/.test(t);
}

export function InstallFromUrl() {
  const install = usePluginsStore((s) => s.install);
  const installing = usePluginsStore((s) => s.installInflight);
  const lastError = usePluginsStore((s) => s.lastInstallError);
  const lastOk = usePluginsStore((s) => s.lastInstallOk);
  const clearFeedback = usePluginsStore((s) => s.clearInstallFeedback);

  const [url, setUrl] = useState('');
  const [sha256, setSha256] = useState('');

  const urlOk = isProbablyHttpsUrl(url);
  const shaOk = isHexSha256(sha256);
  const canSubmit = !installing && urlOk && shaOk;

  const onSubmit = async (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    if (!canSubmit) return;
    const trimmedSha = sha256.trim();
    const result = await install({
      source: 'url',
      url: url.trim(),
      sha256: trimmedSha.length > 0 ? trimmedSha : undefined,
    });
    if (result) {
      // Clear the form on success so the operator can install another.
      setUrl('');
      setSha256('');
    }
  };

  return (
    <form
      data-testid="plugins-install-from-url"
      onSubmit={onSubmit}
      style={{ display: 'flex', flexDirection: 'column', gap: 10 }}
    >
      <div style={{ color: 'var(--fg-2)', fontSize: 11, lineHeight: 1.5 }}>
        Paste an HTTPS URL to a plugin .zip. The optional SHA-256 is verified
        against the downloaded bytes before extraction — recommended for any
        zip not served by the official registry.
      </div>

      <label
        style={{ display: 'flex', flexDirection: 'column', gap: 4 }}
        htmlFor="plugin-install-url"
      >
        <span
          style={{
            fontSize: 11,
            letterSpacing: '0.08em',
            textTransform: 'uppercase',
            color: 'var(--fg-2)',
          }}
        >
          Zip URL
        </span>
        <input
          id="plugin-install-url"
          type="url"
          autoComplete="off"
          spellCheck={false}
          placeholder="https://example.com/my-plugin-1.0.0.zip"
          value={url}
          onChange={(e) => {
            setUrl(e.target.value);
            if (lastError || lastOk) clearFeedback();
          }}
          style={{
            background: 'var(--bg-0)',
            border: '1px solid var(--line-strong)',
            borderRadius: 'var(--r-sm)',
            color: 'var(--fg-0)',
            padding: '6px 8px',
            fontSize: 12,
            fontFamily: 'var(--font-mono)',
          }}
        />
      </label>

      <label
        style={{ display: 'flex', flexDirection: 'column', gap: 4 }}
        htmlFor="plugin-install-sha"
      >
        <span
          style={{
            fontSize: 11,
            letterSpacing: '0.08em',
            textTransform: 'uppercase',
            color: 'var(--fg-2)',
          }}
        >
          SHA-256 (optional)
        </span>
        <input
          id="plugin-install-sha"
          type="text"
          autoComplete="off"
          spellCheck={false}
          placeholder="64 hex characters"
          value={sha256}
          onChange={(e) => {
            setSha256(e.target.value);
            if (lastError || lastOk) clearFeedback();
          }}
          style={{
            background: 'var(--bg-0)',
            border: `1px solid ${shaOk ? 'var(--line-strong)' : 'var(--tx)'}`,
            borderRadius: 'var(--r-sm)',
            color: 'var(--fg-0)',
            padding: '6px 8px',
            fontSize: 12,
            fontFamily: 'var(--font-mono)',
          }}
          aria-invalid={!shaOk}
        />
        {!shaOk && (
          <span style={{ color: 'var(--tx)', fontSize: 11 }}>
            SHA-256 must be 64 hex characters.
          </span>
        )}
      </label>

      <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 6 }}>
        <button type="submit" className="btn sm active" disabled={!canSubmit}>
          {installing ? 'INSTALLING…' : 'INSTALL'}
        </button>
      </div>

      {lastOk && (
        <div
          role="status"
          style={{
            padding: 10,
            borderRadius: 'var(--r-sm)',
            background: 'var(--ok-soft)',
            border: '1px solid var(--ok)',
            color: 'var(--fg-0)',
          }}
        >
          {lastOk}
        </div>
      )}

      {lastError && (
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
          Install failed: {lastError}
        </div>
      )}
    </form>
  );
}
