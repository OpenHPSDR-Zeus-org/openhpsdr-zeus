// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
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

// Release date for the version this build ships against. Bump whenever
// VersionPrefix in Directory.Build.props bumps. ISO 8601 so toLocaleDateString
// renders sensibly in any locale.
const RELEASE_DATE_ISO = '2026-05-13';

type VersionInfo = {
  version: string;
  latestVersion?: string;
  updateAvailable?: boolean;
  checkError?: string;
};

export function AboutPanel() {
  const [versionInfo, setVersionInfo] = useState<VersionInfo>({ version: 'Loading...' });
  const [checking, setChecking] = useState(false);

  useEffect(() => {
    // Fetch current version on mount
    fetch('/api/version')
      .then((r) => r.json())
      .then((data) => {
        setVersionInfo((prev) => ({ ...prev, version: data.version }));
      })
      .catch((err) => {
        console.error('Failed to fetch version:', err);
        setVersionInfo((prev) => ({ ...prev, version: 'Unknown' }));
      });
  }, []);

  const checkForUpdates = async () => {
    setChecking(true);
    try {
      const response = await fetch(
        'https://api.github.com/repos/Kb2uka/openhpsdr-zeus/releases/latest'
      );
      if (!response.ok) {
        throw new Error(`GitHub API returned ${response.status}`);
      }
      const data = await response.json();
      const latestVersion = data.tag_name?.replace(/^v/, '') || 'unknown';
      const currentVersion = versionInfo.version.replace(/-dev$/, '');

      // Simple version comparison: split by dots and compare numerically
      const isNewer = compareVersions(latestVersion, currentVersion) > 0;

      setVersionInfo((prev) => ({
        ...prev,
        latestVersion,
        updateAvailable: isNewer,
        checkError: undefined,
      }));
    } catch (err) {
      console.error('Failed to check for updates:', err);
      setVersionInfo((prev) => ({
        ...prev,
        checkError: err instanceof Error ? err.message : 'Network error',
      }));
    } finally {
      setChecking(false);
    }
  };

  return (
    <div style={{ maxWidth: 600 }}>
      <h3
        style={{
          margin: '0 0 16px 0',
          fontSize: 13,
          fontWeight: 700,
          letterSpacing: '0.12em',
          textTransform: 'uppercase',
          color: 'var(--fg-0)',
        }}
      >
        About OpenHPSDR Zeus
      </h3>

      <div style={{ marginBottom: 20 }}>
        <div style={{ marginBottom: 12 }}>
          <span style={{ color: 'var(--fg-2)', marginRight: 8 }}>Version:</span>
          <span style={{ color: 'var(--accent)', fontWeight: 600 }}>
            {/* Strip -dev / -prerelease / +commit-sha suffix so the About
                pane shows the clean SemVer triple regardless of whether
                the build was tagged. The full string is still available
                via /api/version for support purposes. */}
            {versionInfo.version.replace(/[-+].*$/, '')}
          </span>
        </div>

        <div style={{ marginBottom: 12 }}>
          <span style={{ color: 'var(--fg-2)', marginRight: 8 }}>Released:</span>
          <span style={{ color: 'var(--fg-1)', fontWeight: 600 }}>
            {new Date(RELEASE_DATE_ISO).toLocaleDateString(undefined, {
              year: 'numeric',
              month: 'long',
              day: 'numeric',
            })}
          </span>
        </div>

        {versionInfo.latestVersion && (
          <div style={{ marginBottom: 12 }}>
            <span style={{ color: 'var(--fg-2)', marginRight: 8 }}>Latest Release:</span>
            <span style={{ color: 'var(--fg-1)', fontWeight: 600 }}>{versionInfo.latestVersion}</span>
          </div>
        )}

        {versionInfo.updateAvailable === true && (
          <div
            style={{
              padding: 10,
              marginBottom: 12,
              borderRadius: 'var(--r-sm)',
              background: 'rgba(255, 160, 40, 0.1)',
              border: '1px solid rgba(255, 160, 40, 0.3)',
              color: 'var(--accent)',
            }}
          >
            🎉 A new version is available!{' '}
            <a
              href="https://github.com/Kb2uka/openhpsdr-zeus/releases/latest"
              target="_blank"
              rel="noopener noreferrer"
              style={{ color: 'var(--accent)', textDecoration: 'underline' }}
            >
              Download here
            </a>
          </div>
        )}

        {versionInfo.updateAvailable === false && versionInfo.latestVersion && (
          <div
            style={{
              padding: 10,
              marginBottom: 12,
              borderRadius: 'var(--r-sm)',
              background: 'rgba(100, 200, 100, 0.1)',
              border: '1px solid rgba(100, 200, 100, 0.3)',
              color: '#6c6',
            }}
          >
            ✓ You are running the latest version
          </div>
        )}

        {versionInfo.checkError && (
          <div
            style={{
              padding: 10,
              marginBottom: 12,
              borderRadius: 'var(--r-sm)',
              background: 'rgba(200, 100, 100, 0.1)',
              border: '1px solid rgba(200, 100, 100, 0.3)',
              color: '#c66',
            }}
          >
            Failed to check for updates: {versionInfo.checkError}
          </div>
        )}

        <button type="button" className="btn sm" onClick={checkForUpdates} disabled={checking}>
          {checking ? 'CHECKING…' : 'CHECK FOR UPDATES'}
        </button>
      </div>

      <div style={{ marginBottom: 20, paddingTop: 20, borderTop: '1px solid var(--panel-border)' }}>
        <p style={{ margin: '0 0 12px 0', lineHeight: 1.6, color: 'var(--fg-1)' }}>
          OpenHPSDR Zeus is a cross-platform SDR client for OpenHPSDR Protocol-1 and Protocol-2 radios.
        </p>
        <p style={{ margin: '0 0 12px 0', lineHeight: 1.6, color: 'var(--fg-2)' }}>
          Copyright © 2025-2026 Brian Keating (EI6LF), Douglas J. Cerrato (KB2UKA), and
          contributors.
        </p>
        <p style={{ margin: 0, lineHeight: 1.6, color: 'var(--fg-2)', fontSize: 11 }}>
          Licensed under GNU GPL v2 or later. See{' '}
          <a
            href="https://github.com/Kb2uka/openhpsdr-zeus"
            target="_blank"
            rel="noopener noreferrer"
            style={{ color: 'var(--accent)', textDecoration: 'underline' }}
          >
            github.com/Kb2uka/openhpsdr-zeus
          </a>{' '}
          for source code and documentation.
        </p>
      </div>
    </div>
  );
}

// Compare semver strings (e.g., "0.1.0" vs "0.2.0")
// Returns: -1 if a < b, 0 if equal, 1 if a > b
function compareVersions(a: string, b: string): number {
  const aParts = a.split('.').map((x) => parseInt(x, 10) || 0);
  const bParts = b.split('.').map((x) => parseInt(x, 10) || 0);
  const maxLen = Math.max(aParts.length, bParts.length);

  for (let i = 0; i < maxLen; i++) {
    const aVal = aParts[i] || 0;
    const bVal = bParts[i] || 0;
    if (aVal < bVal) return -1;
    if (aVal > bVal) return 1;
  }
  return 0;
}
