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
import { setMicGain } from '../api/client';
import { useTxStore } from '../state/tx-store';
import { useLiveSlider } from '../hooks/useLiveSlider';

// Mic-gain range: −40..+10 dB, default 0 dB (= unity, no behaviour change
// for operators who never moved the slider). Matches Thetis's defaults at
// console.cs:19151 / :19163 (mic_gain_min = -40, mic_gain_max = 10) so the
// operator can attenuate a hot browser mic — getUserMedia routinely peaks
// −10 to −15 dBFS, which over-drives WDSP TXA + ALC and prints as splatter
// on the air. Server applies as SetTXAPanelGain1(10^(db/20)) — the same
// linear-dB curve Thetis runs through setAudioMicGain → Audio.MicPreamp
// (console.cs:28805-28815). Debounce matches DriveSlider so a drag doesn't
// flood the endpoint; optimistic store update keeps the thumb responsive.
//
// Always enabled: the TXA panel gain persists across MOX off/on, so the
// operator can dial in level against the live mic meter before keying.
const MIN = -40;
const MAX = 10;

export function MicGainSlider() {
  const micGainDb = useTxStore((s) => s.micGainDb);
  const setMicGainDb = useTxStore((s) => s.setMicGainDb);

  const previousOnError = useRef<number>(micGainDb);

  const liveSlider = useLiveSlider<number>({
    send: useCallback(
      (v: number, signal: AbortSignal) => {
        const prevValue = previousOnError.current;
        return setMicGain(v, signal)
          .then((r) => {
            if (signal.aborted) return;
            previousOnError.current = r.micGainDb;
            if (r.micGainDb !== v) setMicGainDb(r.micGainDb);
          })
          .catch((err) => {
            if (signal.aborted) return;
            if (err instanceof DOMException && err.name === 'AbortError') return;
            setMicGainDb(prevValue);
          });
      },
      [setMicGainDb],
    ),
  });

  // Rounded on send / display so the wire contract stays integer dB, but the
  // slider itself uses 0.5-step so micro-drags cross a step boundary on the
  // ~128px-wide input. At step=1 on a 20-dB range, each step is ~6px — drags
  // under that threshold didn't move the thumb and the user had to click to
  // commit, which looked like "drag doesn't work". Fractional store value is
  // fine; the round happens at render + wire time.
  const onInput = (e: React.FormEvent<HTMLInputElement>) => {
    const v = Number(e.currentTarget.value);
    setMicGainDb(v);
    liveSlider.push(Math.round(v));
  };

  return (
    <label className="knob-group">
      <span className="label-xs">MIC</span>
      <input
        type="range"
        min={MIN}
        max={MAX}
        step={0.5}
        value={micGainDb}
        onInput={onInput}
        onChange={onInput}
        onMouseUp={() => liveSlider.flush()}
        onTouchEnd={() => liveSlider.flush()}
        onKeyUp={() => liveSlider.flush()}
        style={{ flex: 1, cursor: 'pointer', accentColor: 'var(--accent)' }}
      />
      <span className="mono" style={{ width: 52, textAlign: 'right', color: 'var(--fg-1)', fontSize: 11 }}>
        {micGainDb > 0 ? '+' : ''}{Math.round(micGainDb)} dB
      </span>
    </label>
  );
}
