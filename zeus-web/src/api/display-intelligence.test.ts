// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  DISPLAY_INTELLIGENCE_DEFAULTS,
  fetchDisplayIntelligenceSettings,
  normalizeDisplayIntelligenceSettings,
  saveDisplayIntelligenceSettings,
} from './display-intelligence';

describe('display-intelligence API', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.clearAllMocks();
  });

  const jsonResponse = (body: unknown, status = 200): Response =>
    new Response(JSON.stringify(body), {
      status,
      headers: { 'content-type': 'application/json' },
    });

  it('normalizes the backend shape with safe bounds', () => {
    expect(normalizeDisplayIntelligenceSettings({
      profileId: 'DX',
      popEnabled: true,
      snapEnabled: true,
      autoProfileEnabled: true,
      visualAgcEnabled: false,
      impulseRejectEnabled: false,
      popFloorDb: -1,
      popSpanDb: 999,
      popGamma: Number.NaN,
      popRenderIntensity: 101,
      coherenceHoldGate: 0.1,
      coherenceBoostDb: 9,
      ridgeBoost: 2,
      ridgeMaxBoostDb: -4,
      visualAgcStrength: -20,
      impulseRejectDb: 100,
      snapRadiusHz: 50,
      snapMinSnrDb: 99,
      peakMinSnrDb: 2,
    })).toEqual({
      ...DISPLAY_INTELLIGENCE_DEFAULTS,
      profileId: 'dx',
      popEnabled: true,
      snapEnabled: true,
      autoProfileEnabled: true,
      visualAgcEnabled: false,
      impulseRejectEnabled: false,
      popFloorDb: 0,
      popSpanDb: 60,
      popRenderIntensity: 100,
      coherenceHoldGate: 0.2,
      coherenceBoostDb: 8,
      ridgeBoost: 0.8,
      ridgeMaxBoostDb: 0,
      visualAgcStrength: 0,
      impulseRejectDb: 32,
      snapRadiusHz: 500,
      snapMinSnrDb: 16,
      peakMinSnrDb: 4,
    });
  });

  it('fetches the display intelligence settings endpoint', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(jsonResponse({
      ...DISPLAY_INTELLIGENCE_DEFAULTS,
      profileId: 'dx',
      popEnabled: true,
    }));
    vi.stubGlobal('fetch', fetchMock);

    const settings = await fetchDisplayIntelligenceSettings();

    expect(fetchMock).toHaveBeenCalledWith('/api/dsp/display-intelligence', { signal: undefined });
    expect(settings.profileId).toBe('dx');
    expect(settings.popEnabled).toBe(true);
  });

  it('saves the full display intelligence policy snapshot', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(jsonResponse({
      ...DISPLAY_INTELLIGENCE_DEFAULTS,
      profileId: 'cw',
      snapEnabled: true,
    }));
    vi.stubGlobal('fetch', fetchMock);

    const settings = await saveDisplayIntelligenceSettings({
      ...DISPLAY_INTELLIGENCE_DEFAULTS,
      profileId: 'cw',
      snapEnabled: true,
    });

    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/dsp/display-intelligence');
    expect(init?.method).toBe('PUT');
    expect(JSON.parse((init?.body ?? '') as string)).toMatchObject({
      profileId: 'cw',
      snapEnabled: true,
    });
    expect(settings.profileId).toBe('cw');
    expect(settings.snapEnabled).toBe(true);
  });
});
