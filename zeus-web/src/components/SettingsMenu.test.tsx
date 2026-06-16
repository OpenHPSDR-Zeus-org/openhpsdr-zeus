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
  });

  afterEach(() => {
    act(() => {
      root.unmount();
    });
    container.remove();
    vi.unstubAllGlobals();
  });

  it('always renders the TX AUDIO TOOLS tab', () => {
    seed();
    act(() => {
      root.render(<SettingsView onClose={() => {}} />);
    });
    const tabs = Array.from(
      container.querySelectorAll('[role="tablist"] button'),
    ).map((b) => b.textContent?.trim() ?? '');
    expect(tabs).toContain('TX AUDIO TOOLS');
  });

  it('shows CFC inside the TX Audio Tools tab', () => {
    seed();
    act(() => {
      root.render(<SettingsView onClose={() => {}} initialTab="tx-audio" />);
    });
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
  });

  afterEach(() => {
    act(() => {
      root.unmount();
    });
    container.remove();
    vi.unstubAllGlobals();
    // Restore the radio-store fixture so other test files start clean.
    seedRadioCaps({
      hasHl2OptionalToggles: false,
      hasTxAntennaRelays: false,
      hasRxAntennaRelays: false,
    });
  });

  it('hides the RADIO tab when hasHl2OptionalToggles is false', () => {
    seedRadioCaps({ hasHl2OptionalToggles: false });
    act(() => {
      root.render(<SettingsView onClose={() => {}} />);
    });
    const tabs = Array.from(
      container.querySelectorAll('[role="tablist"] button'),
    ).map((b) => b.textContent?.trim() ?? '');
    expect(tabs).not.toContain('RADIO');
  });

  it('hides RADIO SETTINGS when the board has no external ports at all', () => {
    seedRadioCaps({ hasTxAntennaRelays: false, hasRxAntennaRelays: false });
    act(() => {
      root.render(<SettingsView onClose={() => {}} />);
    });
    const tabs = Array.from(
      container.querySelectorAll('[role="tablist"] button'),
    ).map((b) => b.textContent?.trim() ?? '');
    expect(tabs).not.toContain('RADIO SETTINGS');
  });

  it('shows RADIO SETTINGS on HL2 (no relays, but mic front-end + user GPIO)', () => {
    // Phase 5 gate broadening: HL2 has no antenna relays but DOES have the
    // register-0x14 mic front-end and the 4-bit user GPIO, so the tab — and
    // therefore those HL2-only controls — must be reachable.
    seedRadioCaps({
      hasTxAntennaRelays: false,
      hasRxAntennaRelays: false,
      hermesLite2MicFrontEnd: true,
      hasHl2UserGpio: true,
    });
    act(() => {
      root.render(<SettingsView onClose={() => {}} />);
    });
    const tabs = Array.from(
      container.querySelectorAll('[role="tablist"] button'),
    ).map((b) => b.textContent?.trim() ?? '');
    expect(tabs).toContain('RADIO SETTINGS');
  });

  it('shows RADIO SETTINGS and renders the antenna panel when the board has relays', () => {
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>().mockResolvedValue(
        new Response(
          JSON.stringify({
            hasTxAntennaRelays: true,
            hasRxAntennaRelays: true,
            bands: [{ band: '20m', txAnt: 'Ant1', rxAnt: 'Ant1' }],
          }),
          { status: 200, headers: { 'content-type': 'application/json' } },
        ),
      ),
    );

    seedRadioCaps({ hasTxAntennaRelays: true, hasRxAntennaRelays: true });
    act(() => {
      root.render(<SettingsView onClose={() => {}} initialTab="radio-settings" />);
    });
    const tabs = Array.from(
      container.querySelectorAll('[role="tablist"] button'),
    ).map((b) => b.textContent?.trim() ?? '');
    expect(tabs).toContain('RADIO SETTINGS');
    expect(container.textContent).toContain('TX Antenna');
    expect(container.textContent).toContain('RX Antenna');
  });

  it('shows the RADIO tab and renders the panel on click when hasHl2OptionalToggles is true', () => {
    // Mock fetch for the panel's mount-effect load(). The PUT path is
    // covered by RadioOptionsPanel.test.tsx — here we just need the GET
    // to not blow up.
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>().mockResolvedValue(
        new Response(JSON.stringify({ bandVolts: false }), {
          status: 200,
          headers: { 'content-type': 'application/json' },
        }),
      ),
    );

    seedRadioCaps({ hasHl2OptionalToggles: true });
    act(() => {
      root.render(<SettingsView onClose={() => {}} />);
    });
    const tabButtons = Array.from(
      container.querySelectorAll<HTMLButtonElement>('[role="tablist"] button'),
    );
    const tabs = tabButtons.map((b) => b.textContent?.trim() ?? '');
    expect(tabs).toContain('RADIO');

    const radioTab = tabButtons.find((b) => b.textContent?.trim() === 'RADIO');
    expect(radioTab).toBeDefined();
    act(() => {
      radioTab!.click();
    });
    expect(container.textContent).toContain('Band Volts');
    expect(container.textContent).toContain('Enable Band Volts PWM output');
  });
});
