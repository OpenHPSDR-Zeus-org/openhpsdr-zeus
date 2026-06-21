// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// RADIO SETTINGS tab. First (and currently only) control: the hardware
// PTT-IN → MOX opt-in gate, with a live PTT-IN status lamp.
//
// Every board exposes a PTT-IN line (P1 boards via C0[0] / ptt_resp, P2 boards
// via the UDP-1025 hi-priority PttIn bit), so this card is ungated by board.
// The `keyed` lamp is driven live by the PttStatusFrame WS edge regardless of
// the enable toggle; the toggle only controls whether a footswitch press is
// promoted to host MOX. Defaults OFF (opt-in) server-side.
//
// Visual idiom reuses PsSettingsPanel's `.ps-shell` / `.ps-card` / `.ps-field`
// surfaces (tokens only, no new chrome / palette). Layout / visual specifics
// are the maintainer's call — this stays clean and minimal.

import { useEffect } from 'react';
import { usePttStore } from '../state/ptt-store';
import {
  useAntennaStore,
  type AntennaName,
  type RxAuxName,
} from '../state/antenna-store';

const ANTENNA_OPTIONS: AntennaName[] = ['Ant1', 'Ant2', 'Ant3'];

export function RadioSettingsPanel() {
  const pttKeyed = usePttStore((s) => s.keyed);
  const pttEnabled = usePttStore((s) => s.enabled);
  const pttHangMs = usePttStore((s) => s.hangMs);
  const pttInflight = usePttStore((s) => s.inflight);
  const loadPtt = usePttStore((s) => s.load);
  const setPttEnabled = usePttStore((s) => s.setEnabled);

  const antSettings = useAntennaStore((s) => s.settings);
  const antInflight = useAntennaStore((s) => s.inflight);
  const loadAntenna = useAntennaStore((s) => s.load);
  const setAntennaBand = useAntennaStore((s) => s.setBand);

  useEffect(() => {
    void loadPtt();
    void loadAntenna();
  }, [loadPtt, loadAntenna]);

  // The antenna card renders only when the connected board exposes at least one
  // antenna control — otherwise (HL2's single jack) there's nothing to set.
  const showAntenna =
    antSettings.hasTxAntennaRelays ||
    antSettings.hasRxAntennaRelays ||
    antSettings.availableRxAux.length > 0;
  // Order the bands by the server's HF list as returned.
  const bands = antSettings.bands;
  const rxAuxOptions: RxAuxName[] = ['None', ...antSettings.availableRxAux];

  return (
    <div className="ps-shell">
      <div className="ps-card">
        <h4>
          <svg className="ps-ic-sm" viewBox="0 0 12 12">
            <path d="M6 1v4M3.5 5h5v3a2.5 2.5 0 0 1-5 0z" />
          </svg>
          PTT-IN
          <span className="ps-card-hint">footswitch / mic-PTT / rear KEY</span>
        </h4>

        <div className="ps-field">
          <div className="ps-name">
            Status
            <em>
              Live hardware PTT-IN level. Read-only — the radio drives this when
              you press the footswitch / mic PTT (or ground the rear KEY).
            </em>
          </div>
          <div style={{ display: 'flex', alignItems: 'center', gap: '0.5rem' }}>
            <span
              aria-hidden
              style={{
                width: '0.6rem',
                height: '0.6rem',
                borderRadius: '50%',
                background: pttKeyed ? 'var(--tx)' : 'var(--fg-3)',
                boxShadow: pttKeyed ? '0 0 6px var(--tx-soft)' : 'none',
                transition: 'background 60ms linear',
              }}
            />
            <span style={{ color: pttKeyed ? 'var(--tx)' : 'var(--fg-2)' }}>
              {pttKeyed ? 'KEYED' : 'idle'}
            </span>
          </div>
        </div>

        <div className="ps-field">
          <div className="ps-name">
            Enable
            <em>
              When off, the footswitch is ignored for keying (UI-only TX). The
              lamp above still shows the physical input.
            </em>
          </div>
          <label className="ps-check">
            <input
              type="checkbox"
              checked={pttEnabled}
              disabled={pttInflight}
              onChange={(e) => void setPttEnabled(e.target.checked)}
            />
            <span className="ps-check-box" />
            <span>Hardware PTT → MOX</span>
          </label>
        </div>

        <div className="ps-field">
          <div className="ps-name">
            Hang
            <em>Release hang time — bridges CW inter-character gaps. Fixed for now.</em>
          </div>
          <span style={{ color: 'var(--fg-2)' }}>{pttHangMs} ms</span>
        </div>
      </div>

      {showAntenna && (
        <div className="ps-card">
          <h4>
            <svg className="ps-ic-sm" viewBox="0 0 12 12">
              <path d="M6 1.5v9M3 3.5l3-2 3 2M2 6.5l4 4 4-4" fill="none" />
            </svg>
            Antenna
            <span className="ps-card-hint">per-band TX / RX relay + RX-aux</span>
          </h4>

          {bands.map((b) => (
            <div className="ps-field" key={b.band}>
              <div className="ps-name">
                {b.band}
                <em>
                  TX / RX antenna relay and auxiliary RX input for this band.
                </em>
              </div>
              <div style={{ display: 'flex', alignItems: 'center', gap: '0.5rem' }}>
                {antSettings.hasTxAntennaRelays && (
                  <label style={{ display: 'flex', alignItems: 'center', gap: '0.25rem' }}>
                    <span style={{ color: 'var(--fg-2)', fontSize: '0.8em' }}>TX</span>
                    <select
                      value={b.txAnt}
                      disabled={antInflight}
                      onChange={(e) =>
                        void setAntennaBand(
                          b.band,
                          e.target.value as AntennaName,
                          b.rxAnt,
                          b.rxAux,
                        )
                      }
                    >
                      {ANTENNA_OPTIONS.map((a) => (
                        <option key={a} value={a}>
                          {a}
                        </option>
                      ))}
                    </select>
                  </label>
                )}
                {antSettings.hasRxAntennaRelays && (
                  <label style={{ display: 'flex', alignItems: 'center', gap: '0.25rem' }}>
                    <span style={{ color: 'var(--fg-2)', fontSize: '0.8em' }}>RX</span>
                    <select
                      value={b.rxAnt}
                      disabled={antInflight}
                      onChange={(e) =>
                        void setAntennaBand(
                          b.band,
                          b.txAnt,
                          e.target.value as AntennaName,
                          b.rxAux,
                        )
                      }
                    >
                      {ANTENNA_OPTIONS.map((a) => (
                        <option key={a} value={a}>
                          {a}
                        </option>
                      ))}
                    </select>
                  </label>
                )}
                {antSettings.availableRxAux.length > 0 && (
                  <label style={{ display: 'flex', alignItems: 'center', gap: '0.25rem' }}>
                    <span style={{ color: 'var(--fg-2)', fontSize: '0.8em' }}>AUX</span>
                    <select
                      value={b.rxAux}
                      disabled={antInflight}
                      onChange={(e) =>
                        void setAntennaBand(
                          b.band,
                          b.txAnt,
                          b.rxAnt,
                          e.target.value as RxAuxName,
                        )
                      }
                    >
                      {rxAuxOptions.map((a) => (
                        <option key={a} value={a}>
                          {a}
                        </option>
                      ))}
                    </select>
                  </label>
                )}
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
