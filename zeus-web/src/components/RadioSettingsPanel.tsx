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
import { useAudioStore, type TxAudioSource } from '../state/audio-store';
import { useHl2GpioStore } from '../state/hl2-gpio-store';
import { usePttStore } from '../state/ptt-store';
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

// TX-audio source labels for the single-select segmented control. The control
// is a radio-button group (role="radiogroup") bound to the ONE TxAudioSource
// enum value — exactly one is active at a time, so the prior bug (independent
// checkboxes that came up checked and let you pick more than one) is
// structurally impossible. Host is always offered; the radio jacks render only
// when the connected board exposes them (board-gated below).
const AUDIO_SOURCE_LABEL: Record<TxAudioSource, string> = {
  Host: 'Host',
  RadioMic: 'Radio Mic',
  RadioLineIn: 'Radio Line In',
  RadioBalancedXlr: 'Radio Balanced',
};

// Explicit confirmation copy for enabling mic bias — the floating-connector
// RF / PTT-hang risk (plan §7). Wording is a maintainer (Brian) call; kept
// short and factual. Only gates turning bias ON; turning it OFF is unguarded.
const MIC_BIAS_CONFIRM =
  'Enable mic bias?\n\n' +
  'This supplies DC bias voltage on the mic connector for electret ' +
  'microphones. On a floating or unconnected mic jack it can hang PTT or ' +
  'couple RF. Leave it OFF unless your microphone needs bias.';

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

  // Board-gated single-select source list. Host is ALWAYS present and is the
  // default; each radio jack appears only when its capability flag is set.
  // HL2 (no codec, no radio jacks) collapses to Host-only — handled by the
  // dedicated note branch below, so this list is for codec boards.
  const audioSources: TxAudioSource[] = [
    'Host',
    ...(hasCodecAudio ? (['RadioMic'] as const) : []),
    ...(caps.hasRadioLineIn ? (['RadioLineIn'] as const) : []),
    ...(caps.hasBalancedXlr ? (['RadioBalancedXlr'] as const) : []),
  ];

  const onSelectSource = (next: TxAudioSource) => {
    if (next === audio.source) return;
    void updateAudio({ source: next });
  };

  // Mic bias is OFF by default and gated behind an explicit confirmation when
  // turning it ON (floating-connector RF / PTT-hang risk, plan §7). Turning it
  // off needs no confirmation.
  const onToggleMicBias = (next: boolean) => {
    if (next && !window.confirm(MIC_BIAS_CONFIRM)) return;
    void updateAudio({ micBias: next });
  };

  const gpio = useHl2GpioStore((s) => s.state);
  const gpioInflight = useHl2GpioStore((s) => s.inflight);
  const loadGpio = useHl2GpioStore((s) => s.load);
  const setGpioBit = useHl2GpioStore((s) => s.setBit);

  // Hardware PTT-IN (footswitch / mic-PTT / rear-KEY). Every board has one, so
  // this card is ungated. `keyed` is driven live by the PttStatusFrame WS edge
  // (per-protocol source server-side); `enabled` / `hangMs` hydrate from REST.
  const pttKeyed = usePttStore((s) => s.keyed);
  const pttEnabled = usePttStore((s) => s.enabled);
  const pttHangMs = usePttStore((s) => s.hangMs);
  const pttInflight = usePttStore((s) => s.inflight);
  const loadPtt = usePttStore((s) => s.load);
  const setPttEnabled = usePttStore((s) => s.setEnabled);

  useEffect(() => {
    load();
    loadAudio();
    loadGpio();
    loadPtt();
  }, [load, loadAudio, loadGpio, loadPtt]);

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

          {/* HL2 has no codec → Host-only; render a note, no picker. */}
          {hasHl2Audio && !hasCodecAudio ? (
            <div className="ps-field">
              <div className="ps-name">
                TX Audio Source
                <em>
                  Hermes-Lite 2 has no onboard audio codec — TX audio comes from
                  the host (USB / Ethernet) only.
                </em>
              </div>
              <select className="ps-select-mini" value="Host" disabled>
                <option value="Host">Host</option>
              </select>
            </div>
          ) : (
            <>
              {/* Single-select TX-audio source — a radio-button group bound to
                  the ONE TxAudioSource value, so it is physically impossible to
                  pick more than one (illegal states unrepresentable). Board-
                  gated: Host always present; radio jacks render only when the
                  connected board exposes them. */}
              <div className="ps-field">
                <div className="ps-name">
                  TX Audio Source
                  <em>
                    Which input feeds the transmitter. Host uses the computer
                    mic / audio chain; the radio options digitize the rig's own
                    analog jacks. Exactly one is active at a time.
                  </em>
                </div>
                <div
                  className="btn-row wrap"
                  role="radiogroup"
                  aria-label="TX audio source"
                >
                  {audioSources.map((src) => {
                    const active = audio.source === src;
                    return (
                      <button
                        key={src}
                        type="button"
                        role="radio"
                        aria-checked={active}
                        className={`btn sm ${active ? 'active' : ''}`}
                        disabled={audioInflight}
                        onClick={() => onSelectSource(src)}
                      >
                        {AUDIO_SOURCE_LABEL[src]}
                      </button>
                    );
                  })}
                </div>
              </div>

              {/* Mic boost — parameter of Radio Mic / Balanced. */}
              {audio.source === 'RadioMic' ||
              audio.source === 'RadioBalancedXlr' ? (
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
                      onChange={(e) =>
                        void updateAudio({ micBoost: e.target.checked })
                      }
                    />
                    <span className="ps-check-box" />
                    <span>{audio.micBoost ? 'On' : 'Off'}</span>
                  </label>
                </div>
              ) : null}

              {/* Mic bias — DEFAULTS OFF, floating-connector PTT-hang guard.
                  Parameter of Radio Mic / Balanced, bias-capable boards only. */}
              {caps.hasMicBias &&
              (audio.source === 'RadioMic' ||
                audio.source === 'RadioBalancedXlr') ? (
                <div className="ps-field">
                  <div className="ps-name">
                    Mic Bias
                    <em>
                      Supply bias voltage for electret microphones. Leave OFF
                      unless your mic needs it — enabling it on a floating /
                      unconnected connector can hang PTT.
                    </em>
                  </div>
                  <label className="ps-check">
                    <input
                      type="checkbox"
                      checked={audio.micBias}
                      disabled={audioInflight}
                      onChange={(e) => onToggleMicBias(e.target.checked)}
                    />
                    <span className="ps-check-box" />
                    <span>{audio.micBias ? 'On' : 'Off (default)'}</span>
                  </label>
                </div>
              ) : null}

              {/* Line-in gain 0..31 — parameter of Radio Line In. */}
              {audio.source === 'RadioLineIn' ? (
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
                        void updateAudio({
                          lineInGain: Math.min(31, Math.max(0, n)),
                        });
                      }
                    }}
                  />
                </div>
              ) : null}
            </>
          )}
        </div>
      ) : null}

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
