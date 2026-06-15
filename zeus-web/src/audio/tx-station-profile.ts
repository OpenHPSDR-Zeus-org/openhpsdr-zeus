// SPDX-License-Identifier: GPL-2.0-or-later

import type {
  CfcBandDto,
  CfcConfigDto,
  RxMode,
  TxLevelingConfigDto,
  TxStationProfileDto,
} from '../api/client';

export type TxStationProfileId = 'studio-ssb' | 'essb' | 'dx';
export type TxAudioSuiteRoute = TxStationProfileDto['audioSuiteRoute'];

export type TxStationProfile = Omit<TxStationProfileDto, 'id'> & {
  id: TxStationProfileId;
};

export type ResolvedTxStationProfile = {
  profile: TxStationProfile;
  cfcConfig: CfcConfigDto;
  filter: { lowHz: number; highHz: number };
};

const TX_PROFILE_IDS: readonly TxStationProfileId[] = ['studio-ssb', 'essb', 'dx'];

export const STUDIO_SSB_MIC_GAIN_DB = 0;
export const STUDIO_SSB_LEVELER_MAX_GAIN_DB = 8;
export const ESSB_MIC_GAIN_DB = -4;
export const ESSB_LEVELER_MAX_GAIN_DB = 11;
export const DX_MIC_GAIN_DB = -2;
export const DX_LEVELER_MAX_GAIN_DB = 12;

export const STUDIO_SSB_LOW_CUT_HZ = 150;
export const STUDIO_SSB_HIGH_CUT_HZ = 2900;
export const STUDIO_SSB_SPECTRAL_DENSITY = 55;
export const ESSB_LOW_CUT_HZ = 40;
export const ESSB_HIGH_CUT_HZ = 5000;
export const ESSB_SPECTRAL_DENSITY = 100;
export const DX_LOW_CUT_HZ = 300;
export const DX_HIGH_CUT_HZ = 2850;
export const DX_SPECTRAL_DENSITY = 100;

export const STUDIO_SSB_AUDIO_SUITE_ROUTE: TxAudioSuiteRoute = 'native';
export const ESSB_AUDIO_SUITE_ROUTE: TxAudioSuiteRoute = 'vst';
export const DX_AUDIO_SUITE_ROUTE: TxAudioSuiteRoute = 'vst';

export const STUDIO_SSB_TX_LEVELING: TxLevelingConfigDto = {
  alcMaxGainDb: 3,
  alcDecayMs: 10,
  levelerEnabled: true,
  levelerDecayMs: 100,
  compressorEnabled: false,
  compressorGainDb: 0,
};

export const ESSB_TX_LEVELING: TxLevelingConfigDto = {
  alcMaxGainDb: 3,
  alcDecayMs: 10,
  levelerEnabled: true,
  levelerDecayMs: 180,
  compressorEnabled: false,
  compressorGainDb: 0,
};

export const DX_TX_LEVELING: TxLevelingConfigDto = {
  alcMaxGainDb: 3,
  alcDecayMs: 8,
  levelerEnabled: true,
  levelerDecayMs: 70,
  compressorEnabled: false,
  compressorGainDb: 0,
};

// Gentle controlled-presence CFC curve for clean SSB speech. Post-EQ is on so
// the spectral-shaping band gains actually participate in the profile.
export const STUDIO_SSB_CFC_CONFIG: CfcConfigDto = {
  enabled: true,
  postEqEnabled: true,
  preCompDb: 1.25,
  prePeqDb: 0,
  bands: [
    { freqHz: 80, compLevelDb: 0.5, postGainDb: -4 },
    { freqHz: 150, compLevelDb: 1, postGainDb: -2 },
    { freqHz: 250, compLevelDb: 2, postGainDb: -1 },
    { freqHz: 500, compLevelDb: 3, postGainDb: 0 },
    { freqHz: 900, compLevelDb: 4, postGainDb: 0.5 },
    { freqHz: 1500, compLevelDb: 5, postGainDb: 1 },
    { freqHz: 2200, compLevelDb: 4.5, postGainDb: 1.5 },
    { freqHz: 2800, compLevelDb: 3.5, postGainDb: 1.5 },
    { freqHz: 3500, compLevelDb: 2, postGainDb: -1 },
    { freqHz: 5000, compLevelDb: 1, postGainDb: -3 },
  ],
};

// Extended SSB voice curve: wider passband plus low-mid weight. Mic gain is
// lower because the curve intentionally carries more broadband energy.
export const ESSB_CFC_CONFIG: CfcConfigDto = {
  enabled: true,
  postEqEnabled: true,
  preCompDb: 1.5,
  prePeqDb: 0,
  bands: [
    { freqHz: 50, compLevelDb: 0.5, postGainDb: -1.6 },
    { freqHz: 90, compLevelDb: 1.5, postGainDb: 1.8 },
    { freqHz: 140, compLevelDb: 2.6, postGainDb: 2.6 },
    { freqHz: 220, compLevelDb: 3.8, postGainDb: 2.3 },
    { freqHz: 400, compLevelDb: 4.8, postGainDb: 0.8 },
    { freqHz: 750, compLevelDb: 5.6, postGainDb: -0.4 },
    { freqHz: 1200, compLevelDb: 5.4, postGainDb: 0 },
    { freqHz: 2200, compLevelDb: 4.8, postGainDb: 1.2 },
    { freqHz: 3400, compLevelDb: 3.4, postGainDb: 1.4 },
    { freqHz: 5000, compLevelDb: 2.1, postGainDb: -1.2 },
  ],
};

// Pileup profile: trimmed lows, high speech-density, and a forward
// 900-2400 Hz region. It is intentionally narrower than eSSB so it punches
// through adjacent callers without dragging a thick low end into the pileup.
export const DX_CFC_CONFIG: CfcConfigDto = {
  enabled: true,
  postEqEnabled: true,
  preCompDb: 2.25,
  prePeqDb: 0,
  bands: [
    { freqHz: 120, compLevelDb: 1, postGainDb: -5 },
    { freqHz: 220, compLevelDb: 2.2, postGainDb: -3.2 },
    { freqHz: 350, compLevelDb: 3.4, postGainDb: -1.4 },
    { freqHz: 550, compLevelDb: 4.6, postGainDb: 0.6 },
    { freqHz: 850, compLevelDb: 5.8, postGainDb: 1.5 },
    { freqHz: 1200, compLevelDb: 6.4, postGainDb: 2.3 },
    { freqHz: 1700, compLevelDb: 6.2, postGainDb: 2.8 },
    { freqHz: 2300, compLevelDb: 5.6, postGainDb: 2.2 },
    { freqHz: 2850, compLevelDb: 4.2, postGainDb: 0.4 },
    { freqHz: 3600, compLevelDb: 2.4, postGainDb: -3.6 },
  ],
};

function round1(value: number): number {
  return Math.round(value * 10) / 10;
}

function clamp(value: number, min: number, max: number): number {
  if (!Number.isFinite(value)) return min;
  return Math.max(min, Math.min(max, value));
}

function clampInt(value: number, min: number, max: number): number {
  return Math.round(clamp(value, min, max));
}

function cloneCfcConfig(config: CfcConfigDto): CfcConfigDto {
  return {
    ...config,
    bands: config.bands.map((b) => ({ ...b })),
  };
}

function cloneTxLeveling(config: TxLevelingConfigDto): TxLevelingConfigDto {
  return { ...config };
}

function sanitizeAudioSuiteRoute(
  route: TxStationProfileDto['audioSuiteRoute'] | string | undefined,
  fallback: TxAudioSuiteRoute,
): TxAudioSuiteRoute {
  return route === 'vst' || route === 'native' ? route : fallback;
}

function txStationProfileCfcLabel(profileId: TxStationProfileId): string {
  if (profileId === 'essb') return 'eSSB CFC';
  if (profileId === 'dx') return 'DX CFC';
  return 'CFC presence';
}

export function formatTxStationProfileSummary(profile: TxStationProfile): string {
  const chain = profile.audioSuiteProfileName?.trim();
  const density =
    profile.spectralDensity >= 100
      ? 'max-density'
      : `density ${clampInt(profile.spectralDensity, 0, 100)}`;
  const lowCutHz = clampInt(profile.lowCutHz, 20, 600);
  const highCutHz = Math.max(lowCutHz + 100, clampInt(profile.highCutHz, 1500, 6000));

  return [
    chain ? `chain ${chain}` : 'current Audio Suite chain',
    `${density} ${txStationProfileCfcLabel(profile.id)}`,
    `SSB ${lowCutHz}..${highCutHz} Hz`,
  ]
    .filter(Boolean)
    .join(' / ');
}

export function cloneStudioSsbCfcConfig(): CfcConfigDto {
  return cloneCfcConfig(STUDIO_SSB_CFC_CONFIG);
}

export function cloneEssbCfcConfig(): CfcConfigDto {
  return cloneCfcConfig(ESSB_CFC_CONFIG);
}

export function cloneDxCfcConfig(): CfcConfigDto {
  return cloneCfcConfig(DX_CFC_CONFIG);
}

export function isTxStationProfileId(id: string): id is TxStationProfileId {
  return TX_PROFILE_IDS.includes(id as TxStationProfileId);
}

export function txStationProfileFilterForMode(
  profile: Pick<TxStationProfile, 'lowCutHz' | 'highCutHz'>,
  mode: RxMode,
): { lowHz: number; highHz: number } {
  const low = clampInt(profile.lowCutHz, 20, 600);
  const high = Math.max(low + 100, clampInt(profile.highCutHz, 1500, 6000));
  switch (mode) {
    case 'LSB':
    case 'DIGL':
    case 'CWL':
      return { lowHz: -high, highHz: -low };
    case 'USB':
    case 'DIGU':
    case 'CWU':
      return { lowHz: low, highHz: high };
    default:
      return { lowHz: -high, highHz: high };
  }
}

export function studioSsbTxFilterForMode(mode: RxMode): {
  lowHz: number;
  highHz: number;
} {
  return txStationProfileFilterForMode(STUDIO_SSB_PROFILE, mode);
}

export function essbTxFilterForMode(mode: RxMode): {
  lowHz: number;
  highHz: number;
} {
  return txStationProfileFilterForMode(ESSB_PROFILE, mode);
}

export function dxTxFilterForMode(mode: RxMode): {
  lowHz: number;
  highHz: number;
} {
  return txStationProfileFilterForMode(DX_PROFILE, mode);
}

function densityBandWeight(band: CfcBandDto): number {
  if (band.freqHz >= 700 && band.freqHz <= 2600) return 1;
  if (band.freqHz >= 350 && band.freqHz < 700) return 0.75;
  if (band.freqHz > 2600 && band.freqHz <= 3600) return 0.65;
  if (band.freqHz < 180) return 0.35;
  return 0.5;
}

export function applySpectralDensity(
  config: CfcConfigDto,
  spectralDensity: number,
): CfcConfigDto {
  const density = clampInt(spectralDensity, 0, 100);
  const drive = (density - 50) / 50;
  return {
    ...config,
    enabled: true,
    postEqEnabled: true,
    preCompDb: round1(clamp(config.preCompDb + drive * 1.2, -12, 12)),
    bands: config.bands.map((band) => {
      const weight = densityBandWeight(band);
      const lowTighten = drive > 0 && band.freqHz < 180 ? drive * 0.5 : 0;
      return {
        ...band,
        compLevelDb: round1(
          clamp(band.compLevelDb + drive * 1.4 * weight, 0, 20),
        ),
        postGainDb: round1(
          clamp(band.postGainDb + drive * 0.7 * weight - lowTighten, -12, 12),
        ),
      };
    }),
  };
}

export function resolveTxStationProfile(
  profile: TxStationProfile,
  mode: RxMode,
): ResolvedTxStationProfile {
  return {
    profile,
    cfcConfig: applySpectralDensity(profile.cfcConfig, profile.spectralDensity),
    filter: txStationProfileFilterForMode(profile, mode),
  };
}

export const STUDIO_SSB_PROFILE: TxStationProfile = {
  id: 'studio-ssb',
  label: 'Studio SSB',
  summary: 'Current Audio Suite chain / CFC presence / 2.9k SSB / density 55',
  applyTitle:
    'Apply a clean TX baseline: ALC/leveler defaults, CFC post-EQ presence, and sideband-correct SSB TX filter. The selected Audio Suite chain owns route and rack state.',
  audioSuiteRoute: STUDIO_SSB_AUDIO_SUITE_ROUTE,
  audioSuiteBypassed: true,
  audioSuiteProfileName: '',
  micGainDb: STUDIO_SSB_MIC_GAIN_DB,
  levelerMaxGainDb: STUDIO_SSB_LEVELER_MAX_GAIN_DB,
  txLeveling: STUDIO_SSB_TX_LEVELING,
  cfcConfig: STUDIO_SSB_CFC_CONFIG,
  lowCutHz: STUDIO_SSB_LOW_CUT_HZ,
  highCutHz: STUDIO_SSB_HIGH_CUT_HZ,
  spectralDensity: STUDIO_SSB_SPECTRAL_DENSITY,
};

export const ESSB_PROFILE: TxStationProfile = {
  id: 'essb',
  label: 'eSSB Wide',
  summary: 'Current Audio Suite chain / max-density eSSB CFC / 5.0k SSB',
  applyTitle:
    'Apply a wide high-density eSSB voice profile: lower mic gain, slower leveler action, 5 kHz sideband-correct TX filter, and the max-density eSSB CFC curve. The selected Audio Suite chain owns route and rack state.',
  audioSuiteRoute: ESSB_AUDIO_SUITE_ROUTE,
  audioSuiteBypassed: false,
  audioSuiteProfileName: '',
  micGainDb: ESSB_MIC_GAIN_DB,
  levelerMaxGainDb: ESSB_LEVELER_MAX_GAIN_DB,
  txLeveling: ESSB_TX_LEVELING,
  cfcConfig: ESSB_CFC_CONFIG,
  lowCutHz: ESSB_LOW_CUT_HZ,
  highCutHz: ESSB_HIGH_CUT_HZ,
  spectralDensity: ESSB_SPECTRAL_DENSITY,
};

export const DX_PROFILE: TxStationProfile = {
  id: 'dx',
  label: 'DX Punch',
  summary: 'Current Audio Suite chain / max-density DX CFC / 2.6k SSB',
  applyTitle:
    'Apply a pileup profile: trimmed lows, fast leveler recovery, dense CFC presence, and a narrow sideband-correct TX filter for intelligibility. The selected Audio Suite chain owns route and rack state.',
  audioSuiteRoute: DX_AUDIO_SUITE_ROUTE,
  audioSuiteBypassed: false,
  audioSuiteProfileName: '',
  micGainDb: DX_MIC_GAIN_DB,
  levelerMaxGainDb: DX_LEVELER_MAX_GAIN_DB,
  txLeveling: DX_TX_LEVELING,
  cfcConfig: DX_CFC_CONFIG,
  lowCutHz: DX_LOW_CUT_HZ,
  highCutHz: DX_HIGH_CUT_HZ,
  spectralDensity: DX_SPECTRAL_DENSITY,
};

export const TX_STATION_PROFILES: ReadonlyArray<TxStationProfile> = [
  STUDIO_SSB_PROFILE,
  ESSB_PROFILE,
  DX_PROFILE,
];

function cloneTxStationProfile(profile: TxStationProfile): TxStationProfile {
  return {
    ...profile,
    txLeveling: cloneTxLeveling(profile.txLeveling),
    cfcConfig: cloneCfcConfig(profile.cfcConfig),
  };
}

export function getTxStationProfile(
  id: string,
  profiles: ReadonlyArray<TxStationProfile> = TX_STATION_PROFILES,
): TxStationProfile {
  return profiles.find((p) => p.id === id) ?? profiles[0] ?? STUDIO_SSB_PROFILE;
}

export function cloneTxStationProfileCfcConfig(profile: TxStationProfile): CfcConfigDto {
  return cloneCfcConfig(profile.cfcConfig);
}

export function sanitizeTxStationProfile(
  raw: TxStationProfileDto,
  fallback: TxStationProfile,
): TxStationProfile {
  const id = isTxStationProfileId(raw.id) ? raw.id : fallback.id;
  const lowCutHz = clampInt(raw.lowCutHz, 20, 600);
  const highCutHz = Math.max(lowCutHz + 100, clampInt(raw.highCutHz, 1500, 6000));
  const bands =
    raw.cfcConfig?.bands?.length === 10
      ? raw.cfcConfig.bands.map((band) => ({
          freqHz: clamp(band.freqHz, 20, 6000),
          compLevelDb: round1(clamp(band.compLevelDb, 0, 20)),
          postGainDb: round1(clamp(band.postGainDb, -12, 12)),
        }))
      : cloneCfcConfig(fallback.cfcConfig).bands;
  return {
    ...fallback,
    ...raw,
    id,
    label: raw.label?.trim() || fallback.label,
    summary: raw.summary?.trim() || fallback.summary,
    applyTitle: raw.applyTitle?.trim() || fallback.applyTitle,
    audioSuiteRoute: sanitizeAudioSuiteRoute(
      raw.audioSuiteRoute,
      fallback.audioSuiteRoute,
    ),
    audioSuiteBypassed: raw.audioSuiteBypassed ?? fallback.audioSuiteBypassed,
    audioSuiteProfileName:
      raw.audioSuiteProfileName?.trim().slice(0, 96) ??
      fallback.audioSuiteProfileName,
    micGainDb: round1(clamp(raw.micGainDb, -40, 10)),
    levelerMaxGainDb: round1(clamp(raw.levelerMaxGainDb, 0, 20)),
    txLeveling: {
      alcMaxGainDb: round1(clamp(raw.txLeveling?.alcMaxGainDb ?? fallback.txLeveling.alcMaxGainDb, 0, 120)),
      alcDecayMs: clampInt(raw.txLeveling?.alcDecayMs ?? fallback.txLeveling.alcDecayMs, 1, 50),
      levelerEnabled: raw.txLeveling?.levelerEnabled ?? fallback.txLeveling.levelerEnabled,
      levelerDecayMs: clampInt(raw.txLeveling?.levelerDecayMs ?? fallback.txLeveling.levelerDecayMs, 1, 5000),
      compressorEnabled: raw.txLeveling?.compressorEnabled ?? fallback.txLeveling.compressorEnabled,
      compressorGainDb: round1(clamp(raw.txLeveling?.compressorGainDb ?? fallback.txLeveling.compressorGainDb, 0, 20)),
    },
    cfcConfig: {
      enabled: raw.cfcConfig?.enabled ?? fallback.cfcConfig.enabled,
      postEqEnabled: raw.cfcConfig?.postEqEnabled ?? fallback.cfcConfig.postEqEnabled,
      preCompDb: round1(clamp(raw.cfcConfig?.preCompDb ?? fallback.cfcConfig.preCompDb, -12, 12)),
      prePeqDb: round1(clamp(raw.cfcConfig?.prePeqDb ?? fallback.cfcConfig.prePeqDb, -12, 12)),
      bands,
    },
    lowCutHz,
    highCutHz,
    spectralDensity: clampInt(raw.spectralDensity, 0, 100),
  };
}

export function mergeTxStationProfileOverrides(
  overrides: ReadonlyArray<TxStationProfileDto>,
): TxStationProfile[] {
  return TX_STATION_PROFILES.map((profile) => {
    const override = overrides.find((entry) => entry.id === profile.id);
    return override
      ? sanitizeTxStationProfile(override, profile)
      : cloneTxStationProfile(profile);
  });
}

export function txStationProfileToDto(profile: TxStationProfile): TxStationProfileDto {
  return {
    ...cloneTxStationProfile(profile),
    summary: formatTxStationProfileSummary(profile),
  };
}
