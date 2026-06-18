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

import { useCallback } from 'react';
import { setCtun } from '../api/client';
import { useConnectionStore } from '../state/connection-store';

/**
 * CTUN (click-tune / centred tuning) toggle. When armed, a click anywhere on
 * the panadapter/waterfall tunes the dial off-centre while the hardware NCO
 * stays frozen — the crosshair roams instead of the display recentring. TX
 * still lands on the dial (the radio retunes on key-down). When off, a click
 * recentres the display on the tuned frequency (classic "radio follows the
 * dial"). Optimistic toggle with rollback on server refusal, same pattern as
 * MoxButton / PsToggleButton.
 */
export function CtunButton() {
  const connected = useConnectionStore((s) => s.status === 'Connected');
  const ctunEnabled = useConnectionStore((s) => s.ctunEnabled);

  const click = useCallback(() => {
    if (!connected) return;
    const next = !ctunEnabled;
    useConnectionStore.setState({ ctunEnabled: next });
    setCtun(next)
      .then((s) => useConnectionStore.getState().applyState(s))
      .catch(() => useConnectionStore.setState({ ctunEnabled: !next }));
  }, [connected, ctunEnabled]);

  return (
    <button
      type="button"
      disabled={!connected}
      onClick={click}
      className={`btn ghost hide-mobile ${ctunEnabled ? 'active' : ''}`}
      title={
        ctunEnabled
          ? 'CTUN on — click the panadapter to tune off-centre (hardware NCO frozen, TX follows the dial)'
          : 'CTUN off — click recentres the display on the tuned frequency'
      }
    >
      CTUN
    </button>
  );
}
