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

import { useCallback } from 'react';
import { setTwoTone } from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { useTxStore } from '../state/tx-store';

/**
 * Two-tone test signal arm. Standard PureSignal calibration excitation,
 * but useful standalone for IMD measurements / linearity checks. Always
 * available when connected — TwoTone is protocol-agnostic; safe on P1
 * even though PS itself is P2-only in v1.
 */
export function TwoToneButton() {
  const connected = useConnectionStore((s) => s.status === 'Connected');
  const twoToneOn = useTxStore((s) => s.twoToneOn);
  const setTwoToneOn = useTxStore((s) => s.setTwoToneOn);
  const f1 = useTxStore((s) => s.twoToneFreq1);
  const f2 = useTxStore((s) => s.twoToneFreq2);
  const mag = useTxStore((s) => s.twoToneMag);

  const click = useCallback(() => {
    const next = !twoToneOn;
    setTwoToneOn(next);
    setTwoTone({ enabled: next, freq1: f1, freq2: f2, mag }).catch(() => {
      setTwoToneOn(!next);
    });
  }, [twoToneOn, f1, f2, mag, setTwoToneOn]);

  return (
    <button
      type="button"
      disabled={!connected}
      onClick={click}
      className={`btn tx-btn ${twoToneOn ? 'tx' : ''}`}
      title={
        twoToneOn
          ? 'Two-tone test signal armed'
          : 'Arm two-tone test signal (PS calibration / IMD)'
      }
    >
      <span className={`led ${twoToneOn ? 'tx' : ''}`} style={{ marginRight: 8 }} />
      2-TONE
    </button>
  );
}
