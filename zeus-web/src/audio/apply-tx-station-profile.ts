// SPDX-License-Identifier: GPL-2.0-or-later

import {
  setCfcConfig,
  setLevelerMaxGain,
  setMicGain,
  setTxFilter,
  setTxLeveling,
  type CfcConfigDto,
  type RadioStateDto,
  type RxMode,
} from '../api/client';
import {
  resolveTxStationProfile,
  type TxStationProfile,
} from './tx-station-profile';

type ApplyAudioProfileResult = {
  ok: boolean;
  error?: string;
};

export type ApplyTxStationProfileDeps = {
  mode: RxMode;
  applyState: (state: RadioStateDto) => void;
  hydrateTxFromState: (state: RadioStateDto) => void;
  setMicGainDb: (db: number) => void;
  setLevelerMaxGainDb: (db: number) => void;
  setCfcConfigLocal: (config: CfcConfigDto) => void;
  applyAudioProfile?: (name: string) => Promise<ApplyAudioProfileResult>;
  signal?: AbortSignal;
};

export async function applyTxStationProfile(
  profile: TxStationProfile,
  deps: ApplyTxStationProfileDeps,
): Promise<void> {
  const resolved = resolveTxStationProfile(profile, deps.mode);
  const { cfcConfig, filter } = resolved;
  const audioProfileName = profile.audioSuiteProfileName?.trim() ?? '';

  if (audioProfileName.length > 0) {
    if (!deps.applyAudioProfile) {
      throw new Error(`Audio Suite profile "${audioProfileName}" cannot be applied here`);
    }
    const result = await deps.applyAudioProfile(audioProfileName);
    if (!result.ok) {
      throw new Error(result.error || `Audio Suite profile "${audioProfileName}" did not apply`);
    }
  }

  const mic = await setMicGain(profile.micGainDb, deps.signal);
  deps.setMicGainDb(mic.micGainDb);

  const leveler = await setLevelerMaxGain(profile.levelerMaxGainDb, deps.signal);
  deps.setLevelerMaxGainDb(leveler.levelerMaxGainDb);

  let state = await setTxLeveling(profile.txLeveling, deps.signal);
  deps.applyState(state);
  deps.hydrateTxFromState(state);

  state = await setTxFilter(filter.lowHz, filter.highHz, deps.signal);
  deps.applyState(state);
  deps.hydrateTxFromState(state);

  state = await setCfcConfig(cfcConfig, deps.signal);
  deps.applyState(state);
  deps.hydrateTxFromState(state);
  deps.setCfcConfigLocal(state.cfc);
}
