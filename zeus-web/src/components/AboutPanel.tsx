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
import { UninstallDialog } from './UninstallDialog';

// Release date for the version this build ships against. Bump whenever
// VersionPrefix in Directory.Build.props bumps. ISO 8601 so toLocaleDateString
// renders sensibly in any locale.
const RELEASE_DATE_ISO = '2026-06-30';
// Note: v0.8.3 is a Windows-focused hotfix for v0.8.0..0.8.2. Fresh Windows
// installs were silently broken without the Visual C++ Runtime present, and
// even when running, Windows operators saw a growing 1-3 second MOX-engage
// delay as the audio ring drifted vs the radio clock. Three coordinated
// fixes (VC++ Runtime bundled in installer, WASAPI Pro Audio MMCSS hint,
// MOX-coupled ring drain) bring Windows responsiveness to parity with
// macOS / Linux. See CHANGELOG for the full picture.
//
// Previous note (v0.8.2 hotfix for v0.8.0/v0.8.1) — Download Audio Suite
// button's plugin array was missing Noise Gate (silently skipped on
// one-click installs) and pinning v0.1.0 of Bass/Exciter/Reverb instead
// of the v0.2.0 versions that shipped on 2026-05-19. See CHANGELOG for
// v0.8.0 for the bulk of changes (Audio Suite, dual-icon installer,
// plugin system rebuild, smoothed-SWR, persistence fixes).

type VersionInfo = {
  version: string;
};

export function AboutPanel() {
  const [versionInfo, setVersionInfo] = useState<VersionInfo>({ version: 'Loading...' });
  const [showUninstall, setShowUninstall] = useState(false);

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
      </div>

      <div style={{ marginBottom: 20, paddingTop: 20, borderTop: '1px solid var(--panel-border)' }}>
        <p style={{ margin: '0 0 12px 0', lineHeight: 1.6, color: 'var(--fg-1)' }}>
          📖{' '}
          {/* The manual PDF ships inside every installer; the backend serves it
              at /manual (see ZeusEndpoints). Opens in a new tab — and is a no-op
              in dev builds that don't bundle the PDF. */}
          <a
            href="/manual"
            target="_blank"
            rel="noopener noreferrer"
            style={{ color: 'var(--accent)', textDecoration: 'underline', fontWeight: 600 }}
          >
            Open the User Manual (PDF)
          </a>
        </p>
        <p style={{ margin: '0 0 12px 0', lineHeight: 1.6, color: 'var(--fg-1)' }}>
          OpenHPSDR Zeus is a cross-platform SDR client for OpenHPSDR Protocol-1 and Protocol-2 radios.
        </p>
        <p style={{ margin: '0 0 12px 0', lineHeight: 1.6, color: 'var(--fg-2)' }}>
          Copyright © 2025-2026 Brian Keating (EI6LF), Douglas J. Cerrato (KB2UKA), Christian
          Suarez (N9WAR), and contributors.
        </p>
        <p style={{ margin: 0, lineHeight: 1.6, color: 'var(--fg-2)', fontSize: 11 }}>
          Licensed under GNU GPL v2 or later. See{' '}
          <a
            href="https://github.com/OpenHPSDR-Zeus-org/openhpsdr-zeus"
            target="_blank"
            rel="noopener noreferrer"
            style={{ color: 'var(--accent)', textDecoration: 'underline' }}
          >
            github.com/OpenHPSDR-Zeus-org/openhpsdr-zeus
          </a>{' '}
          for source code and documentation.
        </p>
      </div>

      {/* Danger zone — full reset / uninstall. Visual placement & copy are a
          red-light item for Brian's/Doug's sign-off; the logic is server-owned
          and safety-audited. */}
      <div style={{ marginTop: 20, paddingTop: 16, borderTop: '1px solid var(--tx)' }}>
        <div style={{ color: 'var(--tx)', fontSize: 11, fontWeight: 700, letterSpacing: '0.12em', textTransform: 'uppercase', marginBottom: 8 }}>
          Danger Zone
        </div>
        <p style={{ margin: '0 0 10px', lineHeight: 1.5, color: 'var(--fg-2)', fontSize: 12 }}>
          Completely remove Zeus and wipe all of its data for a fresh install. You'll be asked
          to confirm, and you can back up your settings and logbook first.
        </p>
        <button
          type="button"
          className="btn sm"
          onClick={() => setShowUninstall(true)}
          style={{ borderColor: 'var(--tx)', color: 'var(--tx)' }}
        >
          RESET &amp; UNINSTALL ZEUS…
        </button>
      </div>

      {showUninstall && <UninstallDialog onClose={() => setShowUninstall(false)} />}
    </div>
  );
}
