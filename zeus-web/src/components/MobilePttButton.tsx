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

import { useCallback, useRef } from 'react';
import { setMox } from '../api/client';
import { ensureMicUplinkRunning, setMicUplinkTxForced } from '../audio/mic-uplink-session';
import { ensureTxStationProfileActivated } from '../audio/tx-station-profile-activation';
import { waitForMicPcmTransportReady } from '../realtime/ws-client';
import { useConnectionStore } from '../state/connection-store';
import { useTxStore } from '../state/tx-store';
import { runTxBandPreflight } from '../tx/tx-band-preflight';

// Press-and-hold PTT for mobile. Pointer events mirror the spacebar-PTT
// pattern in use-keyboard-shortcuts — driveMox(true) on pointerdown,
// driveMox(false) on pointerup/cancel/leave so a drag off the button still
// releases the key. setPointerCapture keeps the up event owned by this
// element even if the finger strays outside the hit area.
export function MobilePttButton() {
  const connected = useConnectionStore((s) => s.status === 'Connected');
  const moxOn = useTxStore((s) => s.moxOn);
  const setMoxOn = useTxStore((s) => s.setMoxOn);
  const setLocalMicArmed = useTxStore((s) => s.setLocalMicArmed);
  const abortRef = useRef<AbortController | null>(null);
  const gestureRef = useRef(0);

  const drive = useCallback(
    async (on: boolean) => {
      const gesture = ++gestureRef.current;
      if (on) {
        try {
          await ensureTxStationProfileActivated();
          const ready = await runTxBandPreflight();
          if (!ready) {
            setMicUplinkTxForced(false);
            return;
          }
        } catch {
          setMicUplinkTxForced(false);
          return;
        }
        if (gesture !== gestureRef.current) return;
        try {
          const micReady = await ensureMicUplinkRunning();
          if (!micReady) return;
          const transportReady = await waitForMicPcmTransportReady();
          if (!transportReady) return;
        } catch {
          setMicUplinkTxForced(false);
          return;
        }
        if (gesture !== gestureRef.current) return;
        setMicUplinkTxForced(true);
      } else {
        setMicUplinkTxForced(false);
      }

      if (useTxStore.getState().moxOn === on) {
        if (!on && useTxStore.getState().localMicArmed) setLocalMicArmed(false);
        return;
      }
      setMoxOn(on);
      setLocalMicArmed(on);
      abortRef.current?.abort();
      const ctrl = new AbortController();
      abortRef.current = ctrl;
      setMox(on, ctrl.signal).catch(() => {
        if (!ctrl.signal.aborted) {
          setMoxOn(!on);
          setLocalMicArmed(!on);
          if (on) setMicUplinkTxForced(false);
        }
      });
    },
    [setMoxOn, setLocalMicArmed],
  );

  const onDown = useCallback(
    (e: React.PointerEvent<HTMLButtonElement>) => {
      if (!connected) return;
      e.currentTarget.setPointerCapture(e.pointerId);
      void drive(true);
    },
    [connected, drive],
  );

  const onUp = useCallback(
    (e: React.PointerEvent<HTMLButtonElement>) => {
      if (e.currentTarget.hasPointerCapture(e.pointerId)) {
        e.currentTarget.releasePointerCapture(e.pointerId);
      }
      void drive(false);
    },
    [drive],
  );

  return (
    <button
      type="button"
      disabled={!connected}
      className={`mobile-ptt-btn ${moxOn ? 'tx' : ''}`}
      onPointerDown={onDown}
      onPointerUp={onUp}
      onPointerCancel={onUp}
      onContextMenu={(e) => e.preventDefault()}
    >
      <span className={`led ${moxOn ? 'tx' : 'on'}`} />
      <span className="mobile-ptt-label">{moxOn ? 'TX' : 'PTT'}</span>
      <span className="mobile-ptt-hint">{moxOn ? 'release to stop' : 'hold to transmit'}</span>
    </button>
  );
}
