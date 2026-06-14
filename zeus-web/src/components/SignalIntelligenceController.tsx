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
  recommendSignalEnhanceScene,
  useSignalEnhanceStore,
  type SignalEnhancePresetId,
} from '../dsp/signal-estimator';
import { useConnectionStore } from '../state/connection-store';
import { useDisplayStore } from '../state/display-store';

const SCENE_SAMPLE_INTERVAL_MS = 1000;
const SCENE_DWELL_SAMPLES = 3;

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
        resetPending();
        return;
      }

      const conn = useConnectionStore.getState();
      const display = useDisplayStore.getState();
      const scene = recommendSignalEnhanceScene({
        mode: conn.mode,
        spectrum: display.panValid ? display.panDb : null,
        floor: getNoiseFloor(),
        hzPerPixel: display.hzPerPixel,
      });
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
      if (!state.autoProfileEnabled) resetPending();
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
