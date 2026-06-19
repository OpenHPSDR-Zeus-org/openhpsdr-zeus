// SPDX-License-Identifier: GPL-2.0-or-later

import { create } from 'zustand';
import { persist } from 'zustand/middleware';

export interface BrowserAudioDevice {
  id: string;
  name: string;
  isDefault: boolean;
}

interface AudioDeviceState {
  browserInputDeviceId: string;
  browserOutputDeviceId: string;
  browserInputs: BrowserAudioDevice[];
  browserOutputs: BrowserAudioDevice[];
  browserOutputSupported: boolean;
  browserDevicesLoaded: boolean;
  browserDeviceError: string | null;
  setBrowserInputDeviceId(id: string): void;
  setBrowserOutputDeviceId(id: string): void;
  refreshBrowserDevices(): Promise<void>;
  __resetForTests(): void;
}

const DEFAULT_DEVICE_ID = '';

function normalizeDeviceId(id: string | null | undefined): string {
  return id?.trim() || DEFAULT_DEVICE_ID;
}

export function browserOutputSelectionSupported(): boolean {
  if (typeof AudioContext === 'undefined') return false;
  return typeof (AudioContext.prototype as AudioContext & {
    setSinkId?: unknown;
  }).setSinkId === 'function';
}

function deviceLabel(device: MediaDeviceInfo, fallback: string): string {
  return device.label?.trim() || fallback;
}

function mapBrowserDevices(devices: MediaDeviceInfo[], kind: MediaDeviceKind): BrowserAudioDevice[] {
  const matching = devices.filter((device) => device.kind === kind);
  return matching.map((device, index) => ({
    id: normalizeDeviceId(device.deviceId),
    name: deviceLabel(device, kind === 'audioinput' ? `Input ${index + 1}` : `Output ${index + 1}`),
    isDefault: device.deviceId === 'default',
  }));
}

const initialTransientState = {
  browserInputs: [] as BrowserAudioDevice[],
  browserOutputs: [] as BrowserAudioDevice[],
  browserOutputSupported: false,
  browserDevicesLoaded: false,
  browserDeviceError: null as string | null,
};

export const useAudioDeviceStore = create<AudioDeviceState>()(
  persist(
    (set) => ({
      browserInputDeviceId: DEFAULT_DEVICE_ID,
      browserOutputDeviceId: DEFAULT_DEVICE_ID,
      ...initialTransientState,

      setBrowserInputDeviceId: (id) =>
        set({ browserInputDeviceId: normalizeDeviceId(id) }),
      setBrowserOutputDeviceId: (id) =>
        set({ browserOutputDeviceId: normalizeDeviceId(id) }),

      refreshBrowserDevices: async () => {
        const mediaDevices = typeof navigator !== 'undefined'
          ? navigator.mediaDevices
          : undefined;
        if (!mediaDevices?.enumerateDevices) {
          set({
            ...initialTransientState,
            browserOutputSupported: browserOutputSelectionSupported(),
            browserDeviceError: 'audio device enumeration unavailable',
          });
          return;
        }

        try {
          const devices = await mediaDevices.enumerateDevices();
          set({
            browserInputs: mapBrowserDevices(devices, 'audioinput'),
            browserOutputs: mapBrowserDevices(devices, 'audiooutput'),
            browserOutputSupported: browserOutputSelectionSupported(),
            browserDevicesLoaded: true,
            browserDeviceError: null,
          });
        } catch (err) {
          set({
            ...initialTransientState,
            browserOutputSupported: browserOutputSelectionSupported(),
            browserDeviceError: err instanceof Error ? err.message : String(err),
          });
        }
      },

      __resetForTests: () =>
        set({
          browserInputDeviceId: DEFAULT_DEVICE_ID,
          browserOutputDeviceId: DEFAULT_DEVICE_ID,
          ...initialTransientState,
        }),
    }),
    {
      name: 'zeus-audio-devices',
      version: 1,
      partialize: (s) => ({
        browserInputDeviceId: s.browserInputDeviceId,
        browserOutputDeviceId: s.browserOutputDeviceId,
      }),
    },
  ),
);
