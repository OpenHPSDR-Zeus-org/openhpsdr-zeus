// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { useCapabilitiesStore } from '../state/capabilities-store';
import {
  __resetAudioHostModeForTests,
  getAudioHostMode,
  isNativeAudio,
  setAudioHostMode,
} from './host-mode';

// Helper: drive the capabilities store to a host value without going
// through the network. The store is the canonical source of truth for
// isNativeAudio() / getAudioHostMode() in the new design.
function setHost(host: 'server' | 'desktop' | null) {
  if (host == null) {
    useCapabilitiesStore.setState({ capabilities: null });
    return;
  }
  useCapabilitiesStore.setState({
    capabilities: {
      host,
      platform: 'darwin',
      architecture: 'arm64',
      version: '0.0.0-test',
      features: { vstHost: { available: false, reason: 't', sidecarPath: null } },
    } as never,
  });
}

describe('audio host-mode flag', () => {
  beforeEach(() => {
    setHost(null);
    __resetAudioHostModeForTests();
  });
  afterEach(() => {
    setHost(null);
    __resetAudioHostModeForTests();
    vi.restoreAllMocks();
  });

  it('defaults to browser mode when capabilities is unresolved', () => {
    expect(getAudioHostMode()).toBe('browser');
    expect(isNativeAudio()).toBe(false);
  });

  it('reports native when capabilities.host === "desktop"', () => {
    setHost('desktop');
    expect(isNativeAudio()).toBe(true);
    expect(getAudioHostMode()).toBe('native');
  });

  it('reports browser when capabilities.host === "server"', () => {
    setHost('server');
    expect(isNativeAudio()).toBe(false);
    expect(getAudioHostMode()).toBe('browser');
  });

  it('setAudioHostMode("native") logs exactly once across repeated calls', () => {
    const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
    setAudioHostMode('native');
    setAudioHostMode('native');
    setAudioHostMode('native');
    expect(logSpy).toHaveBeenCalledTimes(1);
    expect(logSpy.mock.calls[0]?.[0]).toMatch(/native audio active/);
  });

  it('setAudioHostMode("browser") does not log', () => {
    const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
    setAudioHostMode('browser');
    setAudioHostMode('browser');
    expect(logSpy).not.toHaveBeenCalled();
  });
});
