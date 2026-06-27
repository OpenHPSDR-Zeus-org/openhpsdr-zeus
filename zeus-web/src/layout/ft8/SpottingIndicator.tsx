// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Small status-bar indicator for the digital-mode spotting uploaders. Accent
// (blue) when the relevant uploader is enabled AND operator identity is resolved;
// --tx (red) when enabled but missing callsign/grid; dim when off. Read-only — it
// reflects the /api/spotting state that the Network settings tab owns.

import { useEffect } from 'react';
import { useSpottingStore } from '../../state/spotting-store';

export function SpottingIndicator({ kind }: { kind: 'psk' | 'wsprnet' }) {
  const status = useSpottingStore((s) => s.status);
  const refreshStatus = useSpottingStore((s) => s.refreshStatus);

  // Pick up any toggle made on the settings tab while a workspace is open.
  useEffect(() => {
    void refreshStatus();
  }, [refreshStatus]);

  const enabled =
    kind === 'psk' ? (status?.pskReporterEnabled ?? false) : (status?.wsprnetEnabled ?? false);
  const identityResolved = status?.identityResolved ?? false;
  const label = kind === 'psk' ? 'PSK REPORTER' : 'WSPRNET';

  const color = !enabled
    ? 'var(--fg-3)'
    : identityResolved
      ? 'var(--accent)'
      : 'var(--tx)';

  const text = !enabled
    ? `${label} off`
    : identityResolved
      ? `${label} on`
      : `${label} — set call/grid`;

  return (
    <span
      title="Digital-mode spotting (Settings → Network)"
      style={{ display: 'inline-flex', alignItems: 'center', gap: 6 }}
    >
      <span
        style={{
          width: 8,
          height: 8,
          borderRadius: '50%',
          background: color,
          flexShrink: 0,
        }}
      />
      {text}
    </span>
  );
}
