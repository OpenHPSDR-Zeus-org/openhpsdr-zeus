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

export function RadioOptionsPanel() {
  const options = useRadioOptionsStore((s) => s.options);
  const loaded = useRadioOptionsStore((s) => s.loaded);
  const inflight = useRadioOptionsStore((s) => s.inflight);
  const error = useRadioOptionsStore((s) => s.error);
  const load = useRadioOptionsStore((s) => s.load);
  const setBandVolts = useRadioOptionsStore((s) => s.setBandVolts);

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
