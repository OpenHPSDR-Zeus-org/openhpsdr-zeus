// SPDX-License-Identifier: GPL-2.0-or-later
//
// The live bandwidth control must not offer wide P2 DDC rates unless both the
// protocol and board capability fingerprint allow them.

import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { act } from 'react';
import { createRoot, type Root } from 'react-dom/client';

import {
  UNKNOWN_BOARD_CAPABILITIES,
  type BoardCapabilities,
} from '../api/board-capabilities';
import { useConnectionStore } from '../state/connection-store';
import { useRadioStore } from '../state/radio-store';
import { BandwidthSettingsSection } from './BandwidthSettingsSection';

function seedRadioCaps(overrides: Partial<BoardCapabilities>) {
  useRadioStore.setState((s) => ({
    ...s,
    capabilities: { ...UNKNOWN_BOARD_CAPABILITIES, ...overrides },
  }));
}

function rateButton(container: HTMLElement, label: string): HTMLButtonElement {
  const button = Array.from(container.querySelectorAll<HTMLButtonElement>('button'))
    .find((b) => b.textContent?.trim() === label);
  expect(button).toBeDefined();
  return button!;
}

describe('BandwidthSettingsSection', () => {
  let container: HTMLDivElement;
  let root: Root;

  beforeEach(() => {
    useConnectionStore.setState({
      status: 'Connected',
      connectedProtocol: 'P2',
      sampleRate: 192_000,
    });
    seedRadioCaps({ maxRxSampleRateHz: 384_000 });
    container = document.createElement('div');
    document.body.appendChild(container);
    root = createRoot(container);
  });

  afterEach(() => {
    act(() => {
      root.unmount();
    });
    container.remove();
    useConnectionStore.setState({
      status: 'Disconnected',
      connectedProtocol: null,
      sampleRate: 192_000,
    });
    seedRadioCaps({ maxRxSampleRateHz: 384_000 });
  });

  it('locks wideband rates when the board capability ceiling is 384 kHz', () => {
    act(() => {
      root.render(<BandwidthSettingsSection />);
    });

    const wide = rateButton(container, '1536');
    expect(wide.disabled).toBe(true);
    expect(wide.title).toContain("exceeds this radio's 384 kHz RX/DDC capability");
    expect(container.textContent).toContain('max 384 kHz');
  });

  it('unlocks the full P2 ladder for a G2-class 1536 kHz board', () => {
    seedRadioCaps({ maxRxSampleRateHz: 1_536_000 });
    act(() => {
      root.render(<BandwidthSettingsSection />);
    });

    const wide = rateButton(container, '1536');
    expect(wide.disabled).toBe(false);
    expect(wide.title).toBe('Set DDC bandwidth to 1536 kHz');
    expect(container.textContent).toContain('max 1536 kHz');
  });

  it('keeps 768/1536 locked on Protocol 1 even when the board is wideband-capable', () => {
    seedRadioCaps({ maxRxSampleRateHz: 1_536_000 });
    useConnectionStore.setState({ connectedProtocol: 'P1' });
    act(() => {
      root.render(<BandwidthSettingsSection />);
    });

    const wide = rateButton(container, '1536');
    expect(wide.disabled).toBe(true);
    expect(wide.title).toBe('1536 kHz needs a Protocol-2 connection');
    expect(container.textContent).toContain('max 384 kHz');
  });
});
