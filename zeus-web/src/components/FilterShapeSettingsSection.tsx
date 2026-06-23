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
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

// SSB bandpass "rectangularity" — issue #871. Independent RX/TX selectors set
// the WDSP FIR tap count (SetRX/TXABandpassNC), which is what actually changes
// the audible shoulder/skirt steepness: Soft = fewest taps (wide transition,
// Yaesu-like flat), Normal = today's default (no change vs prior builds), Sharp
// = most taps (narrow transition, Icom-like rectangular). Same dsp-cfg row idiom
// as BandwidthSettingsSection so this reads as another DSP-tab control family.

import { useCallback, useEffect, useRef } from 'react';
import {
  setRxFilterWindow,
  setTxFilterWindow,
  type BandpassWindow,
} from '../api/client';
import { useConnectionStore } from '../state/connection-store';

const SHAPES: ReadonlyArray<{ value: BandpassWindow; label: string; hint: string }> = [
  { value: 'Soft', label: 'Soft', hint: 'Gentle, rounded shoulder — Yaesu-like' },
  { value: 'Normal', label: 'Normal', hint: 'Default shoulder' },
  { value: 'Sharp', label: 'Sharp', hint: 'Steep, rectangular shoulder — Icom-like' },
];

export function FilterShapeSettingsSection() {
  const rxWindow = useConnectionStore((s) => s.rxFilterWindow);
  const txWindow = useConnectionStore((s) => s.txFilterWindow);
  const applyState = useConnectionStore((s) => s.applyState);
  const connected = useConnectionStore((s) => s.status === 'Connected');

  const rxAbort = useRef<AbortController | null>(null);
  const txAbort = useRef<AbortController | null>(null);
  useEffect(() => () => {
    rxAbort.current?.abort();
    txAbort.current?.abort();
  }, []);

  const sendRx = useCallback(
    (window: BandpassWindow) => {
      if (window === rxWindow) return;
      rxAbort.current?.abort();
      const ac = new AbortController();
      rxAbort.current = ac;
      useConnectionStore.setState({ rxFilterWindow: window });
      setRxFilterWindow(window, ac.signal)
        .then((s) => !ac.signal.aborted && applyState(s))
        .catch(() => {});
    },
    [rxWindow, applyState],
  );

  const sendTx = useCallback(
    (window: BandpassWindow) => {
      if (window === txWindow) return;
      txAbort.current?.abort();
      const ac = new AbortController();
      txAbort.current = ac;
      useConnectionStore.setState({ txFilterWindow: window });
      setTxFilterWindow(window, ac.signal)
        .then((s) => !ac.signal.aborted && applyState(s))
        .catch(() => {});
    },
    [txWindow, applyState],
  );

  return (
    <div className="dsp-cfg">
      <div className="dsp-cfg-row">
        <span className="dsp-cfg-label">
          RX
          <span className="dsp-cfg-hint"> receive bandpass shape</span>
        </span>
        <div className="dsp-cfg-btns" role="group" aria-label="RX filter shape">
          {SHAPES.map((s) => (
            <button
              key={s.value}
              type="button"
              disabled={!connected}
              aria-pressed={rxWindow === s.value}
              onClick={() => sendRx(s.value)}
              className={`btn sm ${rxWindow === s.value ? 'active' : ''}`}
              title={s.hint}
            >
              {s.label}
            </button>
          ))}
        </div>
      </div>

      <div className="dsp-cfg-row">
        <span className="dsp-cfg-label">
          TX
          <span className="dsp-cfg-hint"> transmit bandpass shape</span>
        </span>
        <div className="dsp-cfg-btns" role="group" aria-label="TX filter shape">
          {SHAPES.map((s) => (
            <button
              key={s.value}
              type="button"
              disabled={!connected}
              aria-pressed={txWindow === s.value}
              onClick={() => sendTx(s.value)}
              className={`btn sm ${txWindow === s.value ? 'active' : ''}`}
              title={s.hint}
            >
              {s.label}
            </button>
          ))}
        </div>
      </div>
    </div>
  );
}
