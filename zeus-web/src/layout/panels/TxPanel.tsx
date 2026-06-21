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

import { TxFilterPanel } from '../../components/TxFilterPanel';

// Flex-layout wrapper around the existing TxFilterPanel so operators can drop
// the DRV / TUN / MIC sliders + bandpass controls into any flex tabset.
// TxFilterPanel was previously only mounted in the legacy CSS-grid right rail
// (App.tsx:963), which left flex-layout users with no way to reach the mic
// or drive sliders without flipping back to grid mode.
export function TxPanel() {
  return (
    <div style={{ flex: 1, overflow: 'auto' }}>
      <TxFilterPanel />
    </div>
  );
}
