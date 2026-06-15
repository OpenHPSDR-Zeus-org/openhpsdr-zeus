// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus - OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.

import { useEffect } from 'react';
import {
  getNoiseFloor,
  getSignalConfidence,
  recommendSignalEnhanceScene,
  useSignalEnhanceStore,
  type SignalEnhanceScene,
  type SignalEnhancePresetId,
} from '../dsp/signal-estimator';
import { useConnectionStore } from '../state/connection-store';
import { useDisplayStore } from '../state/display-store';

const SCENE_SAMPLE_INTERVAL_MS = 1000;
const SCENE_DWELL_SAMPLES = 3;

function sceneReason(scene: SignalEnhanceScene): string {
  if (scene.profileId === 'contest') return 'crowded span';
  if (scene.profileId === 'dx') return 'sparse weak signal';
  if (scene.baseProfileId === 'cw') return 'CW mode';
  if (scene.baseProfileId === 'digital') return 'digital mode';
  if (scene.peakCount === 0) return 'mode profile';
  return 'voice profile';
}

function publishSceneStatus(scene: SignalEnhanceScene): void {
  useSignalEnhanceStore.getState().setSignalEnhanceSceneStatus({
    atUtc: new Date().toISOString(),
    profileId: scene.profileId,
    baseProfileId: scene.baseProfileId,
    reason: sceneReason(scene),
    peakCount: scene.peakCount,
    peaksPer10Khz: Math.round(scene.peaksPer10Khz * 10) / 10,
    occupiedPct: Math.round(scene.occupiedRatio * 1000) / 10,
    maxSnrDb: Math.round(scene.maxSnrDb * 10) / 10,
  });
}

// Always-mounted controller for Settings > DSP > Signal Intelligence.
// The settings panel only edits state; this component performs the live
// automation while the operator is back on the panadapter.
export function SignalIntelligenceController() {
  useEffect(() => {
    let lastSceneAt = 0;
    let pendingProfile: SignalEnhancePresetId | null = null;
    let pendingCount = 0;

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

    const maybeApplyScene = () => {
      const now = Date.now();
      if (now - lastSceneAt < SCENE_SAMPLE_INTERVAL_MS) return;
      lastSceneAt = now;

      const enhance = useSignalEnhanceStore.getState();
      if (!enhance.autoProfileEnabled) {
        if (enhance.sceneStatus !== null) enhance.setSignalEnhanceSceneStatus(null);
        resetPending();
        return;
      }

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
    });
    const unsubEnhance = useSignalEnhanceStore.subscribe((state, prev) => {
      if (state.autoProfileEnabled && !prev.autoProfileEnabled) applyModeProfile();
      if (!state.autoProfileEnabled && (prev.autoProfileEnabled || state.sceneStatus !== null)) {
        state.setSignalEnhanceSceneStatus(null);
        resetPending();
      }
    });
    const unsubDisplay = useDisplayStore.subscribe((state, prev) => {
      if (state.lastSeq !== prev.lastSeq) maybeApplyScene();
    });

    applyModeProfile();
    return () => {
      unsubConn();
      unsubEnhance();
      unsubDisplay();
    };
  }, []);

  return null;
}
