// SPDX-License-Identifier: GPL-2.0-or-later
//
// SettingsView — verify the TX Audio Tools tab is always present. CFC is
// WDSP-driven and must remain visible.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act } from 'react';
import { createRoot, type Root } from 'react-dom/client';

import { SettingsView } from './SettingsMenu';
import { useCapabilitiesStore } from '../state/capabilities-store';
import { useRadioStore } from '../state/radio-store';
import {
  UNKNOWN_BOARD_CAPABILITIES,
  type BoardCapabilities,
} from '../api/board-capabilities';

const HF_BANDS = ['160m', '80m', '60m', '40m', '30m', '20m', '17m', '15m', '12m', '10m', '6m'];

function jsonResponse(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'content-type': 'application/json' },
  });
}

function apiPath(input: RequestInfo | URL): string {
  const raw = typeof input === 'string'
    ? input
    : input instanceof URL
      ? input.toString()
      : input.url;
  const url = new URL(raw, 'http://localhost');
  return url.pathname;
}

function paSettingsFixture() {
  return {
    global: { paEnabled: true, paMaxPowerWatts: 100 },
    bands: HF_BANDS.map((band) => ({
      band,
      paGainDb: 48,
      disablePa: false,
      ocTx: 0,
      ocRx: 0,
      autoOcMask: 0,
      ocDxTx: 0,
      ocDxRx: 0,
    })),
  };
}

function stubSettingsFetch(overrides: Record<string, unknown> = {}) {
  vi.stubGlobal(
    'fetch',
    vi.fn<typeof fetch>(async (input) => {
      const path = apiPath(input);
      if (path in overrides) return jsonResponse(overrides[path]);
      switch (path) {
        case '/api/radio/selection':
          return jsonResponse({
            preferred: 'Auto',
            connected: 'Unknown',
            effective: 'Unknown',
            overrideDetection: false,
          });
        case '/api/radio/capabilities':
          return jsonResponse(UNKNOWN_BOARD_CAPABILITIES);
        case '/api/radio/orion-mkii-variant':
          return jsonResponse({ variant: 'G2' });
        case '/api/pa-settings':
        case '/api/pa-settings/defaults':
          return jsonResponse(paSettingsFixture());
        case '/api/audio-suite/master-bypass':
          return jsonResponse({ bypassed: false });
        case '/api/audio-suite/processing-mode':
          return jsonResponse({ mode: 'native', engineAvailable: false, engineActive: false });
        case '/api/audio-suite/profiles':
          return jsonResponse({ profiles: [] });
        case '/api/tx/fidelity-policy':
          return jsonResponse({ profileId: 'studio-ssb', targetSpectralDensity: 55 });
        case '/api/tx/station-profiles':
          return jsonResponse({ profiles: [] });
        case '/api/radio/hl2-options':
          return jsonResponse({ bandVolts: false });
        default:
          return jsonResponse({});
      }
    }),
  );
}

function seed() {
  useCapabilitiesStore.setState({
    loaded: true,
    inflight: false,
    loadError: null,
    capabilities: {
      host: 'server',
      platform: 'linux',
      architecture: 'x64',
      version: 'test',
      features: {},
    },
    localToServer: false,
  });
}

async function flushEffects() {
  await Promise.resolve();
  await Promise.resolve();
}

// HL2-optional-toggles seeding for the RADIO tab — flips the per-board
// capability flag without touching the rest of the radio-store fixture.
function seedRadioCaps(overrides: Partial<BoardCapabilities>) {
  useRadioStore.setState((s) => ({
    ...s,
    capabilities: { ...UNKNOWN_BOARD_CAPABILITIES, ...overrides },
  }));
}

describe('SettingsView — TX Audio Tools', () => {
  let container: HTMLDivElement;
  let root: Root;

  beforeEach(() => {
    container = document.createElement('div');
    document.body.appendChild(container);
    root = createRoot(container);
    stubSettingsFetch();
  });

  afterEach(() => {
    act(() => {
      root.unmount();
    });
    container.remove();
    vi.unstubAllGlobals();
  });

  it('always renders the TX AUDIO TOOLS tab', async () => {
    seed();
    await act(async () => {
      root.render(<SettingsView onClose={() => {}} />);
      await flushEffects();
    });
    const tabs = Array.from(
      container.querySelectorAll('[role="tablist"] button'),
    ).map((b) => b.textContent?.trim() ?? '');
    expect(tabs).toContain('TX AUDIO TOOLS');
  });

  it('shows CFC inside the TX Audio Tools tab', async () => {
    seed();
    await act(async () => {
      root.render(<SettingsView onClose={() => {}} initialTab="tx-audio" />);
      await flushEffects();
    });
    expect(container.textContent).toContain('TX Fidelity Policy');
    expect(container.textContent).toContain('Station Profile');
    expect(container.textContent).toContain('Continuous Frequency Compressor');
  });
});

describe('SettingsView — RADIO tab gating', () => {
  let container: HTMLDivElement;
  let root: Root;

  beforeEach(() => {
    container = document.createElement('div');
    document.body.appendChild(container);
    root = createRoot(container);
    seed();
    stubSettingsFetch();
  });

  afterEach(() => {
    act(() => {
      root.unmount();
    });
    container.remove();
    vi.unstubAllGlobals();
    // Restore the radio-store fixture so other test files start clean.
    seedRadioCaps({ hasHl2OptionalToggles: false });
  });

  it('hides the RADIO tab when hasHl2OptionalToggles is false', async () => {
    seedRadioCaps({ hasHl2OptionalToggles: false });
    await act(async () => {
      root.render(<SettingsView onClose={() => {}} />);
      await flushEffects();
    });
    const tabs = Array.from(
      container.querySelectorAll('[role="tablist"] button'),
    ).map((b) => b.textContent?.trim() ?? '');
    expect(tabs).not.toContain('RADIO');
  });

  it('shows the RADIO tab and renders the panel on click when hasHl2OptionalToggles is true', async () => {
    // Mock fetch for the panel's mount-effect load(). The PUT path is
    // covered by RadioOptionsPanel.test.tsx — here we just need the GET
    // to not blow up.
    stubSettingsFetch({
      '/api/radio/capabilities': {
        ...UNKNOWN_BOARD_CAPABILITIES,
        hasHl2OptionalToggles: true,
      },
      '/api/radio/hl2-options': { bandVolts: false },
    });

    seedRadioCaps({ hasHl2OptionalToggles: true });
    await act(async () => {
      root.render(<SettingsView onClose={() => {}} />);
      await flushEffects();
    });
    const tabButtons = Array.from(
      container.querySelectorAll<HTMLButtonElement>('[role="tablist"] button'),
    );
    const tabs = tabButtons.map((b) => b.textContent?.trim() ?? '');
    expect(tabs).toContain('RADIO');

    const radioTab = tabButtons.find((b) => b.textContent?.trim() === 'RADIO');
    expect(radioTab).toBeDefined();
    await act(async () => {
      radioTab!.click();
      await flushEffects();
    });
    expect(container.textContent).toContain('Band Volts');
    expect(container.textContent).toContain('Enable Band Volts PWM output');
  });
});
