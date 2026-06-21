// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus - OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.

import { useEffect, useRef } from 'react';
import {
  fetchDisplayIntelligenceSettings,
  saveDisplayIntelligenceSettings,
} from '../api/display-intelligence';
import {
  getNoiseFloor,
  getSignalConfidence,
  getSignalStationarity,
  recommendSignalEnhanceScene,
  registerEstimatorConsumer,
  signalEnhanceSettingsFromState,
  useSignalEnhanceStore,
  type SignalEnhanceScene,
  type SignalEnhancePersisted,
  type SignalEnhancePresetId,
} from '../dsp/signal-estimator';
import {
  createAutoNotchTracker,
  detectAutoNotches,
  explainAutoNotchAt,
  explainAutoNotchRejections,
} from '../dsp/auto-notch';
import { DIGITAL_PROTECTED_RANGES } from '../dsp/digital-segments';
import { useConnectionStore } from '../state/connection-store';
import { registerFrameConsumer, useDisplayStore } from '../state/display-store';
import { useNotchStore } from '../state/notch-store';
import { useTxStore } from '../state/tx-store';

const SCENE_SAMPLE_INTERVAL_MS = 1000;
const SCENE_DWELL_SAMPLES = 3;
const SETTINGS_SAVE_DEBOUNCE_MS = 350;
const EMPTY_AUTO_NOTCH_SIGNATURE = '[]';

function sceneReason(scene: SignalEnhanceScene): string {
  if (scene.profileId === 'contest') return 'crowded span';
  if (scene.profileId === 'dx') return 'sparse weak signal';
  if (scene.baseProfileId === 'cw') return 'CW mode';
  if (scene.baseProfileId === 'digital') return 'digital mode';
  if (scene.peakCount === 0) return 'mode profile';
  if (scene.coherentPeakCount === 0 && scene.impulsiveOccupiedRatio > 0) return 'impulsive artifacts';
  if (scene.coherentPeakCount === 0) return 'uncorrelated peaks';
  return 'voice profile';
}

function publishSceneStatus(scene: SignalEnhanceScene): void {
  useSignalEnhanceStore.getState().setSignalEnhanceSceneStatus({
    atUtc: new Date().toISOString(),
    profileId: scene.profileId,
    baseProfileId: scene.baseProfileId,
    reason: sceneReason(scene),
    peakCount: scene.peakCount,
    coherentPeakCount: scene.coherentPeakCount,
    peaksPer10Khz: Math.round(scene.peaksPer10Khz * 10) / 10,
    occupiedPct: Math.round(scene.occupiedRatio * 1000) / 10,
    coherentOccupiedPct: Math.round(scene.coherentOccupiedRatio * 1000) / 10,
    impulsivePct: Math.round(scene.impulsiveOccupiedRatio * 1000) / 10,
    maxSnrDb: Math.round(scene.maxSnrDb * 10) / 10,
    coherentMaxSnrDb: Math.round(scene.coherentMaxSnrDb * 10) / 10,
  });
}

function autoNotchSignature(notches: Array<{ centerHz: number; widthHz: number }>): string {
  return JSON.stringify(
    notches
      .map((n) => [Math.round(n.centerHz), Math.round(n.widthHz)] as const)
      .sort((a, b) => a[0] - b[0] || a[1] - b[1]),
  );
}

function clearAutoNotchesIfPresent(): void {
  const notch = useNotchStore.getState();
  if (notch.notches.some((n) => n.source === 'auto')) {
    notch.clearAutoNotches();
  }
}

function mergeHydratedSettings(
  server: SignalEnhancePersisted,
  initial: SignalEnhancePersisted,
  current: SignalEnhancePersisted,
): SignalEnhancePersisted {
  const merged: SignalEnhancePersisted = { ...server };
  for (const key of Object.keys(server) as Array<keyof SignalEnhancePersisted>) {
    if (!Object.is(current[key], initial[key])) {
      (merged[key] as SignalEnhancePersisted[typeof key]) = current[key];
    }
  }
  return merged;
}

// Always-mounted controller for Settings > DSP > Signal Intelligence.
// The settings panel only edits state; this component performs the live
// automation while the operator is back on the panadapter.
export function SignalIntelligenceController() {
  const initialSettingsRef = useRef<SignalEnhancePersisted | null>(null);
  const initialSettingsJsonRef = useRef<string | null>(null);
  if (initialSettingsRef.current === null) {
    initialSettingsRef.current = signalEnhanceSettingsFromState(useSignalEnhanceStore.getState());
  }
  if (initialSettingsJsonRef.current === null) {
    initialSettingsJsonRef.current = JSON.stringify(initialSettingsRef.current);
  }

  useEffect(() => {
    let active = true;
    let touched = false;
    let hydrating = false;
    let saveTimer: number | null = null;
    let saveAbort: AbortController | null = null;
    let lastSavedJson = JSON.stringify(signalEnhanceSettingsFromState(useSignalEnhanceStore.getState()));

    const clearSaveTimer = () => {
      if (saveTimer !== null) {
        window.clearTimeout(saveTimer);
        saveTimer = null;
      }
    };

    const scheduleSave = () => {
      touched = true;
      const snapshot = signalEnhanceSettingsFromState(useSignalEnhanceStore.getState());
      const json = JSON.stringify(snapshot);
      clearSaveTimer();
      if (json === lastSavedJson) return;
      saveTimer = window.setTimeout(() => {
        saveTimer = null;
        saveAbort?.abort();
        const controller = new AbortController();
        saveAbort = controller;
        void saveDisplayIntelligenceSettings(snapshot, controller.signal)
          .then((saved) => {
            if (!controller.signal.aborted) {
              lastSavedJson = JSON.stringify(saved);
            }
          })
          .catch(() => {
            /* Local store and localStorage remain authoritative until the next change. */
          })
          .finally(() => {
            if (saveAbort === controller) saveAbort = null;
          });
      }, SETTINGS_SAVE_DEBOUNCE_MS);
    };

    fetchDisplayIntelligenceSettings()
      .then((settings) => {
        const current = signalEnhanceSettingsFromState(useSignalEnhanceStore.getState());
        const currentJson = JSON.stringify(current);
        const localChanged = touched || currentJson !== initialSettingsJsonRef.current;
        if (!active) return;
        const serverJson = JSON.stringify(settings);
        const next = localChanged
          ? mergeHydratedSettings(settings, initialSettingsRef.current!, current)
          : settings;
        hydrating = true;
        useSignalEnhanceStore.getState().applySignalEnhanceSettings(next);
        hydrating = false;
        lastSavedJson = localChanged ? serverJson : JSON.stringify(signalEnhanceSettingsFromState(useSignalEnhanceStore.getState()));
        if (localChanged) scheduleSave();
      })
      .catch(() => {
        /* Browser-local Signal Intelligence settings stay active if the backend is unavailable. */
      });

    const unsubEnhance = useSignalEnhanceStore.subscribe((state, prev) => {
      if (hydrating) return;
      const nextJson = JSON.stringify(signalEnhanceSettingsFromState(state));
      const prevJson = JSON.stringify(signalEnhanceSettingsFromState(prev));
      if (nextJson !== prevJson) scheduleSave();
    });

    return () => {
      active = false;
      clearSaveTimer();
      saveAbort?.abort();
      unsubEnhance();
    };
  }, []);

  useEffect(() => {
    let lastSceneAt = 0;
    let lastAutoNotchesJson = EMPTY_AUTO_NOTCH_SIGNATURE;
    const autoNotchTracker = createAutoNotchTracker();
    let pendingProfile: SignalEnhancePresetId | null = null;
    let pendingCount = 0;
    const releaseDiagnosticsFrames = registerFrameConsumer();
    const releaseDiagnosticsEstimator = registerEstimatorConsumer();

    // Live auto-notch input snapshot, shared by the sync loop and the dev
    // console diagnostics below.
    const currentAutoNotchInput = () => {
      const display = useDisplayStore.getState();
      return {
        spectrum: display.panValid ? display.panDb : null,
        floor: getNoiseFloor(),
        confidence: getSignalConfidence(),
        stationarity: getSignalStationarity(),
        centerHz: display.centerHz,
        hzPerPixel: display.hzPerPixel,
        existingNotches: useNotchStore.getState().notches,
        protectedRanges: DIGITAL_PROTECTED_RANGES,
      };
    };

    // Console diagnostics: in the browser, `zeusAnf.explain(14249000)` reports
    // which gate (if any) rejected the carrier at that frequency, and
    // `zeusAnf.rejections()` lists the strongest visible peaks that were NOT
    // notched, with the reason. "Log what's on the wire" — tune from real data.
    if (typeof window !== 'undefined') {
      (window as unknown as { zeusAnf?: unknown }).zeusAnf = {
        explain: (freqHz: number) => explainAutoNotchAt(currentAutoNotchInput(), freqHz),
        rejections: (limit?: number) => explainAutoNotchRejections(currentAutoNotchInput(), limit),
      };
    }

    const resetPending = () => {
      pendingProfile = null;
      pendingCount = 0;
    };

    const applyModeProfile = () => {
      const enhance = useSignalEnhanceStore.getState();
      if (!enhance.autoProfileEnabled) return;
      enhance.applySignalEnhanceModeProfile(useConnectionStore.getState().mode);
      resetPending();
    };

    const resetAutoNotches = () => {
      autoNotchTracker.clear();
      clearAutoNotchesIfPresent();
      lastAutoNotchesJson = EMPTY_AUTO_NOTCH_SIGNATURE;
    };

    const syncAutoNotches = () => {
      const conn = useConnectionStore.getState();
      const tx = useTxStore.getState();
      const shouldRun =
        conn.status === 'Connected' &&
        conn.nr.anfEnabled &&
        !tx.moxOn &&
        !tx.tunOn;

      if (!shouldRun) {
        resetAutoNotches();
        return;
      }

      const notch = useNotchStore.getState();
      const candidates = detectAutoNotches(currentAutoNotchInput());
      const tracked = autoNotchTracker.update(candidates, notch.notches).map((n) => ({
        centerHz: Math.round(n.centerHz),
        widthHz: Math.round(n.widthHz),
      }));

      const nextJson = autoNotchSignature(tracked);
      const currentJson = autoNotchSignature(notch.notches.filter((n) => n.source === 'auto'));
      if (nextJson !== lastAutoNotchesJson || currentJson !== nextJson) {
        notch.replaceAutoNotches(tracked);
        lastAutoNotchesJson = nextJson;
      }
    };

    const maybeApplyScene = () => {
      const now = Date.now();
      if (now - lastSceneAt < SCENE_SAMPLE_INTERVAL_MS) return;
      lastSceneAt = now;

      const enhance = useSignalEnhanceStore.getState();
      const conn = useConnectionStore.getState();
      const display = useDisplayStore.getState();
      const scene = recommendSignalEnhanceScene({
        mode: conn.mode,
        spectrum: display.panValid ? display.panDb : null,
        floor: getNoiseFloor(),
        confidence: getSignalConfidence(),
        hzPerPixel: display.hzPerPixel,
      });
      publishSceneStatus(scene);
      syncAutoNotches();
      if (!enhance.autoProfileEnabled) {
        resetPending();
        return;
      }
      if (scene.profileId === enhance.profileId) {
        resetPending();
        return;
      }

      if (pendingProfile !== scene.profileId) {
        pendingProfile = scene.profileId;
        pendingCount = 1;
        return;
      }

      pendingCount++;
      if (pendingCount >= SCENE_DWELL_SAMPLES) {
        enhance.applySignalEnhanceAutoProfile(scene.profileId);
        resetPending();
      }
    };

    const unsubConn = useConnectionStore.subscribe((state, prev) => {
      if (state.mode !== prev.mode || state.status !== prev.status) applyModeProfile();
      if (
        (state.status !== prev.status && state.status !== 'Connected') ||
        (state.nr.anfEnabled !== prev.nr.anfEnabled && !state.nr.anfEnabled)
      ) {
        resetAutoNotches();
      }
    });
    const unsubEnhance = useSignalEnhanceStore.subscribe((state, prev) => {
      if (state.autoProfileEnabled && !prev.autoProfileEnabled) applyModeProfile();
      if (!state.autoProfileEnabled && prev.autoProfileEnabled) {
        resetPending();
      }
    });
    const unsubTx = useTxStore.subscribe((state, prev) => {
      if ((state.moxOn && !prev.moxOn) || (state.tunOn && !prev.tunOn)) resetAutoNotches();
    });
    const unsubDisplay = useDisplayStore.subscribe((state, prev) => {
      if (state.lastSeq !== prev.lastSeq) maybeApplyScene();
    });

    applyModeProfile();
    if (!useConnectionStore.getState().nr.anfEnabled) resetAutoNotches();
    return () => {
      unsubConn();
      unsubEnhance();
      unsubTx();
      unsubDisplay();
      releaseDiagnosticsFrames();
      releaseDiagnosticsEstimator();
      if (typeof window !== 'undefined') {
        delete (window as unknown as { zeusAnf?: unknown }).zeusAnf;
      }
    };
  }, []);

  return null;
}
