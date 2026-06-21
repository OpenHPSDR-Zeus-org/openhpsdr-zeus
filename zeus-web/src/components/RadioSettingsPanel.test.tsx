// SPDX-License-Identifier: GPL-2.0-or-later
//
// RadioSettingsPanel — Audio Input card behavior gates (external-audio-jacks
// re-port):
//   1. TX-audio source is SINGLE-SELECT — a radio-button group where exactly one
//      option is checked at a time (the old multi-checkbox bug is structurally
//      impossible).
//   2. DEFAULT-OFF — with the server-authoritative store at Host, every param
//      control (boost/bias/gain) is hidden.
//   3. Dependent params are visible ONLY for the active source.
//   4. Board-gating — the source list is driven by the per-board capability
//      flags carried in the /api/radio/audio response: Balanced renders only
//      when hasBalancedXlr; HL2 (codec false, mic-front-end true) collapses to
//      Host-only.
//
// Uses the project's createRoot + act idiom (see SettingsMenu.test.tsx); no RTL.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act } from 'react';
import { createRoot, type Root } from 'react-dom/client';

import { RadioSettingsPanel } from './RadioSettingsPanel';
import { useAudioStore, type TxAudioSource } from '../state/audio-store';
import { usePttStore } from '../state/ptt-store';

type Gates = {
  hasOnboardCodec: boolean;
  hermesLite2MicFrontEnd: boolean;
  hasRadioLineIn: boolean;
  hasBalancedXlr: boolean;
  hasMicBias: boolean;
};

// A fully-featured Saturn-class fingerprint: codec + line-in + balanced XLR +
// mic-bias. Board-gating tests trim flags off this.
const G2_GATES: Gates = {
  hasOnboardCodec: true,
  hermesLite2MicFrontEnd: false,
  hasRadioLineIn: true,
  hasBalancedXlr: true,
  hasMicBias: true,
};

// HL2: no codec, no radio jacks — the mic front-end flag is set but
// hasOnboardCodec is false, which is the Host-only collapse.
const HL2_GATES: Gates = {
  hasOnboardCodec: false,
  hermesLite2MicFrontEnd: true,
  hasRadioLineIn: false,
  hasBalancedXlr: false,
  hasMicBias: false,
};

function seedAudio(
  gates: Gates,
  source: TxAudioSource,
  params?: Partial<{ micBoost: boolean; micBias: boolean; lineInGain: number }>,
) {
  useAudioStore.setState((s) => ({
    ...s,
    // Neutralize the mount-effect load so it can't clobber the seeded state.
    load: async () => {},
    settings: {
      ...gates,
      source,
      micBoost: params?.micBoost ?? false,
      micBias: params?.micBias ?? false,
      lineInGain: params?.lineInGain ?? 0,
    },
    loaded: true,
    inflight: false,
  }));
}

let container: HTMLDivElement;
let root: Root;

beforeEach(() => {
  // Neutralize the PTT mount-effect load too (no network in jsdom).
  usePttStore.setState((s) => ({ ...s, load: async () => {} }));
  container = document.createElement('div');
  document.body.appendChild(container);
  root = createRoot(container);
});

afterEach(() => {
  act(() => root.unmount());
  container.remove();
  vi.restoreAllMocks();
});

function render() {
  act(() => root.render(<RadioSettingsPanel />));
}

describe('RadioSettingsPanel — Audio Input card', () => {
  it('renders the source radio-group for a codec board', () => {
    seedAudio(G2_GATES, 'Host');
    render();
    const group = container.querySelector('[role="radiogroup"]');
    expect(group).not.toBeNull();
    const radios = group!.querySelectorAll('[role="radio"]');
    // Host + RadioMic + RadioLineIn + RadioBalancedXlr = 4 sources on G2.
    expect(radios.length).toBe(4);
  });

  it('is single-select — exactly one option checked at Host default', () => {
    seedAudio(G2_GATES, 'Host');
    render();
    const checked = container.querySelectorAll('[role="radio"][aria-checked="true"]');
    expect(checked.length).toBe(1);
    expect(checked[0]!.textContent).toContain('Host');
  });

  it('hides every param control at Host default', () => {
    seedAudio(G2_GATES, 'Host');
    render();
    const text = container.textContent ?? '';
    expect(text).not.toContain('Mic Boost');
    expect(text).not.toContain('Mic Bias');
    expect(text).not.toContain('Line-In Gain');
  });

  it('shows Mic Boost + Mic Bias for RadioMic on a bias-capable board', () => {
    seedAudio(G2_GATES, 'RadioMic');
    render();
    const text = container.textContent ?? '';
    expect(text).toContain('Mic Boost');
    expect(text).toContain('Mic Bias');
    expect(text).not.toContain('Line-In Gain');
  });

  it('shows Line-In Gain only for RadioLineIn', () => {
    seedAudio(G2_GATES, 'RadioLineIn');
    render();
    const text = container.textContent ?? '';
    expect(text).toContain('Line-In Gain');
    expect(text).not.toContain('Mic Boost');
  });

  it('hides Mic Bias on a board without mic bias', () => {
    seedAudio({ ...G2_GATES, hasMicBias: false }, 'RadioMic');
    render();
    const text = container.textContent ?? '';
    expect(text).toContain('Mic Boost');
    expect(text).not.toContain('Mic Bias');
  });

  it('omits Balanced when hasBalancedXlr is false', () => {
    seedAudio({ ...G2_GATES, hasBalancedXlr: false }, 'Host');
    render();
    const labels = Array.from(
      container.querySelectorAll('[role="radio"]'),
    ).map((n) => n.textContent ?? '');
    expect(labels.some((l) => l.includes('Balanced'))).toBe(false);
    // Host + Mic + Line In = 3.
    expect(labels.length).toBe(3);
  });

  it('collapses to Host-only on HL2 (no codec)', () => {
    seedAudio(HL2_GATES, 'Host');
    render();
    // No radio-group picker; a disabled Host-only select instead.
    expect(container.querySelector('[role="radiogroup"]')).toBeNull();
    const text = container.textContent ?? '';
    expect(text).toContain('no onboard audio codec');
  });
});
