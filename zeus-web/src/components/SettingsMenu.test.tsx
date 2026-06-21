// SPDX-License-Identifier: GPL-2.0-or-later
//
// SettingsView — verify the Audio Tools tab is always present. CFC is
// WDSP-driven and must remain visible.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act } from 'react';
import { createRoot, type Root } from 'react-dom/client';

import { SettingsView } from './SettingsMenu';
import { useCapabilitiesStore } from '../state/capabilities-store';
import { useRadioStore } from '../state/radio-store';
import { useEasterEggStore } from '../state/easter-egg-store';
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
        case '/api/tx-audio-suite/master-bypass':
          return jsonResponse({ bypassed: false });
        case '/api/tx-audio-suite/processing-mode':
          return jsonResponse({ mode: 'native', engineAvailable: false, engineActive: false });
        case '/api/tx-audio-suite/profiles':
          return jsonResponse({ profiles: [] });
        case '/api/tx/fidelity-policy':
          return jsonResponse({ profileId: 'studio-ssb', targetSpectralDensity: 55 });
        case '/api/tx/station-profiles':
          return jsonResponse({ profiles: [] });
        case '/api/radio/hl2-options':
          return jsonResponse({ bandVolts: false });
        case '/api/radio/g2-options':
          return jsonResponse({
            ditherEnabled: true,
            randomEnabled: true,
            maxRxFreqMHz: 60,
            supported: false,
          });
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
      lanHttpsUrls: [],
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

describe('SettingsView — Audio Tools', () => {
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

  it('always renders the AUDIO TOOLS tab', async () => {
    seed();
    await act(async () => {
      root.render(<SettingsView onClose={() => {}} />);
      await flushEffects();
    });
    const tabs = Array.from(
      container.querySelectorAll('[role="tablist"] button'),
    ).map((b) => b.textContent?.trim() ?? '');
    expect(tabs).toContain('AUDIO TOOLS');
  });

  it('shows CFC inside the Audio Tools tab', async () => {
    seed();
    await act(async () => {
      root.render(<SettingsView onClose={() => {}} initialTab="tx-audio" />);
      await flushEffects();
    });
    expect(container.textContent).toContain('TX Audio');
    expect(container.textContent).toContain('RX Audio');
    expect(container.textContent).not.toContain('TX Fidelity Policy');
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

  it('shows the RADIO tab with radio settings even when no board firmware options are supported', async () => {
    // The RADIO tab is now always present — it hosts the universal radio
    // settings (PTT-IN / TX audio / antenna). With no board firmware-option
    // surface it shows those settings only; the per-board options section is
    // omitted.
    stubSettingsFetch({
      '/api/radio/ptt-status': { keyed: false, enabled: false, hangMs: 250 },
      '/api/radio/audio': {
        hasOnboardCodec: false,
        hermesLite2MicFrontEnd: false,
        hasRadioLineIn: false,
        hasBalancedXlr: false,
        hasMicBias: false,
        source: 'Host',
        micBoost: false,
        micBias: false,
        lineInGain: 0,
      },
      '/api/radio/antenna': {
        hasTxAntennaRelays: false,
        hasRxAntennaRelays: false,
        availableRxAux: [],
        bands: [],
      },
    });
    seedRadioCaps({ hasHl2OptionalToggles: false, supportsG2AdcOptions: false });
    await act(async () => {
      root.render(<SettingsView onClose={() => {}} />);
      await flushEffects();
    });
    const tabButtons = Array.from(
      container.querySelectorAll<HTMLButtonElement>('[role="tablist"] button'),
    );
    expect(tabButtons.map((b) => b.textContent?.trim() ?? '')).toContain('RADIO');
    // The old standalone RADIO SETTINGS tab is gone — folded into RADIO.
    expect(tabButtons.map((b) => b.textContent?.trim() ?? '')).not.toContain(
      'RADIO SETTINGS',
    );

    const radioTab = tabButtons.find((b) => b.textContent?.trim() === 'RADIO');
    expect(radioTab).toBeDefined();
    await act(async () => {
      radioTab!.click();
      await flushEffects();
    });
    // Universal radio settings render (PTT-IN card from RadioSettingsPanel)…
    expect(container.textContent).toContain('PTT-IN');
    // …but no per-board firmware-option section.
    expect(container.textContent).not.toContain('Band Volts');
    expect(container.textContent).not.toContain('ANAN-G2 Options');
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

  it('shows the RADIO tab and renders G2 options when supportsG2AdcOptions is true', async () => {
    stubSettingsFetch({
      '/api/radio/capabilities': {
        ...UNKNOWN_BOARD_CAPABILITIES,
        supportsG2AdcOptions: true,
      },
      '/api/radio/g2-options': {
        ditherEnabled: true,
        randomEnabled: true,
        maxRxFreqMHz: 60,
        supported: true,
      },
    });

    seedRadioCaps({ supportsG2AdcOptions: true });
    await act(async () => {
      root.render(<SettingsView onClose={() => {}} />);
      await flushEffects();
    });
    const tabButtons = Array.from(
      container.querySelectorAll<HTMLButtonElement>('[role="tablist"] button'),
    );
    expect(tabButtons.map((b) => b.textContent?.trim() ?? '')).toContain('RADIO');

    const radioTab = tabButtons.find((b) => b.textContent?.trim() === 'RADIO');
    await act(async () => {
      radioTab!.click();
      await flushEffects();
    });
    expect(container.textContent).toContain('ANAN-G2 Options');
    expect(container.textContent).toContain('Dither Enabled');
    expect(container.textContent).toContain('Random Enabled');
  });
});

describe('SettingsView — hidden HARDWARE folder (easter egg)', () => {
  let container: HTMLDivElement;
  let root: Root;

  beforeEach(() => {
    container = document.createElement('div');
    document.body.appendChild(container);
    root = createRoot(container);
    seed();
    stubSettingsFetch();
    // Default locked state — a fresh session never lists HARDWARE.
    useEasterEggStore.setState({ hardwareUnlocked: false });
  });

  afterEach(() => {
    act(() => {
      root.unmount();
    });
    container.remove();
    vi.unstubAllGlobals();
    useEasterEggStore.setState({ hardwareUnlocked: false });
  });

  it('hides the HARDWARE tab by default', async () => {
    await act(async () => {
      root.render(<SettingsView onClose={() => {}} />);
      await flushEffects();
    });
    const tabs = Array.from(
      container.querySelectorAll('[role="tablist"] button'),
    ).map((b) => b.textContent?.trim() ?? '');
    expect(tabs).not.toContain('HARDWARE');
  });

  it('bounces back to PA when opened on the locked HARDWARE tab', async () => {
    await act(async () => {
      root.render(<SettingsView onClose={() => {}} initialTab="hardware" />);
      await flushEffects();
    });
    const tabs = Array.from(
      container.querySelectorAll<HTMLButtonElement>('[role="tablist"] button'),
    );
    expect(tabs.map((b) => b.textContent?.trim() ?? '')).not.toContain('HARDWARE');
    // PA is the fallback and becomes the active tab.
    const pa = tabs.find((b) => b.textContent?.trim() === 'PA SETTINGS');
    expect(pa?.getAttribute('aria-selected')).toBe('true');
  });

  it('shows the HARDWARE tab once unlocked', async () => {
    useEasterEggStore.setState({ hardwareUnlocked: true });
    await act(async () => {
      root.render(<SettingsView onClose={() => {}} />);
      await flushEffects();
    });
    const tabs = Array.from(
      container.querySelectorAll('[role="tablist"] button'),
    ).map((b) => b.textContent?.trim() ?? '');
    expect(tabs).toContain('HARDWARE');
  });
});
