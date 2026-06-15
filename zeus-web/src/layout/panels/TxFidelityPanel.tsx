// SPDX-License-Identifier: GPL-2.0-or-later
//
// Dockable TX fidelity panel. The analyzer itself lives in
// components/TxFidelityAdvisor so the same surface can be reused later in
// compact/mobile contexts without depending on workspace chrome.

import { useEffect, useState, type CSSProperties } from 'react';

import {
  fetchTxStationProfiles,
  resetTxStationProfile,
  saveTxStationProfile,
  setCfcConfig,
  setLevelerMaxGain,
  setMicGain,
  setTxFilter,
  setTxLeveling,
} from '../../api/client';
import {
  getTxStationProfile,
  mergeTxStationProfileOverrides,
  resolveTxStationProfile,
  sanitizeTxStationProfile,
  STUDIO_SSB_PROFILE,
  TX_STATION_PROFILES,
  txStationProfileToDto,
  type TxStationProfile,
  type TxStationProfileId,
} from '../../audio/tx-station-profile';
import { TxFidelityAdvisor } from '../../components/TxFidelityAdvisor';
import { useAudioSuiteStore } from '../../state/audio-suite-store';
import { useConnectionStore } from '../../state/connection-store';
import { useTxStore } from '../../state/tx-store';

type ApplyPhase = 'idle' | 'applying' | 'applied' | 'saving' | 'saved' | 'resetting' | 'error';

function controlInputStyle(): CSSProperties {
  return {
    width: '100%',
    minWidth: 0,
    height: 26,
    boxSizing: 'border-box',
    border: '1px solid var(--line)',
    borderRadius: 4,
    background: 'var(--bg-0)',
    color: 'var(--fg-0)',
    fontSize: 11,
    fontWeight: 800,
    padding: '0 6px',
  };
}

function controlLabelStyle(): CSSProperties {
  return {
    display: 'grid',
    gap: 3,
    minWidth: 0,
    color: 'var(--fg-2)',
    fontSize: 9,
    fontWeight: 800,
    letterSpacing: '0.08em',
    textTransform: 'uppercase',
  };
}

function miniToggleStyle(active: boolean): CSSProperties {
  return {
    minWidth: 0,
    height: 24,
    border: active ? '1px solid var(--accent)' : '1px solid var(--line)',
    borderRadius: 4,
    background: active ? 'var(--accent)' : 'var(--bg-1)',
    color: active ? '#fff' : 'var(--fg-1)',
    cursor: 'pointer',
    fontSize: 10,
    fontWeight: 900,
    overflow: 'hidden',
    padding: '0 7px',
    textOverflow: 'ellipsis',
    textTransform: 'uppercase',
    whiteSpace: 'nowrap',
  };
}

function miniMenuOptionStyle(active: boolean): CSSProperties {
  return {
    width: '100%',
    minWidth: 0,
    border: '1px solid ' + (active ? 'var(--accent)' : 'transparent'),
    borderRadius: 3,
    background: active ? 'var(--accent-soft)' : 'transparent',
    color: active ? 'var(--fg-0)' : 'var(--fg-1)',
    cursor: 'pointer',
    fontSize: 10,
    fontWeight: 900,
    overflow: 'hidden',
    padding: '6px 8px',
    textAlign: 'left',
    textOverflow: 'ellipsis',
    textTransform: 'uppercase',
    whiteSpace: 'nowrap',
  };
}

function profileDefaults(): TxStationProfile[] {
  return mergeTxStationProfileOverrides([]);
}

function TxStationProfiles() {
  const status = useConnectionStore((s) => s.status);
  const mode = useConnectionStore((s) => s.mode);
  const applyState = useConnectionStore((s) => s.applyState);
  const hydrateTxFromState = useTxStore((s) => s.hydrateFromState);
  const setMicGainDb = useTxStore((s) => s.setMicGainDb);
  const setLevelerMaxGainDb = useTxStore((s) => s.setLevelerMaxGainDb);
  const setCfcConfigLocal = useTxStore((s) => s.setCfcConfig);
  const setMasterBypassed = useAudioSuiteStore((s) => s.setMasterBypassed);
  const setProcessingMode = useAudioSuiteStore((s) => s.setProcessingMode);
  const loadAudioProfiles = useAudioSuiteStore((s) => s.loadProfiles);
  const applyAudioProfile = useAudioSuiteStore((s) => s.applyProfile);
  const loadProcessingMode = useAudioSuiteStore((s) => s.loadProcessingModeFromServer);
  const audioProfiles = useAudioSuiteStore((s) => s.profiles);
  const vstEngineAvailable = useAudioSuiteStore((s) => s.vstEngineAvailable);
  const vstEngineActive = useAudioSuiteStore((s) => s.vstEngineActive);
  const [profiles, setProfiles] = useState<TxStationProfile[]>(profileDefaults);
  const [selectedProfileId, setSelectedProfileId] =
    useState<TxStationProfileId>(STUDIO_SSB_PROFILE.id);
  const [phase, setPhase] = useState<ApplyPhase>('idle');
  const [message, setMessage] = useState(STUDIO_SSB_PROFILE.summary);
  const [profileMenuOpen, setProfileMenuOpen] = useState(false);
  const [audioProfileMenuOpen, setAudioProfileMenuOpen] = useState(false);

  const selectedProfile = getTxStationProfile(selectedProfileId, profiles);
  const busy = phase === 'applying' || phase === 'saving' || phase === 'resetting';
  const applyDisabled = status !== 'Connected' || busy;

  useEffect(() => {
    let active = true;
    fetchTxStationProfiles()
      .then((overrides) => {
        if (!active) return;
        setProfiles(mergeTxStationProfileOverrides(overrides));
      })
      .catch((err) => {
        if (!active) return;
        setPhase('error');
        setMessage(err instanceof Error ? err.message : 'Profile load failed');
      });
    return () => {
      active = false;
    };
  }, []);

  useEffect(() => {
    void loadAudioProfiles();
    void loadProcessingMode();
  }, [loadAudioProfiles, loadProcessingMode]);

  useEffect(() => {
    if (phase === 'idle') {
      setMessage(getTxStationProfile(selectedProfileId, profiles).summary);
    }
  }, [phase, profiles, selectedProfileId]);

  function selectProfile(profileId: string) {
    const profile = getTxStationProfile(profileId, profiles);
    setSelectedProfileId(profile.id);
    setProfileMenuOpen(false);
    setAudioProfileMenuOpen(false);
    setPhase('idle');
    setMessage(profile.summary);
  }

  function selectAudioProfile(name: string) {
    patchSelectedProfile({ audioSuiteProfileName: name });
    setAudioProfileMenuOpen(false);
  }

  function updateSelectedProfile(next: TxStationProfile) {
    const fallback = getTxStationProfile(next.id, TX_STATION_PROFILES);
    const sanitized = sanitizeTxStationProfile(txStationProfileToDto(next), fallback);
    setProfiles((prev) => prev.map((profile) => (profile.id === sanitized.id ? sanitized : profile)));
    setPhase('idle');
    setMessage(sanitized.summary);
  }

  function patchSelectedProfile(patch: Partial<TxStationProfile>) {
    updateSelectedProfile({ ...selectedProfile, ...patch });
  }

  function patchLeveling(patch: Partial<TxStationProfile['txLeveling']>) {
    patchSelectedProfile({
      txLeveling: {
        ...selectedProfile.txLeveling,
        ...patch,
      },
    });
  }

  async function saveSelectedProfile() {
    setPhase('saving');
    setMessage('Saving...');
    try {
      const saved = await saveTxStationProfile(txStationProfileToDto(selectedProfile));
      const fallback = getTxStationProfile(saved.id, TX_STATION_PROFILES);
      const sanitized = sanitizeTxStationProfile(saved, fallback);
      setProfiles((prev) => prev.map((profile) => (profile.id === sanitized.id ? sanitized : profile)));
      setPhase('saved');
      setMessage(`${sanitized.label} saved / density ${sanitized.spectralDensity}`);
    } catch (err) {
      setPhase('error');
      setMessage(err instanceof Error ? err.message : 'Save failed');
    }
  }

  async function resetSelectedProfile() {
    setPhase('resetting');
    setMessage('Resetting...');
    try {
      await resetTxStationProfile(selectedProfile.id);
      const defaults = profileDefaults();
      const restored = getTxStationProfile(selectedProfile.id, defaults);
      setProfiles((prev) => prev.map((profile) => (profile.id === restored.id ? restored : profile)));
      setPhase('idle');
      setMessage(restored.summary);
    } catch (err) {
      setPhase('error');
      setMessage(err instanceof Error ? err.message : 'Reset failed');
    }
  }

  async function applyStationProfile() {
    const resolved = resolveTxStationProfile(selectedProfile, mode);
    const { cfcConfig, filter } = resolved;
    const audioProfileName = selectedProfile.audioSuiteProfileName?.trim() ?? '';
    setPhase('applying');
    setMessage('Applying...');
    try {
      await setProcessingMode(selectedProfile.audioSuiteRoute);
      if (audioProfileName.length > 0) {
        await applyAudioProfile(audioProfileName);
      }
      await setMasterBypassed(selectedProfile.audioSuiteBypassed);

      const mic = await setMicGain(selectedProfile.micGainDb);
      setMicGainDb(mic.micGainDb);

      const leveler = await setLevelerMaxGain(selectedProfile.levelerMaxGainDb);
      setLevelerMaxGainDb(leveler.levelerMaxGainDb);

      let state = await setTxLeveling(selectedProfile.txLeveling);
      applyState(state);
      hydrateTxFromState(state);

      state = await setTxFilter(filter.lowHz, filter.highHz);
      applyState(state);
      hydrateTxFromState(state);

      state = await setCfcConfig(cfcConfig);
      applyState(state);
      hydrateTxFromState(state);
      setCfcConfigLocal(state.cfc);

      setPhase('applied');
      setMessage(
        `${selectedProfile.label} / ${selectedProfile.audioSuiteRoute.toUpperCase()} ${selectedProfile.audioSuiteBypassed ? 'BYP' : 'HOT'} / D${selectedProfile.spectralDensity} / ${filter.lowHz}..${filter.highHz} Hz`,
      );
    } catch (err) {
      setPhase('error');
      setMessage(err instanceof Error ? err.message : 'Apply failed');
    }
  }

  const audioProfileNames = audioProfiles.map((profile) => profile.name);
  const selectedAudioProfileName = selectedProfile.audioSuiteProfileName?.trim() ?? '';
  const selectedAudioProfileMissing =
    selectedAudioProfileName.length > 0 &&
    !audioProfileNames.includes(selectedAudioProfileName);
  const vstRouteNote =
    selectedProfile.audioSuiteRoute === 'vst'
      ? vstEngineActive
        ? 'VST engine active'
        : vstEngineAvailable
          ? 'VST engine installed'
          : 'VST engine unavailable; native chain remains the fallback'
      : 'Native in-process chain';

  return (
    <section
      aria-label="TX station profile"
      style={{
        display: 'grid',
        gap: 7,
        padding: '8px 10px',
        minWidth: 0,
        maxWidth: '100%',
        boxSizing: 'border-box',
        border: '1px solid var(--line)',
        borderRadius: 6,
        background: 'linear-gradient(180deg, var(--panel-top), var(--panel-bot))',
      }}
    >
      <div
        style={{
          display: 'grid',
          gridTemplateColumns: 'minmax(0, 1fr) auto',
          gap: 7,
          alignItems: 'end',
          minWidth: 0,
        }}
      >
        <div style={controlLabelStyle()}>
          Station Profile
          <div style={{ position: 'relative', minWidth: 0 }}>
            <button
              type="button"
            aria-label="TX station profile"
              aria-haspopup="listbox"
              aria-expanded={profileMenuOpen}
            disabled={busy}
              onClick={() => setProfileMenuOpen((open) => !open)}
            style={{
                display: 'grid',
                gridTemplateColumns: 'minmax(0, 1fr) auto',
                alignItems: 'center',
                gap: 6,
              width: '100%',
              minWidth: 0,
              height: 28,
                boxSizing: 'border-box',
                border: '1px solid var(--accent)',
                borderRadius: 4,
                background: 'var(--bg-1)',
                color: 'var(--fg-0)',
                cursor: busy ? 'not-allowed' : 'pointer',
              fontSize: 11,
              fontWeight: 900,
                padding: '0 8px',
              textTransform: 'uppercase',
            }}
          >
              <span
                className="mono"
                style={{ minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}
              >
                {selectedProfile.label}
              </span>
              <span aria-hidden style={{ color: 'var(--fg-2)', fontSize: 10 }}>
                {profileMenuOpen ? '^' : 'v'}
              </span>
            </button>
            {profileMenuOpen && (
              <div
                role="listbox"
                aria-label="TX station profile options"
                style={{
                  position: 'absolute',
                  zIndex: 20,
                  top: 31,
                  left: 0,
                  right: 0,
                  display: 'grid',
                  gap: 2,
                  padding: 4,
                  border: '1px solid var(--accent)',
                  borderRadius: 4,
                  background: 'var(--bg-0)',
                  boxShadow: '0 10px 20px rgba(0, 0, 0, 0.45)',
                }}
              >
                {profiles.map((profile) => {
                  const active = profile.id === selectedProfile.id;
                  return (
                    <button
                      key={profile.id}
                      type="button"
                      role="option"
                      aria-selected={active}
                      onClick={() => selectProfile(profile.id)}
                      style={miniMenuOptionStyle(active)}
                    >
                      {profile.label}
                    </button>
                  );
                })}
              </div>
            )}
          </div>
        </div>
        <button
          type="button"
          disabled={applyDisabled}
          onClick={applyStationProfile}
          title={selectedProfile.applyTitle}
          style={{
            minWidth: 62,
            height: 28,
            border: '1px solid var(--accent)',
            borderRadius: 4,
            background:
              phase === 'applied'
                ? 'var(--signal)'
                : phase === 'error'
                  ? 'var(--tx)'
                  : 'var(--bg-2)',
            color: phase === 'applied' || phase === 'error' ? '#fff' : 'var(--fg-0)',
            cursor: applyDisabled ? 'not-allowed' : 'pointer',
            fontSize: 11,
            fontWeight: 900,
            padding: '0 10px',
            textTransform: 'uppercase',
          }}
        >
          {phase === 'applying' ? 'Applying' : phase === 'applied' ? 'Applied' : 'Apply'}
        </button>
      </div>

      <div
        className="mono"
        style={{
          minWidth: 0,
          color: phase === 'error' ? 'var(--tx)' : 'var(--fg-2)',
          fontSize: 10,
          overflow: 'hidden',
          textOverflow: 'ellipsis',
          whiteSpace: 'nowrap',
        }}
      >
        {message}
      </div>

      <details
        style={{
          minWidth: 0,
          border: '1px solid var(--line)',
          borderRadius: 4,
          background: 'var(--bg-2)',
        }}
      >
        <summary
          className="label-xs"
          style={{
            color: 'var(--fg-2)',
            cursor: 'pointer',
            fontWeight: 800,
            listStyle: 'none',
            padding: '6px 8px',
          }}
        >
          Edit Profile
        </summary>
        <div style={{ display: 'grid', gap: 7, minWidth: 0, padding: '0 8px 8px' }}>
          <div
            style={{
              display: 'grid',
              gap: 7,
              minWidth: 0,
              padding: '7px 8px',
              border: '1px solid var(--line)',
              borderRadius: 4,
              background: 'var(--bg-1)',
            }}
          >
            <div
              style={{
                display: 'grid',
                gridTemplateColumns: 'minmax(0, 1fr) minmax(0, 1fr)',
                gap: 6,
                minWidth: 0,
              }}
            >
              <label style={controlLabelStyle()}>
                Route
                <span
                  role="group"
                  aria-label="TX profile audio route"
                  style={{
                    display: 'grid',
                    gridTemplateColumns: '1fr 1fr',
                    gap: 4,
                    minWidth: 0,
                  }}
                >
                  <button
                    type="button"
                    aria-pressed={selectedProfile.audioSuiteRoute === 'native'}
                    onClick={() => patchSelectedProfile({ audioSuiteRoute: 'native' })}
                    style={miniToggleStyle(selectedProfile.audioSuiteRoute === 'native')}
                  >
                    Native
                  </button>
                  <button
                    type="button"
                    aria-pressed={selectedProfile.audioSuiteRoute === 'vst'}
                    onClick={() => patchSelectedProfile({ audioSuiteRoute: 'vst' })}
                    style={miniToggleStyle(selectedProfile.audioSuiteRoute === 'vst')}
                  >
                    VST
                  </button>
                </span>
              </label>

              <label style={controlLabelStyle()}>
                Rack
                <span
                  role="group"
                  aria-label="TX profile audio suite rack"
                  style={{
                    display: 'grid',
                    gridTemplateColumns: '1fr 1fr',
                    gap: 4,
                    minWidth: 0,
                  }}
                >
                  <button
                    type="button"
                    aria-pressed={!selectedProfile.audioSuiteBypassed}
                    onClick={() => patchSelectedProfile({ audioSuiteBypassed: false })}
                    style={miniToggleStyle(!selectedProfile.audioSuiteBypassed)}
                  >
                    Hot
                  </button>
                  <button
                    type="button"
                    aria-pressed={selectedProfile.audioSuiteBypassed}
                    onClick={() => patchSelectedProfile({ audioSuiteBypassed: true })}
                    style={miniToggleStyle(selectedProfile.audioSuiteBypassed)}
                  >
                    Byp
                  </button>
                </span>
              </label>
            </div>

            <label style={controlLabelStyle()}>
              Chain Profile
              <div style={{ position: 'relative', minWidth: 0 }}>
                <button
                  type="button"
                  aria-label="TX profile audio suite profile"
                  aria-haspopup="listbox"
                  aria-expanded={audioProfileMenuOpen}
                  onClick={() => setAudioProfileMenuOpen((open) => !open)}
                  style={{
                    display: 'grid',
                    gridTemplateColumns: 'minmax(0, 1fr) auto',
                    alignItems: 'center',
                    gap: 6,
                    width: '100%',
                    minWidth: 0,
                    height: 26,
                    boxSizing: 'border-box',
                    border: '1px solid var(--line)',
                    borderRadius: 4,
                    background: 'var(--bg-0)',
                    color: 'var(--fg-0)',
                    cursor: 'pointer',
                    fontSize: 10,
                    fontWeight: 800,
                    padding: '0 7px',
                  }}
                >
                  <span
                    className="mono"
                    style={{
                      minWidth: 0,
                      overflow: 'hidden',
                      textOverflow: 'ellipsis',
                      whiteSpace: 'nowrap',
                    }}
                  >
                    {selectedAudioProfileName || 'Use current rack'}
                  </span>
                  <span aria-hidden style={{ color: 'var(--fg-2)', fontSize: 10 }}>
                    {audioProfileMenuOpen ? '^' : 'v'}
                  </span>
                </button>
                {audioProfileMenuOpen && (
                  <div
                    role="listbox"
                    aria-label="TX profile audio suite profile options"
                    style={{
                      position: 'absolute',
                      zIndex: 21,
                      top: 29,
                      left: 0,
                      right: 0,
                      display: 'grid',
                      gap: 2,
                      maxHeight: 140,
                      overflowY: 'auto',
                      padding: 4,
                      border: '1px solid var(--accent)',
                      borderRadius: 4,
                      background: 'var(--bg-0)',
                      boxShadow: '0 10px 20px rgba(0, 0, 0, 0.45)',
                    }}
                  >
                    <button
                      type="button"
                      role="option"
                      aria-selected={!selectedAudioProfileName}
                      onClick={() => selectAudioProfile('')}
                      style={miniMenuOptionStyle(!selectedAudioProfileName)}
                    >
                      Use current rack
                    </button>
                    {selectedAudioProfileMissing && (
                      <button
                        type="button"
                        role="option"
                        aria-selected
                        onClick={() => selectAudioProfile(selectedAudioProfileName)}
                        style={miniMenuOptionStyle(true)}
                      >
                        {selectedAudioProfileName}
                      </button>
                    )}
                    {audioProfiles.map((profile) => (
                      <button
                        key={profile.name}
                        type="button"
                        role="option"
                        aria-selected={profile.name === selectedAudioProfileName}
                        onClick={() => selectAudioProfile(profile.name)}
                        style={miniMenuOptionStyle(profile.name === selectedAudioProfileName)}
                      >
                        {profile.name}
                      </button>
                    ))}
                  </div>
                )}
              </div>
            </label>

            <div
              className="mono"
              style={{
                color: selectedAudioProfileMissing ? 'var(--power)' : 'var(--fg-3)',
                fontSize: 9.5,
                overflow: 'hidden',
                textOverflow: 'ellipsis',
                whiteSpace: 'nowrap',
              }}
              title={
                selectedAudioProfileMissing
                  ? `Audio Suite profile "${selectedAudioProfileName}" is not currently saved.`
                  : vstRouteNote
              }
            >
              {selectedAudioProfileMissing
                ? `Missing chain profile: ${selectedAudioProfileName}`
                : vstRouteNote}
            </div>
          </div>

          <div
            style={{
              display: 'grid',
              gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
              gap: 7,
              alignItems: 'end',
              minWidth: 0,
            }}
          >
            <label style={{ ...controlLabelStyle(), gridColumn: '1 / -1', minWidth: 0 }}>
              Density
              <span style={{ display: 'flex', gap: 6, alignItems: 'center' }}>
                <input
                  aria-label="TX spectral density"
                  type="range"
                  min={0}
                  max={100}
                  step={1}
                  value={selectedProfile.spectralDensity}
                  onChange={(e) => patchSelectedProfile({ spectralDensity: Number(e.target.value) })}
                  style={{ flex: 1, minWidth: 0 }}
                />
                <button
                  type="button"
                  onClick={() => patchSelectedProfile({ spectralDensity: 100 })}
                  title="Max spectral density"
                  style={{
                    height: 24,
                    border: '1px solid var(--line)',
                    borderRadius: 4,
                    background: selectedProfile.spectralDensity >= 100 ? 'var(--accent)' : 'var(--bg-2)',
                    color: selectedProfile.spectralDensity >= 100 ? '#fff' : 'var(--fg-0)',
                    fontSize: 10,
                    fontWeight: 900,
                    padding: '0 7px',
                  }}
                >
                  Max
                </button>
                <span className="mono" style={{ width: 24, textAlign: 'right', color: 'var(--fg-0)' }}>
                  {selectedProfile.spectralDensity}
                </span>
              </span>
            </label>

            <label style={controlLabelStyle()}>
              Mic
              <input
                aria-label="TX profile mic gain"
                type="number"
                step={0.5}
                min={-40}
                max={10}
                value={selectedProfile.micGainDb}
                onChange={(e) => patchSelectedProfile({ micGainDb: Number(e.target.value) })}
                style={controlInputStyle()}
              />
            </label>
            <label style={controlLabelStyle()}>
              Leveler
              <input
                aria-label="TX profile leveler max gain"
                type="number"
                step={0.5}
                min={0}
                max={20}
                value={selectedProfile.levelerMaxGainDb}
                onChange={(e) => patchSelectedProfile({ levelerMaxGainDb: Number(e.target.value) })}
                style={controlInputStyle()}
              />
            </label>
            <label style={controlLabelStyle()}>
              Decay
              <input
                aria-label="TX profile leveler decay"
                type="number"
                step={5}
                min={1}
                max={5000}
                value={selectedProfile.txLeveling.levelerDecayMs}
                onChange={(e) => patchLeveling({ levelerDecayMs: Number(e.target.value) })}
                style={controlInputStyle()}
              />
            </label>
            <label style={controlLabelStyle()}>
              Low
              <input
                aria-label="TX profile low cut"
                type="number"
                step={10}
                min={20}
                max={600}
                value={selectedProfile.lowCutHz}
                onChange={(e) => patchSelectedProfile({ lowCutHz: Number(e.target.value) })}
                style={controlInputStyle()}
              />
            </label>
            <label style={controlLabelStyle()}>
              High
              <input
                aria-label="TX profile high cut"
                type="number"
                step={50}
                min={1500}
                max={6000}
                value={selectedProfile.highCutHz}
                onChange={(e) => patchSelectedProfile({ highCutHz: Number(e.target.value) })}
                style={controlInputStyle()}
              />
            </label>
          </div>
          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 6 }}>
            <button
              type="button"
              disabled={busy}
              onClick={saveSelectedProfile}
              style={{
                border: '1px solid var(--line)',
                borderRadius: 4,
                background: phase === 'saved' ? 'var(--signal)' : 'var(--bg-2)',
                color: phase === 'saved' ? '#fff' : 'var(--fg-0)',
                cursor: busy ? 'not-allowed' : 'pointer',
                fontSize: 10,
                fontWeight: 800,
                padding: '5px 7px',
                textTransform: 'uppercase',
              }}
            >
              {phase === 'saving' ? 'Saving' : 'Save'}
            </button>
            <button
              type="button"
              disabled={busy}
              onClick={resetSelectedProfile}
              style={{
                border: '1px solid var(--line)',
                borderRadius: 4,
                background: 'var(--bg-2)',
                color: 'var(--fg-1)',
                cursor: busy ? 'not-allowed' : 'pointer',
                fontSize: 10,
                fontWeight: 800,
                padding: '5px 7px',
                textTransform: 'uppercase',
              }}
            >
              {phase === 'resetting' ? 'Resetting' : 'Reset'}
            </button>
          </div>
        </div>
      </details>
    </section>
  );
}

export function TxFidelityPanel() {
  return (
    <div
      style={{
        height: '100%',
        boxSizing: 'border-box',
        display: 'grid',
        alignContent: 'start',
        gap: 8,
        minHeight: 0,
        overflowY: 'auto',
        overflowX: 'hidden',
        padding: 10,
        background: 'var(--bg-1)',
      }}
    >
      <TxFidelityAdvisor />
      <TxStationProfiles />
    </div>
  );
}
