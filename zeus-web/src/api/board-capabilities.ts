// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.

// Per-board capability fingerprint (post-#218 Phase 2). Mirrors
// Zeus.Contracts.BoardCapabilities — see
// docs/references/protocol-1/thetis-board-matrix.md for the per-board
// values. The web reads this once at connect via /api/radio/capabilities
// and gates feature panels on the flags (Phase 5).
export interface BoardCapabilities {
  rxAdcCount: number;
  mkiiBpf: boolean;
  adcSupplyMv: number;
  lrAudioSwap: boolean;
  hasVolts: boolean;
  hasAmps: boolean;
  hasAudioAmplifier: boolean;
  hasSteppedAttenuationRx2: boolean;
  supportsPathIllustrator: boolean;
  /** True when the connected board exposes HL2-only firmware toggles
   *  (currently Band Volts PWM output — see issue Kb2uka/openhpsdr-zeus#279
   *  and hermes-lite2-protocol.md addr 0x00 bit 11). Gates the RADIO tab
   *  in SettingsMenu. False on every non-HL2 board. */
  hasHl2OptionalToggles: boolean;
  /** Rated maximum forward output power in watts. Used as the default top
   *  of the TX power meter axis so a fresh connect to any radio gives a
   *  meter that's neither cramped nor blank. HermesLite2 / Hermes = 10 W,
   *  HermesII / ANAN-10E = 30 W, ANAN-100/200/G2 family = 120 W,
   *  ANAN-8000DLE = 250 W, ANAN-G2-1K = 1000 W. Operator override lives in
   *  the PA settings panel. */
  maxPowerWatts: number;
  /** True when the connected board is Anvelina-PRO3 over Protocol 2 — it
   *  exposes USEROUT7..10 via EU2AV's Open_Collector_Anvelina_DX spec
   *  (issue Kb2uka/openhpsdr-zeus#407, wire byte 1397 bits [4:1]). The
   *  PA Settings panel's DX OUT subsection renders unconditionally but
   *  disables itself when this flag is false. */
  supportsAnvelinaDxOc: boolean;
  // ---- External-port control (external-ports plan) ----
  /** Board has switchable TX antenna relays (ANT1/2/3) — 0x0A / Saturn family
   *  only. Gates the TX-antenna selector in the Radio Settings menu. */
  hasTxAntennaRelays: boolean;
  /** Board has switchable RX antenna relays (ANT1/2/3) via its Alex board.
   *  True for every ANAN board; false for Hermes-Lite 2 (single jack — its
   *  selection is clamped to ANT1 at the wire layer). Gates the RX-antenna
   *  selector. */
  hasRxAntennaRelays: boolean;
  /** Board decodes the host audio stream (TLV320 codec) carrying the
   *  Hermes-class mic/line-in front-end. True for ANAN; false for HL2 (no
   *  stream codec). Gates the Hermes-class audio front-end controls. */
  hasOnboardCodec: boolean;
  /** Board has the Hermes-Lite 2 mic/PTT/line-in front-end (register 0x14).
   *  HL2 only. Gates the HL2 audio front-end controls. */
  hermesLite2MicFrontEnd: boolean;
  /** Board has an analog line-in jack selectable as the TX-audio source
   *  (TxAudioSource.RadioLineIn). True for ANAN-200D + the whole 0x0A Saturn
   *  family; false for pure Hermes-class P1, ANAN-G2E, Metis, HL2. Gates the
   *  Line-In source option + its gain slider. */
  hasRadioLineIn: boolean;
  /** Board has a switchable balanced XLR mic input (TxAudioSource.RadioBalancedXlr).
   *  Saturn-FPGA ANAN-G2 / G2-1K only. Gates the Balanced-XLR source option. */
  hasBalancedXlr: boolean;
  /** Board exposes the Orion mic-bias enable on its mic jack. True for
   *  ANAN-200D / 7000DLE / 8000DLE / G2 / G2-1K / ANVELINA-PRO3; false for
   *  Red Pitaya, Hermes-class, ANAN-G2E, Metis, HL2. Gates the (confirmation-
   *  guarded) mic-bias toggle that appears with RadioMic. */
  hasMicBias: boolean;
  // ---- External-port control (Phase 5) ----
  /** Board has a second RX antenna path (dual-Alex / dual-RX). True for the
   *  dual-ADC boards (100D/200D/0x0A family). Gates the RX2-antenna selector. */
  hasRx2AntennaPath: boolean;
  /** Board has the 4-bit HL2 user GPIO (user_dig_out on register 0x14 C3[3:0]).
   *  HL2 only. Gates the user GPIO toggle group. */
  hasHl2UserGpio: boolean;
}

// Safe defaults matching Zeus.Contracts.BoardCapabilities.UnknownDefaults —
// used when the server hasn't responded yet or when the JSON is malformed.
// Every flag false / single ADC / 33 mV supply: no panels appear by
// default, which is the right behaviour for "we don't know what this is yet".
export const UNKNOWN_BOARD_CAPABILITIES: BoardCapabilities = {
  rxAdcCount: 1,
  mkiiBpf: false,
  adcSupplyMv: 33,
  lrAudioSwap: false,
  hasVolts: false,
  hasAmps: false,
  hasAudioAmplifier: false,
  hasSteppedAttenuationRx2: false,
  supportsPathIllustrator: false,
  hasHl2OptionalToggles: false,
  maxPowerWatts: 100,
  supportsAnvelinaDxOc: false,
  hasTxAntennaRelays: false,
  hasRxAntennaRelays: false,
  hasOnboardCodec: false,
  hermesLite2MicFrontEnd: false,
  hasRadioLineIn: false,
  hasBalancedXlr: false,
  hasMicBias: false,
  hasRx2AntennaPath: false,
  hasHl2UserGpio: false,
};

export function parseBoardCapabilities(raw: unknown): BoardCapabilities {
  if (!raw || typeof raw !== 'object') return UNKNOWN_BOARD_CAPABILITIES;
  const r = raw as Record<string, unknown>;
  return {
    rxAdcCount: typeof r.rxAdcCount === 'number' ? r.rxAdcCount : UNKNOWN_BOARD_CAPABILITIES.rxAdcCount,
    mkiiBpf: typeof r.mkiiBpf === 'boolean' ? r.mkiiBpf : UNKNOWN_BOARD_CAPABILITIES.mkiiBpf,
    adcSupplyMv: typeof r.adcSupplyMv === 'number' ? r.adcSupplyMv : UNKNOWN_BOARD_CAPABILITIES.adcSupplyMv,
    lrAudioSwap: typeof r.lrAudioSwap === 'boolean' ? r.lrAudioSwap : UNKNOWN_BOARD_CAPABILITIES.lrAudioSwap,
    hasVolts: typeof r.hasVolts === 'boolean' ? r.hasVolts : UNKNOWN_BOARD_CAPABILITIES.hasVolts,
    hasAmps: typeof r.hasAmps === 'boolean' ? r.hasAmps : UNKNOWN_BOARD_CAPABILITIES.hasAmps,
    hasAudioAmplifier:
      typeof r.hasAudioAmplifier === 'boolean'
        ? r.hasAudioAmplifier
        : UNKNOWN_BOARD_CAPABILITIES.hasAudioAmplifier,
    hasSteppedAttenuationRx2:
      typeof r.hasSteppedAttenuationRx2 === 'boolean'
        ? r.hasSteppedAttenuationRx2
        : UNKNOWN_BOARD_CAPABILITIES.hasSteppedAttenuationRx2,
    supportsPathIllustrator:
      typeof r.supportsPathIllustrator === 'boolean'
        ? r.supportsPathIllustrator
        : UNKNOWN_BOARD_CAPABILITIES.supportsPathIllustrator,
    hasHl2OptionalToggles:
      typeof r.hasHl2OptionalToggles === 'boolean'
        ? r.hasHl2OptionalToggles
        : UNKNOWN_BOARD_CAPABILITIES.hasHl2OptionalToggles,
    maxPowerWatts:
      typeof r.maxPowerWatts === 'number' && r.maxPowerWatts > 0
        ? r.maxPowerWatts
        : UNKNOWN_BOARD_CAPABILITIES.maxPowerWatts,
    supportsAnvelinaDxOc:
      typeof r.supportsAnvelinaDxOc === 'boolean'
        ? r.supportsAnvelinaDxOc
        : UNKNOWN_BOARD_CAPABILITIES.supportsAnvelinaDxOc,
    hasTxAntennaRelays:
      typeof r.hasTxAntennaRelays === 'boolean'
        ? r.hasTxAntennaRelays
        : UNKNOWN_BOARD_CAPABILITIES.hasTxAntennaRelays,
    hasRxAntennaRelays:
      typeof r.hasRxAntennaRelays === 'boolean'
        ? r.hasRxAntennaRelays
        : UNKNOWN_BOARD_CAPABILITIES.hasRxAntennaRelays,
    hasOnboardCodec:
      typeof r.hasOnboardCodec === 'boolean'
        ? r.hasOnboardCodec
        : UNKNOWN_BOARD_CAPABILITIES.hasOnboardCodec,
    hermesLite2MicFrontEnd:
      typeof r.hermesLite2MicFrontEnd === 'boolean'
        ? r.hermesLite2MicFrontEnd
        : UNKNOWN_BOARD_CAPABILITIES.hermesLite2MicFrontEnd,
    hasRadioLineIn:
      typeof r.hasRadioLineIn === 'boolean'
        ? r.hasRadioLineIn
        : UNKNOWN_BOARD_CAPABILITIES.hasRadioLineIn,
    hasBalancedXlr:
      typeof r.hasBalancedXlr === 'boolean'
        ? r.hasBalancedXlr
        : UNKNOWN_BOARD_CAPABILITIES.hasBalancedXlr,
    hasMicBias:
      typeof r.hasMicBias === 'boolean'
        ? r.hasMicBias
        : UNKNOWN_BOARD_CAPABILITIES.hasMicBias,
    hasRx2AntennaPath:
      typeof r.hasRx2AntennaPath === 'boolean'
        ? r.hasRx2AntennaPath
        : UNKNOWN_BOARD_CAPABILITIES.hasRx2AntennaPath,
    hasHl2UserGpio:
      typeof r.hasHl2UserGpio === 'boolean'
        ? r.hasHl2UserGpio
        : UNKNOWN_BOARD_CAPABILITIES.hasHl2UserGpio,
  };
}

export async function fetchBoardCapabilities(signal?: AbortSignal): Promise<BoardCapabilities> {
  const res = await fetch('/api/radio/capabilities', { signal });
  if (!res.ok) throw new Error(`GET /api/radio/capabilities → ${res.status}`);
  return parseBoardCapabilities(await res.json());
}
