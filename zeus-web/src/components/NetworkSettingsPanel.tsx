// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import { CatSettingsPanel } from './CatSettingsPanel';
import { TciSettingsPanel } from './TciSettingsPanel';
import { WsjtxSettingsPanel } from './WsjtxSettingsPanel';

// The Network tab hosts Zeus's external network-control servers. TCI (the
// ExpertSDR3 WebSocket interface) and CAT (the Kenwood TS-2000 TCP interface)
// are inbound rig-control servers; WSJT-X is the outbound logged-QSO push to a
// third-party logger. Each child panel owns its own store and persistence —
// this container only stacks them with separating rules.
export function NetworkSettingsPanel() {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 24 }}>
      <TciSettingsPanel />
      <div style={{ borderTop: '1px solid var(--panel-border)' }} />
      <CatSettingsPanel />
      <div style={{ borderTop: '1px solid var(--panel-border)' }} />
      <WsjtxSettingsPanel />
    </div>
  );
}
