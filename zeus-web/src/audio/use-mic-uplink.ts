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

import { useEffect } from 'react';
import {
  ensureMicUplinkRunning,
  retainMicUplinkConsumer,
} from './mic-uplink-session';
import { useTxStore } from '../state/tx-store';
import { useCapabilitiesStore } from '../state/capabilities-store';

/**
 * Opens the mic AudioWorklet on mount and keeps it running while the app
 * is live. Peak dBFS of every 20 ms block is pushed to tx-store so the
 * MicMeter renders even on RX — the operator needs to know the mic is
 * being picked up *before* keying. Uplink samples are forwarded to the
 * server only for local TX or TX Monitor / Audio Suite audition; during
 * normal RX the worklet still runs but the wire path is a no-op.
 *
 * getUserMedia requires a user gesture on first grant, but Chrome remembers
 * the grant per-origin for the session, so the capture starts silently on
 * subsequent page loads once the operator has allowed it once.
 */
export function useMicUplink(): void {
  // Phase 2c — wait for /api/capabilities before deciding whether to open
  // the mic. In desktop mode the host process captures TX audio natively
  // via miniaudio (Phase 2b); calling getUserMedia in the webview would
  // pop a redundant OS permission prompt and a second device would race
  // the native capture.
  const capsLoaded = useCapabilitiesStore((s) => s.loaded);
  const hostMode = useCapabilitiesStore((s) => s.capabilities?.host ?? null);

  useEffect(() => {
    if (!capsLoaded) return; // wait for the capabilities snapshot
    if (hostMode === 'desktop') {
      // Clear any stale "mic unavailable" error from a previous server-mode
      // session so the operator doesn't see a misleading red banner.
      useTxStore.getState().setMicError(null);
      return;
    }

    const release = retainMicUplinkConsumer();
    void ensureMicUplinkRunning().catch(() => {
      /* mic-uplink-session stores the visible error */
    });
    return release;
  }, [capsLoaded, hostMode]);
}
