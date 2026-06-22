// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

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
  /** True when the connected/effective board exposes ANAN-G2/Saturn ADC
   *  dither/randomizer controls over Protocol-2 CmdRx bytes 5/6. Gates the
   *  ANAN-G2 Options section in the RADIO tab. */
  supportsG2AdcOptions: boolean;
  /** Rated maximum forward output power in watts. Used as the default top
   *  of the TX power meter axis so a fresh connect to any radio gives a
   *  meter that's neither cramped nor blank. HermesLite2 / Hermes = 10 W,
   *  HermesII / ANAN-10E = 30 W, ANAN-100/200/G2 family = 120 W,
   *  ANAN-8000DLE = 250 W, ANAN-G2-1K = 1000 W. Operator override lives in
   *  the PA settings panel. */
  maxPowerWatts: number;
  /** Maximum supported RX/DDC sample rate in Hz. Protocol 1 remains capped
   *  at 384 kHz, while bench-confirmed Protocol 2 G2-class radios can expose
   *  768/1536 kHz. Unknown or conservative boards report 384 kHz. */
  maxRxSampleRateHz: number;
  /** True when the connected board is Anvelina-PRO3 over Protocol 2 — it
   *  exposes USEROUT7..10 via EU2AV's Open_Collector_Anvelina_DX spec
   *  (issue Kb2uka/openhpsdr-zeus#407, wire byte 1397 bits [4:1]). The
   *  PA Settings panel's DX OUT subsection renders unconditionally but
   *  disables itself when this flag is false. */
  supportsAnvelinaDxOc: boolean;
  // ---- TX audio front-end (external-audio-jacks re-port) ----
  /** Board decodes the host→radio audio STREAM (TLV320 codec). Gates the
   *  Radio Mic source. False on Hermes-Lite 2 (no stream codec). */
  hasOnboardCodec: boolean;
  /** Hermes-Lite 2 0x14-frame mic front-end (inert plumbing in v1). */
  hermesLite2MicFrontEnd: boolean;
  /** Board has a switchable analog line-in jack (gates RadioLineIn). True for
   *  ANAN-200D, the 0x0A Saturn family, and the ANAN-10E (HermesII, issue #667);
   *  false for the remaining Hermes-class P1, ANAN-G2E, Metis, HL2. Gates the
   *  Line-In source option + its gain slider. */
  hasRadioLineIn: boolean;
  /** Board has a switchable balanced XLR mic input (G2 / G2-1K only). */
  hasBalancedXlr: boolean;
  /** Board can enable the Orion electret mic-bias supply (gates the bias
   *  toggle). Defaults OFF; the UI confirms before enabling. */
  hasMicBias: boolean;
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
  supportsG2AdcOptions: false,
  maxPowerWatts: 100,
  maxRxSampleRateHz: 384_000,
  supportsAnvelinaDxOc: false,
  hasOnboardCodec: false,
  hermesLite2MicFrontEnd: false,
  hasRadioLineIn: false,
  hasBalancedXlr: false,
  hasMicBias: false,
};

function parseSampleRateCeiling(raw: unknown): number {
  if (typeof raw !== 'number' || !Number.isFinite(raw)) {
    return UNKNOWN_BOARD_CAPABILITIES.maxRxSampleRateHz;
  }
  const hz = Math.round(raw);
  return hz >= 48_000 && hz <= 1_536_000
    ? hz
    : UNKNOWN_BOARD_CAPABILITIES.maxRxSampleRateHz;
}

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
    supportsG2AdcOptions:
      typeof r.supportsG2AdcOptions === 'boolean'
        ? r.supportsG2AdcOptions
        : UNKNOWN_BOARD_CAPABILITIES.supportsG2AdcOptions,
    maxPowerWatts:
      typeof r.maxPowerWatts === 'number' && r.maxPowerWatts > 0
        ? r.maxPowerWatts
        : UNKNOWN_BOARD_CAPABILITIES.maxPowerWatts,
    maxRxSampleRateHz: parseSampleRateCeiling(r.maxRxSampleRateHz),
    supportsAnvelinaDxOc:
      typeof r.supportsAnvelinaDxOc === 'boolean'
        ? r.supportsAnvelinaDxOc
        : UNKNOWN_BOARD_CAPABILITIES.supportsAnvelinaDxOc,
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
  };
}

export async function fetchBoardCapabilities(signal?: AbortSignal): Promise<BoardCapabilities> {
  const res = await fetch('/api/radio/capabilities', { signal });
  if (!res.ok) throw new Error(`GET /api/radio/capabilities → ${res.status}`);
  return parseBoardCapabilities(await res.json());
}
