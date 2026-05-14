// SPDX-License-Identifier: GPL-2.0-or-later
//
// SettingsView — verify the TX Audio Tools tab is always present and that
// the VST host submenu inside it is gated by /api/capabilities. CFC is
// WDSP-driven and must remain visible regardless of the sidecar.

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

function seed(vstAvailable: boolean) {
  useCapabilitiesStore.setState({
    loaded: true,
    inflight: false,
    loadError: null,
    capabilities: {
      host: 'server',
      platform: vstAvailable ? 'linux' : 'darwin',
      architecture: 'x64',
      version: 'test',
      features: {
        vstHost: {
          available: vstAvailable,
          reason: vstAvailable ? null : 'unsupported',
          sidecarPath: vstAvailable ? '/x' : null,
        },
      },
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
    seed(false);
    act(() => {
      root.render(<SettingsView onClose={() => {}} />);
    });
    const tabs = Array.from(
      container.querySelectorAll('[role="tablist"] button'),
    ).map((b) => b.textContent?.trim() ?? '');
    expect(tabs).toContain('TX AUDIO TOOLS');
  });

  it('shows CFC and the VST host submenu when vstHost.available is true', () => {
    seed(true);
    act(() => {
      root.render(<SettingsView onClose={() => {}} initialTab="tx-audio" />);
    });
    expect(container.textContent).toContain('Continuous Frequency Compressor');
    expect(container.textContent).toContain('VST Host');
  });

  it('shows CFC but hides the VST host submenu when vstHost.available is false', () => {
    seed(false);
    act(() => {
      root.render(<SettingsView onClose={() => {}} initialTab="tx-audio" />);
    });
    expect(container.textContent).toContain('Continuous Frequency Compressor');
    expect(container.textContent).not.toContain('VST Host');
  });
});

describe('SettingsView — RADIO tab gating', () => {
  let container: HTMLDivElement;
  let root: Root;

  beforeEach(() => {
    container = document.createElement('div');
    document.body.appendChild(container);
    root = createRoot(container);
    seed(false);
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
