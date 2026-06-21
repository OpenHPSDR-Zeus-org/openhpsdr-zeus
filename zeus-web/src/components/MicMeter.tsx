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

import { useRef } from 'react';
import { useTxStore } from '../state/tx-store';
import { useMicPeakStore } from '../audio/mic-peak-store';
import { useCapabilitiesStore } from '../state/capabilities-store';
import { useBallisticReading } from './meters/useBallisticReading';
import { mercuryGradientCss } from './meters/render/fillGradient';

// Pre-TX mic level indicator. The worklet measures peak dBFS on the raw
// browser capture *before* any gain; the server then applies
// SetTXAPanelGain1(10^(micGainDb/20)) before the sample reaches TXA/ALC.
// So the level that actually clips is (rawDbfs + micGainDb) — we display
// that "effective" value so the operator can set gain before keying
// without driving into distortion (the Thetis workflow). micGainDb is
// signed: negative values attenuate, positive boost.
//
// Visual sits in the transport strip as a .knob-group, using the design's
// meter-bar chrome. A permanent red zone paints the last 3 dB of the
// scale so peaks have a clear "target just below" indicator. Peak-hold
// comes from the shared meter ballistics hook.

const MIN_DBFS = -60;
const MAX_DBFS = 0;
const CLIP_WARN_DBFS = -3;

function dbfsToFraction(dbfs: number): number {
  const clamped = Math.max(MIN_DBFS, Math.min(MAX_DBFS, dbfs));
  return (clamped - MIN_DBFS) / (MAX_DBFS - MIN_DBFS);
}

const RED_ZONE_START = dbfsToFraction(CLIP_WARN_DBFS);

export function MicMeter() {
  // Phase 4 — dual-source meter input. In server (browser) mode the SPA's
  // AudioWorklet pushes rawDbfs into tx-store. In desktop mode the worklet
  // is disabled (Phase 2c) so we read the server-published MicPeakFrame
  // (0x1C) from useMicPeakStore instead. The two stores never converge —
  // each transport writes its own — so the meter shows whichever path is
  // actually live without bookkeeping in the hot loop.
  //
  // Subscriptions kept to fields that change from operator action (rare,
  // not 10 Hz): hostMode flips on host startup, micGainDb on the gain knob,
  // micError on a permission denial. The dBFS sample fields (micDbfs,
  // peakDbfs) are read inside the rAF closure below via getState() so the
  // 10 Hz wire stream doesn't drag this widget into re-rendering at the
  // wire rate — the ballistic hook owns the visible cadence.
  const hostMode = useCapabilitiesStore((s) => s.capabilities?.host ?? null);
  const isNative = hostMode === 'desktop';
  const micGainDb = useTxStore((s) => s.micGainDb);
  const err = useTxStore((s) => s.micError);
  const rootRef = useRef<HTMLDivElement | null>(null);

  // Shared ballistic — averages + RC-smooths the (raw + gain) signal and
  // holds the peak with the same physics as the analog dial and every
  // other meter in the app. Closes over `isNative` and `micGainDb` so a
  // gain-knob twist or a host-mode flip is picked up on the next tick.
  const reading = useBallisticReading(
    () => {
      const tx = useTxStore.getState();
      const raw = isNative
        ? useMicPeakStore.getState().peakDbfs
        : tx.micDbfs;
      return Math.min(MAX_DBFS, raw + tx.micGainDb);
    },
    { min: MIN_DBFS, max: MAX_DBFS },
    rootRef,
  );
  const effectiveDbfs = isFinite(reading.value) ? reading.value : MIN_DBFS;
  const fraction = dbfsToFraction(effectiveDbfs);
  const peak = isFinite(reading.peak)
    ? dbfsToFraction(reading.peak)
    : fraction;
  const clipping = effectiveDbfs >= CLIP_WARN_DBFS;

  if (err) {
    return (
      <div className="knob-group" title={err} style={{ minWidth: 140 }}>
        <span className="label-xs">MIC</span>
        <span className="chip tx">
          <span className="v">mic unavailable</span>
        </span>
      </div>
    );
  }

  const readoutLabel =
    effectiveDbfs <= MIN_DBFS ? '−∞' : `${effectiveDbfs.toFixed(0)} dBFS`;
  // Hint is a hover tooltip — sample raw dBFS one-shot from whichever
  // store the active transport writes to (no subscription, no extra
  // re-renders). Snapshot captured at React render time, which the
  // ballistic hook drives at ~30 Hz max.
  const rawSnapshot = isNative
    ? useMicPeakStore.getState().peakDbfs
    : useTxStore.getState().micDbfs;
  const hint =
    micGainDb !== 0
      ? `raw ${rawSnapshot.toFixed(0)} dBFS ${micGainDb > 0 ? '+' : '−'} ${Math.abs(micGainDb)} dB gain = ${effectiveDbfs.toFixed(0)} dBFS at ALC`
      : undefined;

  return (
    <div
      ref={rootRef}
      className="knob-group"
      style={{ minWidth: 180 }}
      role="meter"
      aria-label="Microphone level (pre-TX)"
      aria-valuemin={MIN_DBFS}
      aria-valuemax={MAX_DBFS}
      aria-valuenow={Math.round(effectiveDbfs)}
      title={hint}
    >
      <span className="label-xs">MIC</span>
      <div className="meter-bar" style={{ flex: 1, height: 8 }}>
        {/* Permanent red zone on the last 3 dB — always visible so peaks
            have a "target just below" reference, same as Thetis. */}
        <div
          aria-hidden
          style={{
            position: 'absolute',
            top: 0,
            bottom: 0,
            left: `${RED_ZONE_START * 100}%`,
            right: 0,
            background: 'rgba(255,64,64,0.14)',
          }}
        />
        <div
          className="meter-fill"
          style={{
            width: `${fraction * 100}%`,
            background: `${mercuryGradientCss()}, ${
              clipping
                ? 'linear-gradient(90deg, var(--power), var(--tx))'
                : 'linear-gradient(90deg, #2e7a2e, var(--accent))'
            }`,
          }}
        />
        {/* Peak-hold marker, matches .meter-peak style */}
        <div
          aria-hidden
          className="meter-peak"
          style={{ left: `${peak * 100}%` }}
        />
      </div>
      <span
        className="mono"
        style={{
          width: 60,
          textAlign: 'right',
          color: clipping ? 'var(--tx)' : 'var(--fg-1)',
          fontSize: 11,
          fontVariantNumeric: 'tabular-nums',
        }}
      >
        {readoutLabel}
      </span>
    </div>
  );
}
