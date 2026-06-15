// SPDX-License-Identifier: GPL-2.0-or-later
//
// Dockable TX fidelity panel. The analyzer itself lives in
// components/TxFidelityAdvisor so the same surface can be reused later in
// compact/mobile contexts without depending on workspace chrome.

import { useCallback, useEffect, useRef, useState, type CSSProperties } from 'react';

import {
  fetchTxFidelityPolicy,
  fetchTxStationProfiles,
  resetTxStationProfile,
  saveTxFidelityPolicy,
  saveTxStationProfile,
  setCfcConfig,
  setLevelerMaxGain,
  setMicGain,
  setTxFilter,
  setTxLeveling,
} from '../../api/client';
import {
  formatTxStationProfileSummary,
  getTxStationProfile,
  isTxStationProfileId,
  mergeTxStationProfileOverrides,
  resolveTxStationProfile,
  sanitizeTxStationProfile,
  STUDIO_SSB_PROFILE,
  TX_STATION_PROFILES,
  txStationProfileToDto,
  type TxStationProfile,
  type TxStationProfileId,
} from '../../audio/tx-station-profile';
import { AudioChainMeters } from '../../components/AudioChainMeters';
import { TxFidelityAdvisor } from '../../components/TxFidelityAdvisor';
import { useAudioSuiteStore, type AudioProfileSummary } from '../../state/audio-suite-store';
import { useConnectionStore } from '../../state/connection-store';
import { useTxStore } from '../../state/tx-store';

type ApplyPhase =
  | 'idle'
  | 'pending'
  | 'applying'
  | 'applied'
  | 'saving'
  | 'saved'
  | 'resetting'
  | 'error';

type ActivationReason = 'selected' | 'chain' | 'saved' | 'reset';

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

function chooseSuggestedAudioProfileName(
  profile: TxStationProfile,
  audioProfiles: ReadonlyArray<AudioProfileSummary>,
): string {
  const names = audioProfiles
    .filter((audioProfile) => audioProfile.processingMode === profile.audioSuiteRoute)
    .map((audioProfile) => audioProfile.name.trim())
    .filter(Boolean);
  if (names.length === 0 || profile.audioSuiteRoute !== 'vst' || profile.audioSuiteProfileName?.trim()) {
    return '';
  }

  const keywordsByProfile: Record<TxStationProfileId, string[]> = {
    'studio-ssb': ['studio', 'ssb', 'broadcast'],
    essb: ['essb', 'broadcast', 'wide', 'studio'],
    dx: ['dx', 'punch', 'contest', 'pileup'],
  };
  const keywords = keywordsByProfile[profile.id];
  const lowered = names.map((name) => name.toLowerCase());
  for (const keyword of keywords) {
    const match = lowered.findIndex((name) => name.includes(keyword));
    if (match >= 0) return names[match]!;
  }

  return '';
}

function activationPendingMessage(profile: TxStationProfile, reason: ActivationReason): string {
  const action =
    reason === 'saved'
      ? 'saved'
      : reason === 'reset'
        ? 'reset'
        : reason === 'chain'
          ? 'chain selected'
          : 'selected';
  return `${profile.label} ${action} / connect to apply`;
}

function activationSuccessMessage(profile: TxStationProfile, reason: ActivationReason): string {
  const action =
    reason === 'saved'
      ? 'saved and active'
      : reason === 'reset'
        ? 'reset and active'
        : 'active';
  return `${profile.label} ${action} / ${formatTxStationProfileSummary(profile)}`;
}

type TxStationProfilesProps = {
  selectedProfileId: TxStationProfileId;
  onPolicyChange?: (profileId: TxStationProfileId, spectralDensity: number) => void;
  onTargetSpectralDensityChange?: (density: number) => void;
};

function TxStationProfiles({
  selectedProfileId,
  onPolicyChange,
  onTargetSpectralDensityChange,
}: TxStationProfilesProps) {
  const status = useConnectionStore((s) => s.status);
  const mode = useConnectionStore((s) => s.mode);
  const applyState = useConnectionStore((s) => s.applyState);
  const hydrateTxFromState = useTxStore((s) => s.hydrateFromState);
  const setMicGainDb = useTxStore((s) => s.setMicGainDb);
  const setLevelerMaxGainDb = useTxStore((s) => s.setLevelerMaxGainDb);
  const setCfcConfigLocal = useTxStore((s) => s.setCfcConfig);
  const loadAudioProfiles = useAudioSuiteStore((s) => s.loadProfiles);
  const applyAudioProfile = useAudioSuiteStore((s) => s.applyProfile);
  const audioProfiles = useAudioSuiteStore((s) => s.profiles);
  const [profiles, setProfiles] = useState<TxStationProfile[]>(profileDefaults);
  const [phase, setPhase] = useState<ApplyPhase>('idle');
  const [message, setMessage] = useState(formatTxStationProfileSummary(STUDIO_SSB_PROFILE));
  const [profileMenuOpen, setProfileMenuOpen] = useState(false);
  const [audioProfileMenuOpen, setAudioProfileMenuOpen] = useState(false);
  const pendingActivationRef = useRef<{
    profileId: TxStationProfileId;
    reason: ActivationReason;
  } | null>(null);
  const activationSeqRef = useRef(0);

  const selectedProfile = getTxStationProfile(selectedProfileId, profiles);
  const busy = phase === 'applying' || phase === 'saving' || phase === 'resetting';
  const profileSummary = formatTxStationProfileSummary(selectedProfile);
  const displayedMessage = phase === 'idle' ? profileSummary : message;

  useEffect(() => {
    onTargetSpectralDensityChange?.(selectedProfile.spectralDensity);
  }, [onTargetSpectralDensityChange, selectedProfile.id, selectedProfile.spectralDensity]);

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
  }, [loadAudioProfiles]);

  useEffect(() => {
    if (phase === 'idle') {
      setMessage(formatTxStationProfileSummary(getTxStationProfile(selectedProfileId, profiles)));
    }
  }, [phase, profiles, selectedProfileId]);

  const applyStationProfile = useCallback(
    async (profile: TxStationProfile, reason: ActivationReason) => {
      const seq = ++activationSeqRef.current;

      if (status !== 'Connected') {
        pendingActivationRef.current = { profileId: profile.id, reason };
        setPhase('pending');
        setMessage(activationPendingMessage(profile, reason));
        return;
      }

      const resolved = resolveTxStationProfile(profile, mode);
      const { cfcConfig, filter } = resolved;
      const audioProfileName = profile.audioSuiteProfileName?.trim() ?? '';
      setPhase('applying');
      setMessage('Applying...');
      try {
        if (audioProfileName.length > 0) {
          const result = await applyAudioProfile(audioProfileName);
          if (!result.ok) {
            throw new Error(result.error || `Audio Suite profile "${audioProfileName}" did not apply`);
          }
        }

        const mic = await setMicGain(profile.micGainDb);
        setMicGainDb(mic.micGainDb);

        const leveler = await setLevelerMaxGain(profile.levelerMaxGainDb);
        setLevelerMaxGainDb(leveler.levelerMaxGainDb);

        let state = await setTxLeveling(profile.txLeveling);
        applyState(state);
        hydrateTxFromState(state);

        state = await setTxFilter(filter.lowHz, filter.highHz);
        applyState(state);
        hydrateTxFromState(state);

        state = await setCfcConfig(cfcConfig);
        applyState(state);
        hydrateTxFromState(state);
        setCfcConfigLocal(state.cfc);

        if (seq !== activationSeqRef.current) return;
        setPhase('applied');
        setMessage(activationSuccessMessage(profile, reason));
      } catch (err) {
        if (seq !== activationSeqRef.current) return;
        setPhase('error');
        setMessage(err instanceof Error ? err.message : 'Apply failed');
      }
    },
    [
      applyAudioProfile,
      applyState,
      hydrateTxFromState,
      mode,
      setCfcConfigLocal,
      setLevelerMaxGainDb,
      setMicGainDb,
      status,
    ],
  );

  const activateStationProfile = useCallback(
    (profile: TxStationProfile, reason: ActivationReason) => {
      pendingActivationRef.current = null;
      void applyStationProfile(profile, reason);
    },
    [applyStationProfile],
  );

  useEffect(() => {
    if (status !== 'Connected') return;
    const pending = pendingActivationRef.current;
    if (!pending) return;
    pendingActivationRef.current = null;
    const profile = getTxStationProfile(pending.profileId, profiles);
    void applyStationProfile(profile, pending.reason);
  }, [applyStationProfile, profiles, status]);

  function selectProfile(profileId: string) {
    const profile = getTxStationProfile(profileId, profiles);
    onPolicyChange?.(profile.id, profile.spectralDensity);
    setProfileMenuOpen(false);
    setAudioProfileMenuOpen(false);
    activateStationProfile(profile, 'selected');
  }

  function selectAudioProfile(name: string) {
    const savedProfile = audioProfiles.find((profile) => profile.name === name);
    const next = {
      ...selectedProfile,
      audioSuiteProfileName: name,
      ...(savedProfile
        ? {
            audioSuiteRoute: savedProfile.processingMode,
            audioSuiteBypassed: savedProfile.masterBypass,
          }
        : {}),
    };
    updateSelectedProfile(next);
    setAudioProfileMenuOpen(false);
    activateStationProfile(next, 'chain');
  }

  function bindSuggestedAudioProfile(name: string) {
    const savedProfile = audioProfiles.find((profile) => profile.name === name);
    const next = {
      ...selectedProfile,
      audioSuiteRoute: savedProfile?.processingMode ?? 'vst',
      audioSuiteBypassed: savedProfile?.masterBypass ?? false,
      audioSuiteProfileName: name,
    };
    updateSelectedProfile(next);
    setAudioProfileMenuOpen(false);
    activateStationProfile(next, 'chain');
  }

  function updateSelectedProfile(next: TxStationProfile) {
    const fallback = getTxStationProfile(next.id, TX_STATION_PROFILES);
    const sanitized = sanitizeTxStationProfile(txStationProfileToDto(next), fallback);
    setProfiles((prev) => prev.map((profile) => (profile.id === sanitized.id ? sanitized : profile)));
    setPhase('idle');
    setMessage(formatTxStationProfileSummary(sanitized));
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
      if (sanitized.id === selectedProfileId) {
        onPolicyChange?.(sanitized.id, sanitized.spectralDensity);
      }
      activateStationProfile(sanitized, 'saved');
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
      onPolicyChange?.(restored.id, restored.spectralDensity);
      activateStationProfile(restored, 'reset');
    } catch (err) {
      setPhase('error');
      setMessage(err instanceof Error ? err.message : 'Reset failed');
    }
  }

  const audioProfileNames = audioProfiles.map((profile) => profile.name);
  const selectedAudioProfileName = selectedProfile.audioSuiteProfileName?.trim() ?? '';
  const selectedAudioProfileMissing =
    selectedAudioProfileName.length > 0 &&
    !audioProfileNames.includes(selectedAudioProfileName);
  const selectedAudioProfile = selectedAudioProfileName
    ? audioProfiles.find((profile) => profile.name === selectedAudioProfileName)
    : undefined;
  const suggestedAudioProfileName = chooseSuggestedAudioProfileName(
    selectedProfile,
    audioProfiles,
  );
  const audioProfileNote = selectedAudioProfile
    ? `${selectedAudioProfile.processingMode === 'vst' ? 'VST' : 'Native'} route / ${
        selectedAudioProfile.masterBypass ? 'rack bypass' : 'rack hot'
      } saved in chain profile`
    : 'No chain profile selected; current Audio Suite chain stays active';

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
          gridTemplateColumns: 'minmax(0, 1fr)',
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
              title={selectedProfile.applyTitle}
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
        title={displayedMessage}
      >
        {displayedMessage}
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
            <label style={controlLabelStyle()}>
              Chain Profile
              <div style={{ position: 'relative', minWidth: 0 }}>
                <button
                  type="button"
                  aria-label="TX profile audio suite profile"
                  aria-haspopup="listbox"
                  aria-expanded={audioProfileMenuOpen}
                  disabled={busy}
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
                    cursor: busy ? 'not-allowed' : 'pointer',
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
                    {selectedAudioProfileName || 'Use current chain'}
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
                      Use current chain
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

            {suggestedAudioProfileName && (
              <div
                style={{
                  display: 'grid',
                  gridTemplateColumns: 'minmax(0, 1fr) auto',
                  gap: 6,
                  alignItems: 'center',
                  minWidth: 0,
                  padding: '5px 6px',
                  border: '1px solid var(--accent-soft)',
                  borderRadius: 4,
                  background: 'var(--bg-2)',
                }}
              >
                <span
                  className="mono"
                  style={{
                    minWidth: 0,
                    color: 'var(--fg-2)',
                    fontSize: 9.5,
                    overflow: 'hidden',
                    textOverflow: 'ellipsis',
                    whiteSpace: 'nowrap',
                  }}
                  title={`Suggested Audio Suite chain: ${suggestedAudioProfileName}`}
                >
                  Suggested chain: {suggestedAudioProfileName}
                </span>
                <button
                  type="button"
                  disabled={busy}
                  onClick={() => bindSuggestedAudioProfile(suggestedAudioProfileName)}
                  style={{
                    height: 23,
                    border: '1px solid var(--accent)',
                    borderRadius: 4,
                    background: 'var(--bg-1)',
                    color: 'var(--fg-0)',
                    cursor: busy ? 'not-allowed' : 'pointer',
                    fontSize: 10,
                    fontWeight: 900,
                    padding: '0 8px',
                    textTransform: 'uppercase',
                  }}
                >
                  Bind
                </button>
              </div>
            )}

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
                  : audioProfileNote
              }
            >
              {selectedAudioProfileMissing
                ? `Missing chain profile: ${selectedAudioProfileName}`
                : audioProfileNote}
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
  const policyTouchedRef = useRef(false);
  const [selectedProfileId, setSelectedProfileId] = useState<TxStationProfileId>(
    STUDIO_SSB_PROFILE.id,
  );
  const [targetSpectralDensity, setTargetSpectralDensity] = useState(
    STUDIO_SSB_PROFILE.spectralDensity,
  );

  useEffect(() => {
    let active = true;
    fetchTxFidelityPolicy()
      .then((policy) => {
        if (!active || policyTouchedRef.current) return;
        const profileId = isTxStationProfileId(policy.profileId)
          ? policy.profileId
          : STUDIO_SSB_PROFILE.id;
        setSelectedProfileId(profileId);
        setTargetSpectralDensity(policy.targetSpectralDensity);
      })
      .catch(() => {
        /* Keep the built-in Studio SSB default when the policy is unavailable. */
      });
    return () => {
      active = false;
    };
  }, []);

  const updatePolicy = useCallback((profileId: TxStationProfileId, spectralDensity: number) => {
    policyTouchedRef.current = true;
    setSelectedProfileId(profileId);
    setTargetSpectralDensity(spectralDensity);
    void saveTxFidelityPolicy({
      profileId,
      targetSpectralDensity: spectralDensity,
    }).catch(() => {
      /* The advisor stays usable even if the preference write fails. */
    });
  }, []);

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
      <TxFidelityAdvisor targetSpectralDensity={targetSpectralDensity} />
      <AudioChainMeters compact title="Audio Suite Chain" />
      <TxStationProfiles
        selectedProfileId={selectedProfileId}
        onPolicyChange={updatePolicy}
        onTargetSpectralDensityChange={setTargetSpectralDensity}
      />
    </div>
  );
}
