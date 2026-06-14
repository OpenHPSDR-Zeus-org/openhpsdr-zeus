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
  type RxAuxName,
} from '../state/antenna-store';
import { useAudioStore } from '../state/audio-store';
import { useHl2GpioStore } from '../state/hl2-gpio-store';
import { bandOf } from './design/data';

const ANTENNAS: AntennaName[] = ['Ant1', 'Ant2', 'Ant3'];
const ANTENNA_LABEL: Record<AntennaName, string> = {
  Ant1: 'ANT 1',
  Ant2: 'ANT 2',
  Ant3: 'ANT 3',
};

const RX_AUX_LABEL: Record<RxAuxName, string> = {
  None: 'Base ANT',
  Ext1: 'EXT 1',
  Ext2: 'EXT 2',
  Xvtr: 'XVTR IN',
  Bypass: 'RX BYPASS',
};

// HL2 user GPIO line labels (4-bit user_dig_out → MCP23008).
const GPIO_LINES = [0, 1, 2, 3] as const;

export function RadioSettingsPanel() {
  const vfoHz = useConnectionStore((s) => s.vfoHz);
  const caps = useRadioStore((s) => s.capabilities);
  const settings = useAntennaStore((s) => s.settings);
  const loaded = useAntennaStore((s) => s.loaded);
  const inflight = useAntennaStore((s) => s.inflight);
  const error = useAntennaStore((s) => s.error);
  const load = useAntennaStore((s) => s.load);
  const setBand = useAntennaStore((s) => s.setBand);

  const audio = useAudioStore((s) => s.settings);
  const audioInflight = useAudioStore((s) => s.inflight);
  const loadAudio = useAudioStore((s) => s.load);
  const updateAudio = useAudioStore((s) => s.update);
  const hasCodecAudio = caps.hasOnboardCodec;
  const hasHl2Audio = caps.hermesLite2MicFrontEnd;

  const gpio = useHl2GpioStore((s) => s.state);
  const gpioInflight = useHl2GpioStore((s) => s.inflight);
  const loadGpio = useHl2GpioStore((s) => s.load);
  const setGpioBit = useHl2GpioStore((s) => s.setBit);

  useEffect(() => {
    load();
    loadAudio();
    loadGpio();
  }, [load, loadAudio, loadGpio]);

  const band = bandOf(vfoHz);
  const onBand = band !== '—';
  const current = settings.bands.find((b) => b.band === band);
  const txAnt: AntennaName = current?.txAnt ?? 'Ant1';
  const rxAnt: AntennaName = current?.rxAnt ?? 'Ant1';
  const rxAux: RxAuxName = current?.rxAux ?? 'None';
  const auxOptions: RxAuxName[] = ['None', ...settings.availableRxAux];
  const showAux = settings.availableRxAux.length > 0;

  const onSetTx = (next: AntennaName) => {
    if (!onBand) return;
    void setBand(band, next, rxAnt, rxAux);
  };
  const onSetRx = (next: AntennaName) => {
    if (!onBand) return;
    void setBand(band, txAnt, next, rxAux);
  };
  const onSetRxAux = (next: RxAuxName) => {
    if (!onBand) return;
    void setBand(band, txAnt, rxAnt, next);
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

        {showAux ? (
          <div className="ps-field">
            <div className="ps-name">
              RX Aux Input
              <em>
                Auxiliary receive feed (EXT / transverter / RX-bypass) for
                {onBand ? ` ${band}` : ' the current band'}. While PureSignal is
                armed it keeps control of the RX-bypass coupler.
              </em>
            </div>
            <select
              className="ps-select-mini"
              value={rxAux}
              disabled={!onBand || inflight}
              onChange={(e) => onSetRxAux(e.target.value as RxAuxName)}
            >
              {auxOptions.map((a) => (
                <option key={a} value={a}>
                  {RX_AUX_LABEL[a]}
                </option>
              ))}
            </select>
          </div>
        ) : null}

        {caps.hasRx2AntennaPath ? (
          <div className="ps-field">
            <div className="ps-name">
              RX2 Antenna
              <em>
                Second-receiver antenna path (dual-RX boards). Per-board wire
                emission is pending bench verification on a 100D/200D — the
                selector is not yet active.
              </em>
            </div>
            <select className="ps-select-mini" value="Ant1" disabled>
              <option value="Ant1">ANT 1</option>
            </select>
          </div>
        ) : null}
      </div>

      {hasCodecAudio || hasHl2Audio ? (
        <div className="ps-card">
          <h4>
            <svg className="ps-ic-sm" viewBox="0 0 12 12">
              <path d="M6 1a2 2 0 0 1 2 2v3a2 2 0 0 1-4 0V3a2 2 0 0 1 2-2zM3 6a3 3 0 0 0 6 0M6 9v2" />
            </svg>
            Audio Input
            <span className="ps-card-hint">
              {hasHl2Audio ? 'mic / line front-end' : 'mic / line-in'}
            </span>
          </h4>

          {/* Line-in vs mic select — present on both codec and HL2 paths. */}
          <div className="ps-field">
            <div className="ps-name">
              Input Source
              <em>
                Select the line-in jack instead of the microphone input.
              </em>
            </div>
            <label className="ps-check">
              <input
                type="checkbox"
                checked={audio.lineIn}
                disabled={audioInflight}
                onChange={(e) => void updateAudio({ lineIn: e.target.checked })}
              />
              <span className="ps-check-box" />
              <span>{audio.lineIn ? 'Line In' : 'Microphone'}</span>
            </label>
          </div>

          {/* Mic boost — Hermes-class codec boards only. */}
          {hasCodecAudio ? (
            <div className="ps-field">
              <div className="ps-name">
                Mic Boost
                <em>+20 dB microphone preamp boost.</em>
              </div>
              <label className="ps-check">
                <input
                  type="checkbox"
                  checked={audio.micBoost}
                  disabled={audioInflight}
                  onChange={(e) => void updateAudio({ micBoost: e.target.checked })}
                />
                <span className="ps-check-box" />
                <span>{audio.micBoost ? 'On' : 'Off'}</span>
              </label>
            </div>
          ) : null}

          {/* Balanced / TRS input — HL2 mic_trs (balanced) or Saturn XLR. */}
          <div className="ps-field">
            <div className="ps-name">
              Balanced Input
              <em>
                {hasHl2Audio
                  ? 'Use the TRS (balanced) mic pin on the HL2 front-end.'
                  : 'Select the balanced (XLR) microphone input.'}
              </em>
            </div>
            <label className="ps-check">
              <input
                type="checkbox"
                checked={audio.balancedInput}
                disabled={audioInflight}
                onChange={(e) => void updateAudio({ balancedInput: e.target.checked })}
              />
              <span className="ps-check-box" />
              <span>{audio.balancedInput ? 'Balanced' : 'Standard'}</span>
            </label>
          </div>

          {/* Mic bias — DEFAULTS OFF, floating-connector PTT-hang guard. */}
          <div className="ps-field">
            <div className="ps-name">
              Mic Bias
              <em>
                Supply bias voltage for electret microphones. Leave OFF unless
                your mic needs it — enabling it on a floating / unconnected
                connector can hang PTT.
              </em>
            </div>
            <label className="ps-check">
              <input
                type="checkbox"
                checked={audio.micBias}
                disabled={audioInflight}
                onChange={(e) => void updateAudio({ micBias: e.target.checked })}
              />
              <span className="ps-check-box" />
              <span>{audio.micBias ? 'On' : 'Off (default)'}</span>
            </label>
          </div>

          {/* Line-in gain 0..31 — present on both paths. */}
          <div className="ps-field">
            <div className="ps-name">
              Line-In Gain
              <em>Line-in input gain (0–31).</em>
            </div>
            <input
              className="ps-select-mini"
              type="number"
              min={0}
              max={31}
              step={1}
              value={audio.lineInGain}
              disabled={audioInflight}
              onChange={(e) => {
                const n = Number.parseInt(e.target.value, 10);
                if (!Number.isNaN(n)) {
                  void updateAudio({ lineInGain: Math.min(31, Math.max(0, n)) });
                }
              }}
            />
          </div>
        </div>
      ) : null}

      {caps.hasHl2UserGpio && gpio.supported ? (
        <div className="ps-card">
          <h4>
            <svg className="ps-ic-sm" viewBox="0 0 12 12">
              <path d="M2 6h2M8 6h2M6 2v2M6 8v2M4 4h4v4H4z" />
            </svg>
            User GPIO
            <span className="ps-card-hint">HL2 — 4 digital outputs</span>
          </h4>
          <div className="ps-field">
            <div className="ps-name">
              Output Lines
              <em>
                The four user-controllable digital output pins (user_dig_out →
                MCP23008). Operator-defined; wire to whatever your station needs.
              </em>
            </div>
            <div style={{ display: 'flex', gap: '0.75rem', flexWrap: 'wrap' }}>
              {GPIO_LINES.map((bit) => {
                const on = (gpio.bits & (1 << bit)) !== 0;
                return (
                  <label className="ps-check" key={bit}>
                    <input
                      type="checkbox"
                      checked={on}
                      disabled={gpioInflight}
                      onChange={(e) => void setGpioBit(bit, e.target.checked)}
                    />
                    <span className="ps-check-box" />
                    <span>OUT {bit}</span>
                  </label>
                );
              })}
            </div>
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
