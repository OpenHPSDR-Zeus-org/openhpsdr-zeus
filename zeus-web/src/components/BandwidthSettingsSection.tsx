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

// Live DDC bandwidth (sample-rate) editor for the DSP settings tab. The rate is
// otherwise only chosen at connect time (ConnectPanel); this surfaces the full
// ladder as a live control so an operator can widen/narrow the spectrum without
// reconnecting. Mirrors Thetis's Setup ▸ General ▸ "Sample Rate" selector.
//
// The 768/1536 kHz rungs need a wide DDC transport: Protocol-1's control frame
// carries the rate in 2 bits (max 384 kHz), so they are gated to P2/P3 here and
// in the backend. Sends are optimistic + applyState-reconciled like every other
// section, so the toolbar/connect view and this control stay in sync.

import { useCallback, useEffect, useRef } from 'react';
import { setSampleRate, type SampleRate } from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { useRadioStore } from '../state/radio-store';

const RATES: readonly SampleRate[] = [
  48_000, 96_000, 192_000, 384_000, 768_000, 1_536_000,
];
const P1_MAX_SAMPLE_RATE = 384_000;

export function BandwidthSettingsSection() {
  const sampleRate = useConnectionStore((s) => s.sampleRate);
  const protocol = useConnectionStore((s) => s.connectedProtocol);
  const applyState = useConnectionStore((s) => s.applyState);
  const connected = useConnectionStore((s) => s.status === 'Connected');
  const boardMaxRateHz = useRadioStore((s) => s.capabilities.maxRxSampleRateHz);

  const abort = useRef<AbortController | null>(null);
  useEffect(() => () => abort.current?.abort(), []);

  const send = useCallback(
    (rate: SampleRate) => {
      abort.current?.abort();
      const ac = new AbortController();
      abort.current = ac;
      setSampleRate(rate, ac.signal)
        .then((s) => !ac.signal.aborted && applyState(s))
        .catch(() => {});
    },
    [applyState],
  );

  // 768/1536 are P2/P3-only and board-capability gated. Treat unknown protocol
  // (null, e.g. after reload) conservatively as the P1 cap so we never offer
  // a rung the radio rejects.
  const protocolMaxRateHz = protocol === 'P2' || protocol === 'P3'
    ? boardMaxRateHz
    : P1_MAX_SAMPLE_RATE;

  return (
    <div className="dsp-cfg">
      <div className="dsp-cfg-row">
        <span className="dsp-cfg-label">
          Sample Rate
          <span className="dsp-cfg-hint"> DDC spectrum width</span>
        </span>
        <div className="dsp-cfg-btns">
          {RATES.map((r) => {
            const protocolLocked = r > P1_MAX_SAMPLE_RATE && protocol !== 'P2' && protocol !== 'P3';
            const capabilityLocked = r > protocolMaxRateHz;
            const locked = protocolLocked || capabilityLocked;
            const isActive = sampleRate === r;
            return (
              <button
                key={r}
                type="button"
                disabled={!connected || locked}
                aria-pressed={isActive}
                onClick={() => !isActive && send(r)}
                className={`btn sm ${isActive ? 'active' : ''}`}
                title={
                  protocolLocked
                    ? `${r / 1000} kHz needs a Protocol-2 or Protocol-3 connection`
                    : capabilityLocked
                      ? `${r / 1000} kHz exceeds this radio's ${protocolMaxRateHz / 1000} kHz RX/DDC capability`
                    : `Set DDC bandwidth to ${r / 1000} kHz`
                }
              >
                {r / 1000}
              </button>
            );
          })}
        </div>
      </div>

      <div className="dsp-cfg-row">
        <span className="dsp-cfg-label">Active</span>
        <span className="dsp-cfg-hint" style={{ flex: 1 }}>
          {sampleRate / 1000} kHz
          {protocol != null ? ` · ${protocol}` : ''}
          {` · max ${protocolMaxRateHz / 1000} kHz`}
        </span>
      </div>
    </div>
  );
}
