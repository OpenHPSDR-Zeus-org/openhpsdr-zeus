// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// DIVERSITY combiner settings tab. Mirrors Thetis DiversityForm — two
// phase-synchronous ADC streams are summed with a complex weight
// (gain·e^{jθ}) before WDSP RX0 to null an interferer or peak a wanted
// signal. The backend DSP combine and the /api/rx/diversity endpoint are
// already live (DspPipelineService.ApplyDiversityConfig + RadioService.
// SetDiversity); this panel is the operator-facing control.
//
// Gated to live Protocol-2 dual-ADC radios (RxAdcCount ≥ 2 in the board
// capability fingerprint). On other boards the panel renders an
// explanatory empty state instead.

import { DIVERSITY_CONFIG_DEFAULT, setDiversity } from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { useRadioStore } from '../state/radio-store';

export function DiversityPanel() {
  const status = useConnectionStore((s) => s.status);
  const connectedProtocol = useConnectionStore((s) => s.connectedProtocol);
  const receivers = useConnectionStore((s) => s.receivers);
  const diversity = useConnectionStore((s) => s.diversity);
  const applyState = useConnectionStore((s) => s.applyState);
  const rxAdcCount = useRadioStore((s) => s.capabilities.rxAdcCount);

  const connected = status === 'Connected';
  const isP2 = connectedProtocol === 'P2';
  const hasDualAdc = rxAdcCount >= 2;

  // Effective config: server snapshot when present, else the off-default.
  const cfg = diversity ?? DIVERSITY_CONFIG_DEFAULT;

  // Source-receiver candidates are every exposed receiver except RX1 (the
  // reference / phase anchor). The combiner needs the source on a DIFFERENT
  // ADC than RX1; we surface the receiver's current ADC so the operator can
  // see whether the source is actually wired to ADC 1.
  const sourceOptions = receivers.filter((r) => r.enabled && r.index >= 1);

  function send(patch: { enabled?: boolean; gain?: number; phaseDeg?: number; sourceRx?: number }) {
    setDiversity(patch).then(applyState).catch(() => {});
  }

  const sourceRow = sourceOptions.find((r) => r.index === cfg.sourceRx) ?? sourceOptions[0];
  const sourceAdc = sourceRow?.adcSource;

  return (
    <div className="ps-shell">
      <div className="ps-card">
        <h4>
          <svg className="ps-ic-sm" viewBox="0 0 12 12">
            <path d="M1 3h10M1 6h10M1 9h10" />
          </svg>
          DIVERSITY COMBINER
          <span className="ps-card-hint">null an interferer on two antennas</span>
        </h4>

        {!connected ? (
          <div className="ps-field">
            <div className="ps-name">
              Not connected
              <em>Connect a radio to configure the diversity combiner.</em>
            </div>
          </div>
        ) : !isP2 ? (
          <div className="ps-field">
            <div className="ps-name">
              Protocol 2 only
              <em>
                Diversity needs two phase-synchronous ADCs; only a Protocol-2
                radio (ANAN G2 / Saturn class) exposes those. RX1/RX2 stay
                available on this radio, but the combiner is disabled.
              </em>
            </div>
          </div>
        ) : !hasDualAdc ? (
          <div className="ps-field">
            <div className="ps-name">
              Single-ADC board
              <em>
                The combiner adds RX1's ADC stream to a second receiver tuned
                to a different ADC; this radio reports a single ADC, so there's
                nothing to combine against.
              </em>
            </div>
          </div>
        ) : (
          <>
            <div className="ps-field">
              <div className="ps-name">
                How this works
                <em>
                  Plug your main antenna into RX1's ADC (ADC 0) and a second
                  antenna into the back-panel RX2/EXT jack (ADC 1). Enable a
                  second receiver in the RECEIVERS panel and assign it to ADC
                  1, then turn DIVERSITY on. Adjust <strong>Phase</strong> first
                  to null the interferer (signal will dip sharply), then trim
                  <strong> Gain</strong> for the deepest null. Wanted signals
                  on a different bearing should stay audible. Off = byte-
                  identical to normal RX. Re-arm each session (the combiner
                  never auto-engages after a restart).
                </em>
              </div>
            </div>

            <div className="ps-field">
              <div className="ps-name">
                Enable
                <em>Turn the combiner on once both antennas are wired and a source receiver is on ADC 1.</em>
              </div>
              <button
                type="button"
                aria-pressed={cfg.enabled}
                className={`btn sm ${cfg.enabled ? 'active' : ''}`}
                onClick={() => send({ enabled: !cfg.enabled })}
              >
                {cfg.enabled ? 'ON' : 'OFF'}
              </button>
            </div>

            <div className="ps-field">
              <div className="ps-name">
                Source receiver
                <em>
                  Which receiver feeds the second ADC's stream into the
                  combine. Only receivers that are already enabled appear here
                  — open the RECEIVERS panel first if your list is empty.
                  {sourceRow && sourceAdc === 0 ? (
                    <> <strong>Warning:</strong> RX{sourceRow.index + 1} is on
                    ADC 0 (same antenna as RX1) — the combine will just
                    reinforce/cancel the same signal. Move it to ADC 1 in
                    the RECEIVERS panel.</>
                  ) : null}
                </em>
              </div>
              {sourceOptions.length === 0 ? (
                <em style={{ color: 'var(--fg-3)', fontSize: '11px' }}>no RX2+ active</em>
              ) : (
                <select
                  className="ps-select-mini"
                  value={cfg.sourceRx}
                  aria-label="Diversity source receiver"
                  onChange={(e) => send({ sourceRx: Number(e.target.value) })}
                >
                  {sourceOptions.map((r) => (
                    <option key={r.index} value={r.index}>
                      {`RX${r.index + 1} · ADC ${r.adcSource}`}
                    </option>
                  ))}
                </select>
              )}
            </div>

            <div className="ps-field">
              <div className="ps-name">
                Gain
                <em>Magnitude of the source stream (0..2, 1.0 = unity). Trim after phase to deepen the null.</em>
              </div>
              <label style={{ display: 'inline-flex', alignItems: 'center', gap: 8 }}>
                <input
                  type="range"
                  min={0}
                  max={2}
                  step={0.01}
                  value={cfg.gain}
                  aria-label="Diversity gain"
                  onChange={(e) => send({ gain: Number(e.currentTarget.value) })}
                  style={{ width: 140, accentColor: 'var(--accent)' }}
                />
                <span className="mono" style={{ minWidth: 42, textAlign: 'right' }}>
                  {cfg.gain.toFixed(2)}
                </span>
              </label>
            </div>

            <div className="ps-field">
              <div className="ps-name">
                Phase
                <em>Rotation in degrees (−180..+180). Sweep slowly to find the dip on the unwanted signal.</em>
              </div>
              <label style={{ display: 'inline-flex', alignItems: 'center', gap: 8 }}>
                <input
                  type="range"
                  min={-180}
                  max={180}
                  step={1}
                  value={cfg.phaseDeg}
                  aria-label="Diversity phase"
                  onChange={(e) => send({ phaseDeg: Number(e.currentTarget.value) })}
                  style={{ width: 140, accentColor: 'var(--accent)' }}
                />
                <span className="mono" style={{ minWidth: 50, textAlign: 'right' }}>
                  {Math.round(cfg.phaseDeg)}°
                </span>
              </label>
            </div>

            <div className="ps-field">
              <div className="ps-name">
                Reset
                <em>Restore gain to 1.00 and phase to 0° without disabling the combiner.</em>
              </div>
              <button
                type="button"
                className="btn sm"
                onClick={() => send({ gain: 1.0, phaseDeg: 0 })}
              >
                RESET
              </button>
            </div>
          </>
        )}
      </div>
    </div>
  );
}
