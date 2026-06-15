// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';

import {
  applySpectralDensity,
  cloneDxCfcConfig,
  cloneEssbCfcConfig,
  cloneStudioSsbCfcConfig,
  cloneTxStationProfileCfcConfig,
  DX_CFC_CONFIG,
  DX_LEVELER_MAX_GAIN_DB,
  DX_MIC_GAIN_DB,
  DX_PROFILE,
  DX_TX_LEVELING,
  dxTxFilterForMode,
  ESSB_CFC_CONFIG,
  ESSB_PROFILE,
  ESSB_LEVELER_MAX_GAIN_DB,
  ESSB_MIC_GAIN_DB,
  ESSB_TX_LEVELING,
  essbTxFilterForMode,
  getTxStationProfile,
  mergeTxStationProfileOverrides,
  resolveTxStationProfile,
  STUDIO_SSB_CFC_CONFIG,
  STUDIO_SSB_PROFILE,
  STUDIO_SSB_LEVELER_MAX_GAIN_DB,
  STUDIO_SSB_MIC_GAIN_DB,
  STUDIO_SSB_TX_LEVELING,
  studioSsbTxFilterForMode,
  TX_STATION_PROFILES,
} from './tx-station-profile';

describe('Studio SSB TX station profile', () => {
  it('keeps the gain staging conservative', () => {
    expect(STUDIO_SSB_MIC_GAIN_DB).toBe(0);
    expect(STUDIO_SSB_LEVELER_MAX_GAIN_DB).toBe(8);
    expect(STUDIO_SSB_TX_LEVELING).toEqual({
      alcMaxGainDb: 3,
      alcDecayMs: 10,
      levelerEnabled: true,
      levelerDecayMs: 100,
      compressorEnabled: false,
      compressorGainDb: 0,
    });
  });

  it('signs the TX filter for the active sideband family', () => {
    expect(studioSsbTxFilterForMode('LSB')).toEqual({ lowHz: -2900, highHz: -150 });
    expect(studioSsbTxFilterForMode('DIGL')).toEqual({ lowHz: -2900, highHz: -150 });
    expect(studioSsbTxFilterForMode('USB')).toEqual({ lowHz: 150, highHz: 2900 });
    expect(studioSsbTxFilterForMode('DIGU')).toEqual({ lowHz: 150, highHz: 2900 });
    expect(studioSsbTxFilterForMode('AM')).toEqual({ lowHz: -2900, highHz: 2900 });
  });

  it('uses a gentle ten-band CFC presence curve with post-EQ enabled', () => {
    expect(STUDIO_SSB_CFC_CONFIG.enabled).toBe(true);
    expect(STUDIO_SSB_CFC_CONFIG.postEqEnabled).toBe(true);
    expect(STUDIO_SSB_CFC_CONFIG.bands).toHaveLength(10);
    expect(Math.max(...STUDIO_SSB_CFC_CONFIG.bands.map((b) => b.compLevelDb))).toBeLessThanOrEqual(5);
    expect(Math.max(...STUDIO_SSB_CFC_CONFIG.bands.map((b) => b.postGainDb))).toBeLessThanOrEqual(1.5);
  });

  it('clones the CFC bands before handing them to UI code', () => {
    const a = cloneStudioSsbCfcConfig();
    const b = cloneStudioSsbCfcConfig();
    a.bands[0]!.compLevelDb = 9;
    expect(b.bands[0]!.compLevelDb).toBe(STUDIO_SSB_CFC_CONFIG.bands[0]!.compLevelDb);
  });
});

describe('TX station profile catalog', () => {
  it('exposes Studio SSB, eSSB, and DX profiles for the panel selector', () => {
    expect(TX_STATION_PROFILES.map((p) => p.id)).toEqual(['studio-ssb', 'essb', 'dx']);
    expect(getTxStationProfile('studio-ssb')).toBe(STUDIO_SSB_PROFILE);
    expect(getTxStationProfile('essb')).toBe(ESSB_PROFILE);
    expect(getTxStationProfile('dx')).toBe(DX_PROFILE);
    expect(getTxStationProfile('missing')).toBe(STUDIO_SSB_PROFILE);
  });

  it('carries Audio Suite route intent for each station profile', () => {
    expect(STUDIO_SSB_PROFILE.audioSuiteRoute).toBe('native');
    expect(STUDIO_SSB_PROFILE.audioSuiteBypassed).toBe(true);
    expect(ESSB_PROFILE.audioSuiteRoute).toBe('vst');
    expect(ESSB_PROFILE.audioSuiteBypassed).toBe(false);
    expect(DX_PROFILE.audioSuiteRoute).toBe('vst');
    expect(DX_PROFILE.audioSuiteBypassed).toBe(false);
  });

  it('clones selected profile CFC bands before applying them', () => {
    const a = cloneTxStationProfileCfcConfig(ESSB_PROFILE);
    const b = cloneTxStationProfileCfcConfig(ESSB_PROFILE);
    a.bands[0]!.postGainDb = 9;
    expect(b.bands[0]!.postGainDb).toBe(ESSB_CFC_CONFIG.bands[0]!.postGainDb);
  });

  it('merges persisted overrides onto known defaults only', () => {
    const profiles = mergeTxStationProfileOverrides([
      {
        ...ESSB_PROFILE,
        audioSuiteProfileName: 'ESSB Broadcast',
        highCutHz: 5400,
        spectralDensity: 88,
      },
      { ...DX_PROFILE, id: 'unknown', highCutHz: 9999 },
    ]);
    expect(getTxStationProfile('essb', profiles).highCutHz).toBe(5400);
    expect(getTxStationProfile('essb', profiles).spectralDensity).toBe(88);
    expect(getTxStationProfile('essb', profiles).audioSuiteProfileName).toBe('ESSB Broadcast');
    expect(getTxStationProfile('dx', profiles).highCutHz).toBe(DX_PROFILE.highCutHz);
  });
});

describe('ESSB TX station profile', () => {
  it('keeps extra headroom for the wider voice curve', () => {
    expect(ESSB_MIC_GAIN_DB).toBe(-4);
    expect(ESSB_LEVELER_MAX_GAIN_DB).toBe(11);
    expect(ESSB_TX_LEVELING).toEqual({
      alcMaxGainDb: 3,
      alcDecayMs: 10,
      levelerEnabled: true,
      levelerDecayMs: 180,
      compressorEnabled: false,
      compressorGainDb: 0,
    });
  });

  it('uses a wider sideband-correct TX filter', () => {
    expect(essbTxFilterForMode('LSB')).toEqual({ lowHz: -5000, highHz: -40 });
    expect(essbTxFilterForMode('DIGL')).toEqual({ lowHz: -5000, highHz: -40 });
    expect(essbTxFilterForMode('USB')).toEqual({ lowHz: 40, highHz: 5000 });
    expect(essbTxFilterForMode('DIGU')).toEqual({ lowHz: 40, highHz: 5000 });
    expect(essbTxFilterForMode('AM')).toEqual({ lowHz: -5000, highHz: 5000 });
  });

  it('uses a thick but bounded max-density CFC curve', () => {
    const resolved = resolveTxStationProfile(ESSB_PROFILE, 'LSB').cfcConfig;
    expect(resolved.enabled).toBe(true);
    expect(resolved.postEqEnabled).toBe(true);
    expect(resolved.bands).toHaveLength(10);
    expect(Math.max(...resolved.bands.map((b) => b.compLevelDb))).toBeLessThanOrEqual(7);
    expect(Math.max(...resolved.bands.map((b) => b.postGainDb))).toBeLessThanOrEqual(3);
    expect(resolved.bands[2]!.postGainDb).toBeGreaterThan(
      STUDIO_SSB_CFC_CONFIG.bands[2]!.postGainDb,
    );
  });

  it('clones the ESSB bands before handing them to UI code', () => {
    const a = cloneEssbCfcConfig();
    const b = cloneEssbCfcConfig();
    a.bands[2]!.postGainDb = 9;
    expect(b.bands[2]!.postGainDb).toBe(ESSB_CFC_CONFIG.bands[2]!.postGainDb);
  });
});

describe('DX TX station profile', () => {
  it('trims bandwidth and raises density for pileups', () => {
    expect(DX_MIC_GAIN_DB).toBe(-2);
    expect(DX_LEVELER_MAX_GAIN_DB).toBe(12);
    expect(DX_TX_LEVELING.levelerDecayMs).toBe(70);
    expect(DX_PROFILE.spectralDensity).toBe(100);
    expect(dxTxFilterForMode('USB')).toEqual({ lowHz: 300, highHz: 2850 });
    expect(dxTxFilterForMode('LSB')).toEqual({ lowHz: -2850, highHz: -300 });
  });

  it('uses a presence-forward CFC curve with low-end cleanup', () => {
    const resolved = resolveTxStationProfile(DX_PROFILE, 'USB').cfcConfig;
    expect(resolved.postEqEnabled).toBe(true);
    expect(resolved.bands[0]!.postGainDb).toBeLessThan(-4);
    expect(resolved.bands[6]!.postGainDb).toBeGreaterThan(3);
    expect(Math.max(...resolved.bands.map((b) => b.compLevelDb))).toBeLessThanOrEqual(8);
  });

  it('clones the DX bands before handing them to UI code', () => {
    const a = cloneDxCfcConfig();
    const b = cloneDxCfcConfig();
    a.bands[6]!.compLevelDb = 12;
    expect(b.bands[6]!.compLevelDb).toBe(DX_CFC_CONFIG.bands[6]!.compLevelDb);
  });
});

describe('spectral density macro', () => {
  it('raises CFC drive and presence when density is maxed', () => {
    const low = applySpectralDensity(STUDIO_SSB_CFC_CONFIG, 25);
    const high = applySpectralDensity(STUDIO_SSB_CFC_CONFIG, 100);
    expect(high.preCompDb).toBeGreaterThan(low.preCompDb);
    expect(high.bands[5]!.compLevelDb).toBeGreaterThan(low.bands[5]!.compLevelDb);
    expect(high.bands[5]!.postGainDb).toBeGreaterThan(low.bands[5]!.postGainDb);
  });
});
