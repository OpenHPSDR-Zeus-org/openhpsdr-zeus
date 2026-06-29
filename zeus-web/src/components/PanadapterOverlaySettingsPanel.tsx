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
// Panadapter overlay toggles in the Display settings tab. Currently the single
// chat-roster overlay switch (also reachable from the Chat panel header); a
// natural home for future panadapter-overlay preferences.

import type { CSSProperties } from 'react';
import { useDisplaySettingsStore } from '../state/display-settings-store';

export function PanadapterOverlaySettingsPanel() {
  const showChatRoster = useDisplaySettingsStore((s) => s.showChatRosterOverlay);
  const setShowChatRoster = useDisplaySettingsStore((s) => s.setShowChatRosterOverlay);

  return (
    <section>
      <div style={sectionHead}>
        <h3 style={sectionH3}>Panadapter Overlays</h3>
        <p style={sectionP}>
          What gets painted over the spectrum on top of the trace and waterfall.
        </p>
      </div>

      <div style={card}>
        <div style={autoRow}>
          <label style={switchLabel}>
            <input
              type="checkbox"
              checked={showChatRoster}
              onChange={(event) => setShowChatRoster(event.currentTarget.checked)}
              style={{ accentColor: 'var(--accent)' }}
            />
            Show Chat Operators
          </label>
          <span style={autoHint}>
            Paints connected ZeusChat operators — callsign + transmit dot — onto
            the panadapter at the frequency they're tuned to. Only operators who
            share their frequency (friends) appear. Also toggleable from the Chat
            panel header.
          </span>
        </div>
      </div>
    </section>
  );
}

const sectionHead: CSSProperties = {
  display: 'flex',
  alignItems: 'baseline',
  flexWrap: 'wrap',
  gap: 10,
  marginBottom: 10,
};

const sectionH3: CSSProperties = {
  margin: 0,
  fontSize: 11,
  fontWeight: 700,
  letterSpacing: '0.18em',
  textTransform: 'uppercase',
  color: 'var(--fg-0)',
};

const sectionP: CSSProperties = {
  margin: 0,
  flex: '1 1 260px',
  fontSize: 12,
  lineHeight: 1.5,
  color: 'var(--fg-2)',
};

const card: CSSProperties = {
  display: 'grid',
  gap: 8,
  padding: 10,
  border: '1px solid var(--line)',
  borderRadius: 'var(--r-md)',
  background: 'linear-gradient(180deg, var(--bg-1), var(--bg-0))',
};

const autoRow: CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  justifyContent: 'space-between',
  gap: 10,
  flexWrap: 'wrap',
};

const switchLabel: CSSProperties = {
  display: 'inline-flex',
  alignItems: 'center',
  gap: 8,
  fontSize: 11,
  fontWeight: 800,
  letterSpacing: '0.08em',
  textTransform: 'uppercase',
  color: 'var(--fg-0)',
};

const autoHint: CSSProperties = {
  flex: '1 1 260px',
  fontSize: 11,
  lineHeight: 1.35,
  color: 'var(--fg-3)',
};
