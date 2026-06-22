// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// RECEIVERS (multi-DDC) settings tab. Lets the operator choose how many
// hardware DDC receivers to expose and which phase-synchronous ADC each one
// reads — the front end for the B4-server N-receiver path (POST
// /api/receivers/{index}, StateDto.Receivers[]). Multi-DDC is a Protocol-2
// feature (ANAN G2 / Saturn class); the panel gates on a live P2 connection.
//
// Receivers are contiguous (the radio's DDC run has no gaps): exposing N means
// RX1..RXN are active. The "exposed" button row composes the per-index calls;
// per-receiver rows carry the ADC0/ADC1 selector. Visual idiom reuses the
// shared `.ps-shell` / `.ps-card` / `.ps-field` surfaces (tokens only).

import { setReceiver } from '../api/client';
import { useConnectionStore } from '../state/connection-store';

function formatMhz(hz: number): string {
  return `${(hz / 1_000_000).toFixed(6)} MHz`;
}

export function ReceiversPanel() {
  const receivers = useConnectionStore((s) => s.receivers);
  const maxReceivers = useConnectionStore((s) => s.maxReceivers);
  const status = useConnectionStore((s) => s.status);
  const connectedProtocol = useConnectionStore((s) => s.connectedProtocol);
  const applyState = useConnectionStore((s) => s.applyState);

  const connected = status === 'Connected';
  const isP2 = connectedProtocol === 'P2';

  // Number of contiguous active receivers (RX1 always counts).
  const exposedCount = Math.max(1, receivers.filter((r) => r.enabled).length);

  // Set the exposed receiver count by composing the per-index endpoint. Server
  // contiguity does the cascade: enabling index target-1 turns on RX2..target-1;
  // disabling index target turns off everything above it.
  function setExposedCount(target: number): void {
    const n = Math.min(Math.max(target, 1), maxReceivers);
    if (n === exposedCount) return;
    if (n <= 1) {
      setReceiver(1, { enabled: false }).then(applyState).catch(() => {});
      return;
    }
    setReceiver(n - 1, { enabled: true })
      .then((s) => {
        applyState(s);
        if (n < maxReceivers) {
          return setReceiver(n, { enabled: false }).then(applyState);
        }
        return undefined;
      })
      .catch(() => {});
  }

  function setAdc(index: number, adcSource: number): void {
    setReceiver(index, { adcSource }).then(applyState).catch(() => {});
  }

  const countOptions = Array.from({ length: maxReceivers }, (_, i) => i + 1);

  return (
    <div className="ps-shell">
      <div className="ps-card">
        <h4>
          <svg className="ps-ic-sm" viewBox="0 0 12 12">
            <path d="M1 6h2M9 6h2M6 1v2M6 9v2M3.5 3.5l1.2 1.2M7.3 7.3l1.2 1.2M8.5 3.5L7.3 4.7M4.7 7.3L3.5 8.5" />
          </svg>
          RECEIVERS (DDC)
          <span className="ps-card-hint">independent hardware DDCs across the dual ADCs</span>
        </h4>

        {!connected ? (
          <div className="ps-field">
            <div className="ps-name">
              Not connected
              <em>Connect a radio to configure its DDC receivers.</em>
            </div>
          </div>
        ) : !isP2 ? (
          <div className="ps-field">
            <div className="ps-name">
              Protocol 2 only
              <em>
                Multiple independent DDC receivers require a Protocol-2 radio
                (ANAN G2 / Saturn class). RX1/RX2 remain available on this radio.
              </em>
            </div>
          </div>
        ) : (
          <>
            <div className="ps-field">
              <div className="ps-name">
                Exposed receivers
                <em>
                  How many hardware DDC receivers to run concurrently. RX1..RXN
                  are activated together (contiguous DDC run); each tunes to its
                  own VFO. Up to {maxReceivers}.
                </em>
              </div>
              <div className="btn-row wrap" role="group" aria-label="Exposed receiver count">
                {countOptions.map((n) => (
                  <button
                    key={n}
                    type="button"
                    className={`btn sm ${n === exposedCount ? 'active' : ''}`}
                    onClick={() => setExposedCount(n)}
                  >
                    {n}
                  </button>
                ))}
              </div>
            </div>

            {receivers.map((r) => (
              <div className="ps-field" key={r.index}>
                <div className="ps-name">
                  {`RX${r.index + 1}`}
                  <em>
                    {`${r.mode} · ${formatMhz(r.vfoHz)}`}
                    {r.index === 0 ? ' · clock master' : ''}
                  </em>
                </div>
                <select
                  className="ps-select-mini"
                  value={r.adcSource}
                  aria-label={`RX${r.index + 1} ADC source`}
                  onChange={(e) => setAdc(r.index, Number(e.target.value))}
                >
                  <option value={0}>ADC 0</option>
                  <option value={1}>ADC 1</option>
                </select>
              </div>
            ))}
          </>
        )}
      </div>
    </div>
  );
}
