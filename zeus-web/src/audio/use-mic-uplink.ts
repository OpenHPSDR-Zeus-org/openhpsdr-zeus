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
import { startMicUplink, type MicUplinkHandle } from './mic-uplink';
import { sendMicPcm } from '../realtime/ws-client';
import { useTxStore } from '../state/tx-store';
import { warnOnce } from '../util/logger';

// Silence floor for the mic meter — below this we clamp so the UI doesn't
// render -∞ and so the bar snaps to fully-empty on a quiet mic.
const MIC_DBFS_FLOOR = -100;

// Visual update cadence for MicMeter. The mic worklet emits a peak per
// 20 ms block (50 Hz); driving Zustand at that rate triggers ~50 React
// reconciliations a second of the bottom-bar meter, which is invisible to
// the operator but a real CPU drain. We bucket the worklet's per-block
// peaks across a ~50 ms window (20 Hz visual rate, smoother than TV) and
// emit the *maximum* of each window so clip indication stays accurate.
const MIC_VISUAL_INTERVAL_MS = 50;

/**
 * Opens the mic AudioWorklet on mount and keeps it running while the app
 * is live. Peak dBFS of every 20 ms block is pushed to tx-store so the
 * MicMeter renders even on RX — the operator needs to know the mic is
 * being picked up *before* keying. Uplink samples are only forwarded to
 * the server when MOX is on; during RX the worklet still runs but the
 * wire path is a no-op.
 *
 * getUserMedia requires a user gesture on first grant, but Chrome remembers
 * the grant per-origin for the session, so the capture starts silently on
 * subsequent page loads once the operator has allowed it once.
 */
export function useMicUplink(): void {
  useEffect(() => {
    let handle: MicUplinkHandle | null = null;
    let disposed = false;
    let windowPeak = 0;
    let lastEmit = 0;

    startMicUplink((samples, peak) => {
      // Level: always pushed so MicMeter animates on RX. Bucket the
      // per-block peaks across MIC_VISUAL_INTERVAL_MS and emit the max so
      // a transient clip is never lost between visual ticks.
      if (peak > windowPeak) windowPeak = peak;
      const now = performance.now();
      if (now - lastEmit >= MIC_VISUAL_INTERVAL_MS) {
        const dbfs = windowPeak > 0
          ? Math.max(MIC_DBFS_FLOOR, 20 * Math.log10(windowPeak))
          : MIC_DBFS_FLOOR;
        useTxStore.getState().setMicDbfs(dbfs);
        lastEmit = now;
        windowPeak = 0;
      }

      // Samples: only forwarded when the local operator has actually keyed
      // (MoxButton / spacebar PTT / MobilePttButton). Capturing always +
      // gating here avoids a ~300 ms getUserMedia cold-start on every MOX.
      // Gated on localMicArmed rather than moxOn so a server-side MOX edge
      // from a TCI client (WSJT-X / MSHV) doesn't make the browser race the
      // TCI audio path into TxAudioIngest._accumulator. See issue #346.
      if (useTxStore.getState().localMicArmed) sendMicPcm(samples);
    })
      .then((h) => {
        if (disposed) { void h.stop(); return; }
        handle = h;
        useTxStore.getState().setMicError(null);
      })
      .catch((err: unknown) => {
        const msg = err instanceof Error ? err.message : String(err);
        warnOnce('mic-uplink-failed', `mic capture unavailable: ${msg}`);
        useTxStore.getState().setMicError(msg);
      });

    return () => {
      disposed = true;
      const h = handle;
      handle = null;
      if (h) void h.stop();
    };
  }, []);
}
