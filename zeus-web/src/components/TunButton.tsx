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

import { useCallback, useState } from 'react';
import { setTun } from '../api/client';
import { ensureTxStationProfileActivated } from '../audio/tx-station-profile-activation';
import { useConnectionStore } from '../state/connection-store';
import { useTxStore } from '../state/tx-store';
import { runTxBandPreflight } from '../tx/tx-band-preflight';

/**
 * PRD FR-7: TUN keys a single-tone carrier (WDSP SetTXAPostGen*) and is
 * mutually exclusive with MOX — the store setters enforce the exclusion so
 * the click handler stays dumb. Server clamps drive to min(drive, 25%) while
 * TUN is on; the DriveSlider reflects whatever StateDto reports back.
 */
export function TunButton() {
  const connected = useConnectionStore((s) => s.status === 'Connected');
  const tunOn = useTxStore((s) => s.tunOn);
  const setTunOn = useTxStore((s) => s.setTunOn);
  const [arming, setArming] = useState(false);

  const click = useCallback(() => {
    const next = !tunOn;
    void (async () => {
      if (next) {
        setArming(true);
        try {
          await ensureTxStationProfileActivated();
          const approved = await runTxBandPreflight();
          if (!approved) {
            setArming(false);
            return;
          }
        } catch (err) {
          console.warn('TX preflight failed; TUN cancelled', err);
          setArming(false);
          return;
        }
        setArming(false);
      }
      setTunOn(next);
      setTun(next)
        .catch(() => {
          setTunOn(!next);
        });
    })();
  }, [tunOn, setTunOn]);

  return (
    <button
      type="button"
      disabled={!connected || arming}
      onClick={click}
      className={`btn tx-btn ${tunOn ? 'active' : ''}`}
      title={
        arming
          ? 'Applying TX profile before tune'
          : tunOn
            ? 'TUN on — single-tone carrier'
            : 'TUN off (single-tone carrier for tuning)'
      }
    >
      {arming ? 'ARM' : 'TUNE'}
    </button>
  );
}
