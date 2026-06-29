// SPDX-License-Identifier: GPL-2.0-or-later

/** @vitest-environment jsdom */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import '../components/meters/__tests__/harness';

import type { RadioStateDto, TxAudioProfileDto } from '../api/client';
import * as client from '../api/client';
import { useAudioSuiteStore } from './audio-suite-store';
import { useConnectionStore } from './connection-store';
import { useTxAudioProfileStore } from './tx-audio-profile-store';
import { useTxStore } from './tx-store';

function makeProfile(id: string, name: string, mode: 'native' | 'vst' = 'native'): TxAudioProfileDto {
  return {
    id,
    name,
    micGainDb: 0,
    levelerMaxGainDb: 8,
    txLeveling: {
      alcMaxGainDb: 3,
      alcDecayMs: 10,
      levelerEnabled: true,
      levelerDecayMs: 100,
      compressorEnabled: false,
      compressorGainDb: 0,
    },
    cfcConfig: { enabled: false, postEqEnabled: false, preCompDb: 0, prePeqDb: 0, bands: [] },
    lowCutHz: 150,
    highCutHz: 2850,
    processingMode: mode,
    masterBypass: false,
    chainOrder: [],
    chainParked: [],
    vstPluginStates: {},
    nativePluginStates: {},
    targetSpectralDensity: 55,
    createdUtc: '',
    updatedUtc: '',
  };
}

describe('tx-audio-profile-store', () => {
  beforeEach(() => {
    useTxAudioProfileStore.setState({
      profiles: [],
      loaded: false,
      lastLoadedId: null,
      busy: false,
      dirty: false,
      baseline: null,
      applyRevision: 0,
    });
    vi.restoreAllMocks();
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('load() populates profiles + last-loaded pointer (does not apply)', async () => {
    vi.spyOn(client, 'fetchTxAudioProfiles').mockResolvedValue([
      makeProfile('studio-ssb', 'Studio SSB'),
      makeProfile('dx-punch', 'DX Punch'),
    ]);
    vi.spyOn(client, 'fetchLastLoadedTxAudioProfile').mockResolvedValue({ id: 'studio-ssb' });

    await useTxAudioProfileStore.getState().load();

    const s = useTxAudioProfileStore.getState();
    expect(s.profiles.map((p) => p.id)).toEqual(['studio-ssb', 'dx-punch']);
    expect(s.lastLoadedId).toBe('studio-ssb');
    expect(s.loaded).toBe(true);
  });

  it('save() captures by name and does NOT change the last-loaded selection', async () => {
    useTxAudioProfileStore.setState({ lastLoadedId: 'studio-ssb', loaded: true });
    vi.spyOn(client, 'saveTxAudioProfile').mockResolvedValue(makeProfile('my-voice', 'My Voice'));
    vi.spyOn(client, 'fetchTxAudioProfiles').mockResolvedValue([
      makeProfile('my-voice', 'My Voice'),
      makeProfile('studio-ssb', 'Studio SSB'),
    ]);
    vi.spyOn(client, 'fetchLastLoadedTxAudioProfile').mockResolvedValue({ id: 'studio-ssb' });

    const result = await useTxAudioProfileStore.getState().save('My Voice');

    expect(result.ok).toBe(true);
    // Saving snapshots live state; the selection pointer is unchanged.
    expect(useTxAudioProfileStore.getState().lastLoadedId).toBe('studio-ssb');
    expect(useTxAudioProfileStore.getState().profiles.some((p) => p.id === 'my-voice')).toBe(true);
  });

  it('save() rejects a blank name', async () => {
    const result = await useTxAudioProfileStore.getState().save('   ');
    expect(result.ok).toBe(false);
  });

  it('apply() drives live stores and records last-loaded', async () => {
    const applyStateSpy = vi.spyOn(useConnectionStore.getState(), 'applyState');
    const hydrateSpy = vi.spyOn(useTxStore.getState(), 'hydrateFromState');
    const loadChainSpy = vi
      .spyOn(useAudioSuiteStore.getState(), 'loadChainOrderFromServer')
      .mockResolvedValue();

    const fakeState = { status: 'Connected', mode: 'USB' } as unknown as RadioStateDto;
    vi.spyOn(client, 'applyTxAudioProfile').mockResolvedValue({
      profile: makeProfile('dx-punch', 'DX Punch', 'vst'),
      state: fakeState,
      pluginIds: ['comp', 'eq'],
      parked: ['gate'],
      processingMode: 'vst',
      engineActive: true,
      engineAvailable: true,
      masterBypass: true,
    });

    const result = await useTxAudioProfileStore.getState().apply('dx-punch');

    expect(result.ok).toBe(true);
    expect(applyStateSpy).toHaveBeenCalledWith(fakeState);
    expect(hydrateSpy).toHaveBeenCalledWith(fakeState);
    expect(loadChainSpy).toHaveBeenCalled();
    expect(useTxAudioProfileStore.getState().lastLoadedId).toBe('dx-punch');
    expect(useTxAudioProfileStore.getState().applyRevision).toBe(1);
    // The Audio Suite store reconciled from the apply response.
    const audio = useAudioSuiteStore.getState();
    expect(audio.chainOrder).toEqual(['comp', 'eq']);
    expect(audio.processingMode).toBe('vst');
    expect(audio.masterBypassed).toBe(true);
  });

  it('importProfile() imports then immediately applies the imported profile', async () => {
    const applyStateSpy = vi.spyOn(useConnectionStore.getState(), 'applyState');
    const hydrateSpy = vi.spyOn(useTxStore.getState(), 'hydrateFromState');
    const loadChainSpy = vi
      .spyOn(useAudioSuiteStore.getState(), 'loadChainOrderFromServer')
      .mockResolvedValue();

    const imported = makeProfile('voodoo-4k', 'VooDoo 4K');
    const fakeState = { status: 'Connected', mode: 'USB' } as unknown as RadioStateDto;
    vi.spyOn(client, 'importTxAudioProfile').mockResolvedValue(imported);
    vi.spyOn(client, 'fetchTxAudioProfiles').mockResolvedValue([imported]);
    vi.spyOn(client, 'fetchLastLoadedTxAudioProfile').mockResolvedValue({ id: null });
    vi.spyOn(client, 'applyTxAudioProfile').mockResolvedValue({
      profile: imported,
      state: fakeState,
      pluginIds: ['com.openhpsdr.zeus.samples.eq'],
      parked: [],
      processingMode: 'native',
      engineActive: false,
      engineAvailable: false,
      masterBypass: false,
    });

    const file = new File(['{}'], 'voodoo-4k.json', { type: 'application/json' });
    const result = await useTxAudioProfileStore.getState().importProfile(file);

    expect(result.ok).toBe(true);
    expect(client.importTxAudioProfile).toHaveBeenCalledWith(file, undefined);
    expect(client.applyTxAudioProfile).toHaveBeenCalledWith('voodoo-4k');
    expect(applyStateSpy).toHaveBeenCalledWith(fakeState);
    expect(hydrateSpy).toHaveBeenCalledWith(fakeState);
    expect(loadChainSpy).toHaveBeenCalled();
    expect(useTxAudioProfileStore.getState().lastLoadedId).toBe('voodoo-4k');
    expect(useTxAudioProfileStore.getState().applyRevision).toBe(1);
    expect(useTxAudioProfileStore.getState().dirty).toBe(false);
  });

  it('remove() drops the row and clears a matching selection', async () => {
    useTxAudioProfileStore.setState({
      profiles: [makeProfile('dx-punch', 'DX Punch')],
      lastLoadedId: 'dx-punch',
      loaded: true,
    });
    vi.spyOn(client, 'deleteTxAudioProfile').mockResolvedValue('dx-punch');

    const result = await useTxAudioProfileStore.getState().remove('dx-punch');

    expect(result.ok).toBe(true);
    expect(useTxAudioProfileStore.getState().profiles).toEqual([]);
    expect(useTxAudioProfileStore.getState().lastLoadedId).toBeNull();
  });
});

describe('tx-audio-profile-store — dirty tracking', () => {
  beforeEach(() => {
    useTxAudioProfileStore.setState({
      profiles: [],
      loaded: false,
      lastLoadedId: null,
      busy: false,
      dirty: false,
      baseline: null,
      applyRevision: 0,
    });
    // Deterministic clean live state for the snapshot to read.
    useTxStore.setState({ micGainDb: 0, levelerMaxGainDb: 8 });
    vi.restoreAllMocks();
  });

  it('markClean() captures a baseline and clears dirty', () => {
    useTxAudioProfileStore.getState().markClean();
    const s = useTxAudioProfileStore.getState();
    expect(s.dirty).toBe(false);
    expect(s.baseline).not.toBeNull();
  });

  it('recomputeDirty() flips dirty when a watched setting changes, and clears it when reverted', () => {
    const store = useTxAudioProfileStore.getState();
    store.markClean();

    useTxStore.setState({ micGainDb: -6 });
    store.recomputeDirty();
    expect(useTxAudioProfileStore.getState().dirty).toBe(true);

    useTxStore.setState({ micGainDb: 0 }); // revert -> clean again
    store.recomputeDirty();
    expect(useTxAudioProfileStore.getState().dirty).toBe(false);
  });

  it('recomputeDirty() is a no-op before a baseline exists (pre-connect)', () => {
    useTxAudioProfileStore.setState({ baseline: null, dirty: false });
    useTxStore.setState({ micGainDb: -20 });
    useTxAudioProfileStore.getState().recomputeDirty();
    expect(useTxAudioProfileStore.getState().dirty).toBe(false);
  });

  it('apply() re-baselines so a freshly applied profile is clean', async () => {
    const store = useTxAudioProfileStore.getState();
    store.markClean();
    useTxStore.setState({ micGainDb: -10 });
    store.recomputeDirty();
    expect(useTxAudioProfileStore.getState().dirty).toBe(true);

    vi.spyOn(useConnectionStore.getState(), 'applyState').mockImplementation(() => {});
    vi.spyOn(useTxStore.getState(), 'hydrateFromState').mockImplementation(() => {});
    vi.spyOn(useAudioSuiteStore.getState(), 'loadChainOrderFromServer').mockResolvedValue();
    vi.spyOn(client, 'applyTxAudioProfile').mockResolvedValue({
      profile: makeProfile('dx-punch', 'DX Punch'),
      state: { status: 'Connected' } as unknown as RadioStateDto,
      pluginIds: [], parked: [], processingMode: 'native',
      engineActive: false, engineAvailable: false, masterBypass: false,
    });

    await useTxAudioProfileStore.getState().apply('dx-punch');
    expect(useTxAudioProfileStore.getState().dirty).toBe(false);
  });

  it('save() re-baselines so the just-persisted state is clean', async () => {
    useTxAudioProfileStore.setState({ lastLoadedId: 'studio-ssb' });
    const store = useTxAudioProfileStore.getState();
    store.markClean();
    useTxStore.setState({ micGainDb: 3 });
    store.recomputeDirty();
    expect(useTxAudioProfileStore.getState().dirty).toBe(true);

    vi.spyOn(client, 'saveTxAudioProfile').mockResolvedValue(makeProfile('studio-ssb', 'Studio SSB'));
    vi.spyOn(client, 'fetchTxAudioProfiles').mockResolvedValue([makeProfile('studio-ssb', 'Studio SSB')]);
    vi.spyOn(client, 'fetchLastLoadedTxAudioProfile').mockResolvedValue({ id: 'studio-ssb' });

    await useTxAudioProfileStore.getState().save('Studio SSB');
    expect(useTxAudioProfileStore.getState().dirty).toBe(false);
  });
});
