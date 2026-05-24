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

import { useCallback, useRef } from 'react';
import { setLevelerMaxGain } from '../api/client';
import { useTxStore } from '../state/tx-store';
import { useLiveSlider } from '../hooks/useLiveSlider';

// Leveler max-gain (TX): how much the WDSP TXA Leveler can boost quiet
// speech before the ALC catches it. Default +5 dB matches the W1AEX /
// softerhardware community starting point. Server clamp is [0, 15] dB
// (POST /api/tx/leveler-max-gain). Same debounce shape as DriveSlider /
// MicGainSlider so a drag doesn't flood the endpoint.
//
// Lives in the TX panel (TxFilterPanel) alongside DRV / TUN / MIC — moved
// out of the DSP panel because it only acts during MOX (TX-only stage).
const MIN = 0;
const MAX = 15;
const STEP = 0.5;

function quantize(v: number): number {
  const snapped = Math.round(v / STEP) * STEP;
  // JS float artefact guard — keep "5.0" displayed as "5.0".
  return Math.round(snapped * 10) / 10;
}

export function LevelerMaxGainSlider() {
  const value = useTxStore((s) => s.levelerMaxGainDb);
  const setValue = useTxStore((s) => s.setLevelerMaxGainDb);

  const previousOnError = useRef<number>(value);

  const liveSlider = useLiveSlider<number>({
    send: useCallback(
      (v: number, signal: AbortSignal) => {
        const prevValue = previousOnError.current;
        return setLevelerMaxGain(v, signal)
          .then((r) => {
            if (signal.aborted) return;
            previousOnError.current = r.levelerMaxGainDb;
            if (r.levelerMaxGainDb !== v) setValue(r.levelerMaxGainDb);
          })
          .catch((err) => {
            if (signal.aborted) return;
            if (err instanceof DOMException && err.name === 'AbortError') return;
            setValue(prevValue);
          });
      },
      [setValue],
    ),
  });

  const onInput = (e: React.FormEvent<HTMLInputElement>) => {
    const q = quantize(Number(e.currentTarget.value));
    setValue(q);
    liveSlider.push(q);
  };

  return (
    <label
      className="knob-group"
      title="Leveler Max Gain — how much the TXA Leveler can boost quiet speech before ALC catches it. +5 dB is the community-recommended SSB starting point; higher pushes ALC into harder limiting. TX-only."
    >
      <span className="label-xs">LVLR</span>
      <input
        type="range"
        min={MIN}
        max={MAX}
        step={STEP}
        value={value}
        onInput={onInput}
        onChange={onInput}
        onMouseUp={() => liveSlider.flush()}
        onTouchEnd={() => liveSlider.flush()}
        onKeyUp={() => liveSlider.flush()}
        style={{ flex: 1, cursor: 'pointer', accentColor: 'var(--accent)' }}
      />
      <span
        className="mono"
        style={{ width: 52, textAlign: 'right', color: 'var(--fg-1)', fontSize: 11 }}
      >
        +{value.toFixed(1)} dB
      </span>
    </label>
  );
}
