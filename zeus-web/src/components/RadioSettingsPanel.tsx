// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// RADIO SETTINGS tab — external-port controls (external-ports plan, Phase 2).
// First control: per-band TX / RX antenna relay selection for the CURRENT
// band, board-gated. The TX selector renders only when the board has TX
// antenna relays (0x0A / Saturn family); the RX selector only when it has RX
// antenna relays (every ANAN board; HL2's single jack is clamped to ANT1 on
// the wire, so its RX selector is hidden). Persistence is per band and
// server-authoritative — the panel loads + PUTs; the backend pushes the active
// band to the live radio.
//
// Visual idiom reuses PsSettingsPanel's `.ps-shell` / `.ps-card` / `.ps-field`
// surfaces and the `.ps-select-mini` select, so no new chrome / palette is
// introduced (tokens only). Layout / visual specifics are the maintainer's
// call — this stays clean and minimal.

import { useEffect } from 'react';
import { useConnectionStore } from '../state/connection-store';
import { useRadioStore } from '../state/radio-store';
import {
  useAntennaStore,
  type AntennaName,
} from '../state/antenna-store';
import { bandOf } from './design/data';

const ANTENNAS: AntennaName[] = ['Ant1', 'Ant2', 'Ant3'];
const ANTENNA_LABEL: Record<AntennaName, string> = {
  Ant1: 'ANT 1',
  Ant2: 'ANT 2',
  Ant3: 'ANT 3',
};

export function RadioSettingsPanel() {
  const vfoHz = useConnectionStore((s) => s.vfoHz);
  const caps = useRadioStore((s) => s.capabilities);
  const settings = useAntennaStore((s) => s.settings);
  const loaded = useAntennaStore((s) => s.loaded);
  const inflight = useAntennaStore((s) => s.inflight);
  const error = useAntennaStore((s) => s.error);
  const load = useAntennaStore((s) => s.load);
  const setBand = useAntennaStore((s) => s.setBand);

  useEffect(() => {
    load();
  }, [load]);

  const band = bandOf(vfoHz);
  const onBand = band !== '—';
  const current = settings.bands.find((b) => b.band === band);
  const txAnt: AntennaName = current?.txAnt ?? 'Ant1';
  const rxAnt: AntennaName = current?.rxAnt ?? 'Ant1';

  const onSetTx = (next: AntennaName) => {
    if (!onBand) return;
    void setBand(band, next, rxAnt);
  };
  const onSetRx = (next: AntennaName) => {
    if (!onBand) return;
    void setBand(band, txAnt, next);
  };

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
            <path d="M6 1v7M3 4l3-3 3 3M3 10h6" />
          </svg>
          Antenna
          <span className="ps-card-hint">
            per band — {onBand ? band : 'no HF band'}
          </span>
        </h4>

        {!caps.hasTxAntennaRelays && !caps.hasRxAntennaRelays ? (
          <div className="ps-field">
            <div className="ps-name">
              No switchable antennas
              <em>
                The connected radio is hardwired to ANT 1 (no TX or RX antenna
                relays). Nothing to select here.
              </em>
            </div>
          </div>
        ) : null}

        {caps.hasTxAntennaRelays ? (
          <div className="ps-field">
            <div className="ps-name">
              TX Antenna
              <em>Transmit antenna relay for {onBand ? band : 'the current band'}.</em>
            </div>
            <select
              className="ps-select-mini"
              value={txAnt}
              disabled={!onBand || inflight}
              onChange={(e) => onSetTx(e.target.value as AntennaName)}
            >
              {ANTENNAS.map((a) => (
                <option key={a} value={a}>
                  {ANTENNA_LABEL[a]}
                </option>
              ))}
            </select>
          </div>
        ) : null}

        {caps.hasRxAntennaRelays ? (
          <div className="ps-field">
            <div className="ps-name">
              RX Antenna
              <em>Receive antenna relay for {onBand ? band : 'the current band'}.</em>
            </div>
            <select
              className="ps-select-mini"
              value={rxAnt}
              disabled={!onBand || inflight}
              onChange={(e) => onSetRx(e.target.value as AntennaName)}
            >
              {ANTENNAS.map((a) => (
                <option key={a} value={a}>
                  {ANTENNA_LABEL[a]}
                </option>
              ))}
            </select>
          </div>
        ) : null}
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
