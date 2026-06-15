// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// RADIO settings tab — HL2-only optional toggles. Wraps a small set of
// firmware feature flags the operator can flip without rebooting the
// radio. Currently one toggle: Band Volts PWM output (issue #279).
//
// Visual idiom borrowed from PsSettingsPanel's `.ps-card` / `.ps-field`
// / `.ps-check` so this panel reads as the same surface family as the
// other settings tabs (no new chrome introduced).

import { useEffect } from 'react';
import { useRadioOptionsStore } from '../state/radio-options-store';
import { useRadioStore } from '../state/radio-store';

export function RadioOptionsPanel() {
  const options = useRadioOptionsStore((s) => s.options);
  const g2Options = useRadioOptionsStore((s) => s.g2Options);
  const loaded = useRadioOptionsStore((s) => s.loaded);
  const inflight = useRadioOptionsStore((s) => s.inflight);
  const error = useRadioOptionsStore((s) => s.error);
  const load = useRadioOptionsStore((s) => s.load);
  const setBandVolts = useRadioOptionsStore((s) => s.setBandVolts);
  const setG2Dither = useRadioOptionsStore((s) => s.setG2Dither);
  const setG2Random = useRadioOptionsStore((s) => s.setG2Random);
  const hasHl2OptionalToggles = useRadioStore(
    (s) => s.capabilities.hasHl2OptionalToggles,
  );
  const supportsG2AdcOptions = useRadioStore(
    (s) => s.capabilities.supportsG2AdcOptions,
  );

  useEffect(() => {
    load();
  }, [load]);

  const statusText = inflight
    ? 'Saving…'
    : loaded
      ? 'Loaded from server — changes apply immediately'
      : 'Loading…';

  return (
    <div className="ps-shell">
      {hasHl2OptionalToggles ? (
        <div className="ps-card">
          <h4>
            <svg className="ps-ic-sm" viewBox="0 0 12 12">
              <rect x="2" y="4" width="8" height="4" rx="1" />
              <path d="M4 4V2M8 4V2M4 10V8M8 10V8" />
            </svg>
            Hermes Lite 2 Options
            <span className="ps-card-hint">firmware features — HL2 only</span>
          </h4>

          <div className="ps-field">
            <div className="ps-name">
              Band Volts
              <em>
                Enable Band Volts PWM output (replaces fan-control PWM). Lets
                external amps such as the Xiegu XPA125B follow band changes
                from Zeus. HL2 firmware feature — see
                hermes-lite2-protocol.md address 0x00 bit 11.
              </em>
            </div>
            <label className="ps-check">
              <input
                type="checkbox"
                checked={options.bandVolts}
                disabled={inflight}
                onChange={(e) => {
                  setBandVolts(e.target.checked);
                }}
              />
              <span className="ps-check-box" />
              <span>{options.bandVolts ? 'Enabled' : 'Disabled'}</span>
            </label>
          </div>
        </div>
      ) : null}

      {supportsG2AdcOptions ? (
        <div className="ps-card">
          <h4>
            <svg className="ps-ic-sm" viewBox="0 0 12 12">
              <path d="M2 8h8M2 4h8M4 2v8M8 2v8" />
              <rect x="2" y="2" width="8" height="8" rx="1" />
            </svg>
            ANAN-G2 Options
            <span className="ps-card-hint">ADC linearity and decorrelation</span>
          </h4>

          <div className="ps-field">
            <div className="ps-name">
              Dither Enabled
              <em>
                Enables the Protocol-2 CmdRx ADC dither mask used by Thetis to
                address ADC nonlinearity errors. Applies live on verified
                G2-class radios and persists for reconnect.
              </em>
            </div>
            <label className="ps-check">
              <input
                type="checkbox"
                checked={g2Options.ditherEnabled}
                disabled={inflight}
                onChange={(e) => {
                  setG2Dither(e.target.checked);
                }}
              />
              <span className="ps-check-box" />
              <span>{g2Options.ditherEnabled ? 'Enabled' : 'Disabled'}</span>
            </label>
          </div>

          <div className="ps-field">
            <div className="ps-name">
              Random Enabled
              <em>
                Enables the Protocol-2 CmdRx ADC digital-output randomizer
                mask used by Thetis to reduce digital feedback artifacts.
              </em>
            </div>
            <label className="ps-check">
              <input
                type="checkbox"
                checked={g2Options.randomEnabled}
                disabled={inflight}
                onChange={(e) => {
                  setG2Random(e.target.checked);
                }}
              />
              <span className="ps-check-box" />
              <span>{g2Options.randomEnabled ? 'Enabled' : 'Disabled'}</span>
            </label>
          </div>

          <div className="ps-field">
            <div className="ps-name">
              MaxRXFreq
              <em>
                Zeus enforces the G2 0-60 MHz receive ceiling through the
                VFO and radio-LO clamp, so this is shown as a live parity
                value instead of a duplicate setting.
              </em>
            </div>
            <span className="mono">{g2Options.maxRxFreqMHz.toFixed(2)} MHz</span>
          </div>
        </div>
      ) : null}

      <div className="ps-status-row">
        <div className="ps-status-left">
          <span>Status</span>
          <span className={inflight ? '' : 'saved'}>{statusText}</span>
        </div>
        {error ? (
          <div className="ps-status-left" style={{ color: 'var(--tx)' }}>
            <span>Error</span>
            <span>{error}</span>
          </div>
        ) : null}
      </div>
    </div>
  );
}
