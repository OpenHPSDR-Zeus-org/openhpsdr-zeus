// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// RADIO SETTINGS tab. Cards:
//   1. PTT-IN → MOX opt-in gate, with a live PTT-IN status lamp.
//   2. Audio Input — the single-select TX-audio SOURCE (external-audio-jacks
//      re-port). Board-gated off the per-board capability flags carried in the
//      /api/radio/audio GET response, so the picker offers only the jacks the
//      connected board has. Host is always available and is the default.
//
// Every board exposes a PTT-IN line, so that card is ungated by board. The
// `keyed` lamp is driven live by the PttStatusFrame WS edge regardless of the
// enable toggle; the toggle only controls whether a footswitch press is promoted
// to host MOX. Defaults OFF (opt-in) server-side.
//
// Visual idiom reuses PsSettingsPanel's `.ps-shell` / `.ps-card` / `.ps-field`
// surfaces (tokens only, no new chrome / palette). Layout / visual specifics
// are the maintainer's call — this stays clean and minimal.

import { useEffect } from 'react';
import { usePttStore } from '../state/ptt-store';
import { useAudioStore, type TxAudioSource } from '../state/audio-store';
import {
  useAntennaStore,
  type AntennaName,
  type RxAuxName,
} from '../state/antenna-store';

// TX-audio source labels for the single-select control. The control is a radio-
// button group (role="radiogroup") bound to the ONE TxAudioSource enum value —
// exactly one is active at a time, so independent-checkbox combinations are
// structurally impossible. Host is always offered; the radio jacks render only
// when the connected board exposes them (board-gated below).
const AUDIO_SOURCE_LABEL: Record<TxAudioSource, string> = {
  Host: 'Host',
  RadioMic: 'Radio Mic',
  RadioLineIn: 'Radio Line In',
  RadioBalancedXlr: 'Radio Balanced',
};

// Explicit confirmation copy for enabling mic bias — the floating-connector
// RF / PTT-hang risk. Only gates turning bias ON; turning it OFF is unguarded.
const MIC_BIAS_CONFIRM =
  'Enable mic bias?\n\n' +
  'This supplies DC bias voltage on the mic connector for electret ' +
  'microphones. On a floating or unconnected mic jack it can hang PTT or ' +
  'couple RF. Leave it OFF unless your microphone needs bias.';

const ANTENNA_OPTIONS: AntennaName[] = ['Ant1', 'Ant2', 'Ant3'];

export function RadioSettingsPanel() {
  const pttKeyed = usePttStore((s) => s.keyed);
  const pttEnabled = usePttStore((s) => s.enabled);
  const pttHangMs = usePttStore((s) => s.hangMs);
  const pttInflight = usePttStore((s) => s.inflight);
  const loadPtt = usePttStore((s) => s.load);
  const setPttEnabled = usePttStore((s) => s.setEnabled);

  const audio = useAudioStore((s) => s.settings);
  const audioInflight = useAudioStore((s) => s.inflight);
  const loadAudio = useAudioStore((s) => s.load);
  const updateAudio = useAudioStore((s) => s.update);

  const antSettings = useAntennaStore((s) => s.settings);
  const antInflight = useAntennaStore((s) => s.inflight);
  const loadAntenna = useAntennaStore((s) => s.load);
  const setAntennaBand = useAntennaStore((s) => s.setBand);

  useEffect(() => {
    void loadPtt();
    void loadAudio();
    void loadAntenna();
  }, [loadPtt, loadAudio, loadAntenna]);

  // Per-board source-availability gates ride the /api/radio/audio response, so
  // we read them straight off the audio settings (no separate caps fetch).
  const hasCodecAudio = audio.hasOnboardCodec;
  const hasHl2Audio = audio.hermesLite2MicFrontEnd;

  // Board-gated single-select source list. Host is ALWAYS present and is the
  // default / universal fallback; the radio jacks appear only when the board
  // advertises them.
  const audioSources: TxAudioSource[] = [
    'Host',
    ...(hasCodecAudio ? (['RadioMic'] as const) : []),
    ...(hasCodecAudio && audio.hasRadioLineIn ? (['RadioLineIn'] as const) : []),
    ...(hasCodecAudio && audio.hasBalancedXlr ? (['RadioBalancedXlr'] as const) : []),
  ];

  const onSelectSource = (next: TxAudioSource) => {
    if (next === audio.source) return;
    void updateAudio({ source: next });
  };

  const onToggleMicBias = (next: boolean) => {
    // Confirm only when ENABLING — the floating-connector PTT-hang guard.
    if (next && !window.confirm(MIC_BIAS_CONFIRM)) return;
    void updateAudio({ micBias: next });
  };

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

      {/* Audio Input — only when the connected board exposes a radio audio
          source (stream codec or the HL2 mic front-end). Host-only boards show
          nothing here. */}
      {hasCodecAudio || hasHl2Audio ? (
        <div className="ps-card">
          <h4>
            <svg className="ps-ic-sm" viewBox="0 0 12 12">
              <path d="M6 1a2 2 0 0 1 2 2v3a2 2 0 0 1-4 0V3a2 2 0 0 1 2-2zM3 6a3 3 0 0 0 6 0M6 9v2" />
            </svg>
            Audio Input
            <span className="ps-card-hint">
              {hasHl2Audio && !hasCodecAudio ? 'host only' : 'mic / line-in'}
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
                  pick more than one (illegal states unrepresentable). */}
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
              {audio.hasMicBias &&
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
