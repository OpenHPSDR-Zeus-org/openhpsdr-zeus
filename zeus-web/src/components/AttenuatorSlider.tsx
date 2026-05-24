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

import { useCallback, useEffect, useRef, useState } from 'react';
import { setAttenuator, setAutoAtt } from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { useLiveSlider } from '../hooks/useLiveSlider';

// HpsdrAtten range — the server clamps to [MIN, MAX], but pinning the UI
// to the same bounds avoids a round-trip that would visually snap the thumb.
const MIN = 0;
const MAX = 31;

export function AttenuatorSlider() {
  const userAtten = useConnectionStore((s) => s.attenDb);
  const offsetDb = useConnectionStore((s) => s.attOffsetDb);
  const autoEnabled = useConnectionStore((s) => s.autoAttEnabled);
  const overload = useConnectionStore((s) => s.adcOverloadWarning);
  const connected = useConnectionStore((s) => s.status === 'Connected');
  const applyState = useConnectionStore((s) => s.applyState);

  const [dragValue, setDragValue] = useState<number | null>(null);
  // Slider thumb edits the user baseline (attenDb); the displayed number shows
  // the effective atten on the hardware so the user can watch the auto ramp.
  const sliderValue = dragValue ?? userAtten;
  const effective = Math.min(MAX, sliderValue + offsetDb);

  const autoAbort = useRef<AbortController | null>(null);

  // Stream during drag (rAF coalesced), flush on release — see useLiveSlider
  // notes in hooks/useLiveSlider.ts. Previously the wire POST only fired on
  // mouseUp / touchEnd / keyUp so the attenuator didn't change audibly until
  // release; now it tracks the thumb in real time.
  const liveSlider = useLiveSlider<number>({
    send: useCallback(
      (v: number, signal: AbortSignal) =>
        setAttenuator(v, signal)
          .then((next) => {
            if (!signal.aborted) applyState(next);
          })
          .catch(() => {
            /* next poll will reconcile; don't noisily log on abort */
          }),
      [applyState],
    ),
  });

  const toggleAuto = useCallback(() => {
    if (!connected) return;
    autoAbort.current?.abort();
    const ac = new AbortController();
    autoAbort.current = ac;
    setAutoAtt(!autoEnabled, ac.signal)
      .then((next) => {
        if (!ac.signal.aborted) applyState(next);
      })
      .catch(() => {
        /* state subscription will reconcile on next broadcast */
      });
  }, [autoEnabled, connected, applyState]);

  useEffect(
    () => () => {
      autoAbort.current?.abort();
    },
    [],
  );

  return (
    <label className="knob-group">
      <button
        type="button"
        onClick={toggleAuto}
        disabled={!connected}
        aria-pressed={autoEnabled}
        aria-label={autoEnabled ? 'Auto attenuator on' : 'Auto attenuator off'}
        title={
          autoEnabled
            ? 'Auto-ATT ON (click to disable)'
            : 'Auto-ATT OFF (click to enable)'
        }
        className={`btn sm ${autoEnabled ? 'active' : ''} ${overload ? 'overload' : ''}`}
      >
        {/* Issue #126 — Dfinitski / HPSDR convention: this control is the
            Step Attenuator (S-ATT). The button toggles the auto-att overlay
            on top of it; "active" CSS class conveys that auto state without
            renaming the control itself. Previously read "A-ATT" while auto
            was on, which was non-standard nomenclature. */}
        S-ATT
      </button>
      <input
        type="range"
        min={MIN}
        max={MAX}
        step={1}
        value={sliderValue}
        disabled={!connected || autoEnabled}
        onChange={(e) => {
          const v = Number(e.currentTarget.value);
          setDragValue(v);
          liveSlider.push(v);
        }}
        onMouseUp={() => {
          liveSlider.flush();
          setDragValue(null);
        }}
        onTouchEnd={() => {
          liveSlider.flush();
          setDragValue(null);
        }}
        onKeyUp={() => {
          liveSlider.flush();
          setDragValue(null);
        }}
        style={{ flex: 1, cursor: 'pointer', accentColor: 'var(--accent)' }}
      />
      <span className="mono" style={{ width: 48, textAlign: 'right', color: overload ? 'var(--power)' : 'var(--fg-1)', fontSize: 11 }}>
        {effective} dB
      </span>
    </label>
  );
}
