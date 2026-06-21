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
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import { useCallback } from 'react';
import { setPreamp } from '../api/client';
import { useConnectionStore } from '../state/connection-store';

// HermesLite2 has no hardware preamp — the RX gain path is firmware-controlled
// through the attenuator register (CC0=0x14), so a preamp toggle is a no-op
// that only confuses the user. Hide the button on HL2 entirely.
const HL2_BOARD_ID = 'HermesLite2';

export function PreampButton() {
  const boardId = useConnectionStore((s) => s.boardId);
  const preampOn = useConnectionStore((s) => s.preampOn);
  const setPreampOn = useConnectionStore((s) => s.setPreampOn);
  const applyState = useConnectionStore((s) => s.applyState);
  const connected = useConnectionStore((s) => s.status === 'Connected');

  const click = useCallback(() => {
    const next = !preampOn;
    setPreampOn(next);
    setPreamp(next)
      .then(applyState)
      .catch(() => {
        setPreampOn(!next);
      });
  }, [applyState, preampOn, setPreampOn]);

  if (boardId === HL2_BOARD_ID) return null;

  return (
    <button
      type="button"
      disabled={!connected}
      onClick={click}
      className={`btn sm ${preampOn ? 'active' : ''}`}
      title={preampOn ? 'Preamp on' : 'Preamp off'}
    >
      PRE
    </button>
  );
}
