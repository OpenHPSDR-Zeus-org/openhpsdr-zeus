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

export function RadioSettingsPanel() {
  const pttKeyed = usePttStore((s) => s.keyed);
  const pttEnabled = usePttStore((s) => s.enabled);
  const pttHangMs = usePttStore((s) => s.hangMs);
  const pttInflight = usePttStore((s) => s.inflight);
  const loadPtt = usePttStore((s) => s.load);
  const setPttEnabled = usePttStore((s) => s.setEnabled);

  useEffect(() => {
    void loadPtt();
  }, [loadPtt]);

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
    </div>
  );
}
