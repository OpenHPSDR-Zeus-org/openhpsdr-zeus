// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  browserOutputSelectionSupported,
  useAudioDeviceStore,
} from './audio-device-store';

const originalMediaDevices = navigator.mediaDevices;

function setMediaDevices(devices: MediaDeviceInfo[]) {
  Object.defineProperty(navigator, 'mediaDevices', {
    configurable: true,
    value: {
      enumerateDevices: vi.fn(async () => devices),
    },
  });
}

describe('audio-device-store', () => {
  beforeEach(() => {
    localStorage.clear();
    useAudioDeviceStore.getState().__resetForTests();
  });

  afterEach(() => {
    Object.defineProperty(navigator, 'mediaDevices', {
      configurable: true,
      value: originalMediaDevices,
    });
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
    localStorage.clear();
    useAudioDeviceStore.getState().__resetForTests();
  });

  it('persists selected browser input and output ids', () => {
    const s = useAudioDeviceStore.getState();

    s.setBrowserInputDeviceId(' mic-1 ');
    s.setBrowserOutputDeviceId(' speaker-1 ');

    const next = useAudioDeviceStore.getState();
    expect(next.browserInputDeviceId).toBe('mic-1');
    expect(next.browserOutputDeviceId).toBe('speaker-1');
    expect(localStorage.getItem('zeus-audio-devices')).toContain('speaker-1');
  });

  it('refreshBrowserDevices maps browser media devices by route', async () => {
    class TestAudioContext {}
    (TestAudioContext.prototype as { setSinkId?: () => Promise<void> }).setSinkId =
      async () => {};
    vi.stubGlobal('AudioContext', TestAudioContext);
    setMediaDevices([
      { kind: 'audioinput', deviceId: 'default', label: 'Default mic' },
      { kind: 'audioinput', deviceId: 'mic-2', label: 'USB mic' },
      { kind: 'audiooutput', deviceId: 'speaker-2', label: 'USB speaker' },
      { kind: 'videoinput', deviceId: 'cam-1', label: 'Camera' },
    ] as MediaDeviceInfo[]);

    await useAudioDeviceStore.getState().refreshBrowserDevices();

    const s = useAudioDeviceStore.getState();
    expect(s.browserDevicesLoaded).toBe(true);
    expect(s.browserInputDeviceId).toBe('');
    expect(s.browserInputs.map((device) => device.name)).toEqual([
      'Default mic',
      'USB mic',
    ]);
    expect(s.browserOutputs.map((device) => device.id)).toEqual(['speaker-2']);
    expect(s.browserOutputSupported).toBe(true);
    expect(browserOutputSelectionSupported()).toBe(true);
  });
});
