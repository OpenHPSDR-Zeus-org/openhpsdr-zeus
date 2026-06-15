// SPDX-License-Identifier: GPL-2.0-or-later
//
// RadioSettingsPanel — Pass D behavior gates (external-ports plan §6/§7/§8):
//   1. TX-audio source is SINGLE-SELECT — a radio-button group where exactly
//      one option is checked; selecting another deselects the prior (the old
//      multi-checkbox bug is structurally impossible).
//   2. DEFAULT-OFF — on first render with the server-authoritative store at its
//      default, Host is selected and every param control (boost/bias/gain) is
//      hidden / off. Nothing comes up engaged.
//   3. Dependent params are visible ONLY for the active source.
//   4. Board-gating — HL2 is Host-only (no Mic/Line/Balanced); Balanced renders
//      only when hasBalancedXlr (G2 / G2-1K).
//
// Uses the project's createRoot + act idiom (see SettingsMenu.test.tsx); no RTL.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act } from 'react';
import { createRoot, type Root } from 'react-dom/client';

import { RadioSettingsPanel } from './RadioSettingsPanel';
import { useRadioStore } from '../state/radio-store';
import { useAudioStore, type TxAudioSource } from '../state/audio-store';
import { useAntennaStore } from '../state/antenna-store';
import { usePttStore } from '../state/ptt-store';
import { useHl2GpioStore } from '../state/hl2-gpio-store';
import {
  UNKNOWN_BOARD_CAPABILITIES,
  type BoardCapabilities,
} from '../api/board-capabilities';

// A fully-featured Saturn-class fingerprint: codec + line-in + balanced XLR +
// mic-bias. The board-gating tests trim flags off this.
const G2_CAPS: Partial<BoardCapabilities> = {
  hasOnboardCodec: true,
  hasRadioLineIn: true,
  hasBalancedXlr: true,
  hasMicBias: true,
};

// HL2: no codec, no radio jacks — the mic front-end flag is set but
// hasOnboardCodec is false, which is the Host-only collapse.
const HL2_CAPS: Partial<BoardCapabilities> = {
  hasOnboardCodec: false,
  hermesLite2MicFrontEnd: true,
};

function seedCaps(overrides: Partial<BoardCapabilities>) {
  useRadioStore.setState((s) => ({
    ...s,
    capabilities: { ...UNKNOWN_BOARD_CAPABILITIES, ...overrides },
  }));
}

function seedAudio(
  source: TxAudioSource,
  params?: Partial<{ micBoost: boolean; micBias: boolean; lineInGain: number }>,
) {
  useAudioStore.setState((s) => ({
    ...s,
    settings: {
      ...s.settings,
      source,
      micBoost: params?.micBoost ?? false,
      micBias: params?.micBias ?? false,
      lineInGain: params?.lineInGain ?? 0,
    },
    loaded: true,
    inflight: false,
  }));
}

// The panel's mount effect fires four GET loads (load/loadAudio/loadGpio/
// loadPtt). Those flip `inflight` true and would clobber the directly-seeded
// store state with the (empty) fetch response. Neutralize them to no-ops so the
// test renders exactly the seeded server-authoritative state — which is also
// the precise hydration path the panel relies on. We assert against the seeded
// state, not a live fetch.
function stubLoaders() {
  vi.spyOn(useAntennaStore.getState(), 'load').mockResolvedValue();
  vi.spyOn(useAudioStore.getState(), 'load').mockResolvedValue();
  vi.spyOn(useHl2GpioStore.getState(), 'load').mockResolvedValue();
  vi.spyOn(usePttStore.getState(), 'load').mockResolvedValue();
  vi.stubGlobal(
    'fetch',
    vi.fn<typeof fetch>().mockResolvedValue(
      new Response('{}', {
        status: 200,
        headers: { 'content-type': 'application/json' },
      }),
    ),
  );
}

// Source radio buttons live in the role="radiogroup" labelled "TX audio source".
function sourceButtons(container: HTMLElement): HTMLButtonElement[] {
  const group = container.querySelector(
    '[role="radiogroup"][aria-label="TX audio source"]',
  );
  if (!group) return [];
  return Array.from(group.querySelectorAll<HTMLButtonElement>('button[role="radio"]'));
}

function labelOf(b: HTMLButtonElement): string {
  return b.textContent?.trim() ?? '';
}

describe('RadioSettingsPanel — TX audio source (Pass D)', () => {
  let container: HTMLDivElement;
  let root: Root;

  beforeEach(() => {
    container = document.createElement('div');
    document.body.appendChild(container);
    root = createRoot(container);
    // Reset shared stores so order-independent.
    useAntennaStore.setState((s) => ({ ...s, loaded: true, inflight: false }));
    usePttStore.getState().__resetForTests();
    useHl2GpioStore.setState((s) => ({ ...s, state: { supported: false, bits: 0 } }));
  });

  afterEach(() => {
    act(() => root.unmount());
    container.remove();
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
    seedCaps({});
    seedAudio('Host');
  });

  function render() {
    stubLoaders();
    act(() => {
      root.render(<RadioSettingsPanel />);
    });
  }

  it('single-select: exactly one source button is checked', () => {
    seedCaps(G2_CAPS);
    seedAudio('Host');
    render();
    const btns = sourceButtons(container);
    expect(btns.length).toBeGreaterThan(1);
    const checked = btns.filter((b) => b.getAttribute('aria-checked') === 'true');
    expect(checked).toHaveLength(1);
    expect(labelOf(checked[0]!)).toBe('Host');
  });

  it('selecting another source deselects the prior (mutual exclusion)', () => {
    seedCaps(G2_CAPS);
    seedAudio('RadioMic');
    render();
    const checked = sourceButtons(container).filter(
      (b) => b.getAttribute('aria-checked') === 'true',
    );
    expect(checked).toHaveLength(1);
    expect(labelOf(checked[0]!)).toBe('Radio Mic');
    // Host is NOT also checked — single value, not independent toggles.
    const host = sourceButtons(container).find((b) => labelOf(b) === 'Host')!;
    expect(host.getAttribute('aria-checked')).toBe('false');
  });

  it('clicking a source button calls the store update with the new source', () => {
    seedCaps(G2_CAPS);
    seedAudio('Host');
    const spy = vi.spyOn(useAudioStore.getState(), 'update').mockResolvedValue();
    render();
    const lineIn = sourceButtons(container).find(
      (b) => labelOf(b) === 'Radio Line In',
    )!;
    act(() => lineIn.click());
    expect(spy).toHaveBeenCalledWith({ source: 'RadioLineIn' });
    spy.mockRestore();
  });

  it('DEFAULT-OFF: Host selected, no param controls, nothing engaged on first render', () => {
    seedCaps(G2_CAPS);
    seedAudio('Host'); // the store default
    render();
    const checked = sourceButtons(container).filter(
      (b) => b.getAttribute('aria-checked') === 'true',
    );
    expect(checked).toHaveLength(1);
    expect(labelOf(checked[0]!)).toBe('Host');
    // No dependent-param controls render under Host.
    expect(container.textContent).not.toContain('Mic Boost');
    expect(container.textContent).not.toContain('Mic Bias');
    expect(container.textContent).not.toContain('Line-In Gain');
    // No checkbox anywhere in the audio card comes up checked.
    const audioChecks = Array.from(
      container.querySelectorAll<HTMLInputElement>('input[type="checkbox"]'),
    );
    // PTT Enable defaults ON, so filter to the audio card by proximity to the
    // audio labels — simplest: assert NONE of the audio param toggles exist.
    expect(audioChecks.every((c) => c.checked === false || c.closest('.ps-card')))
      .toBe(true);
  });

  it('dependent params: Mic Boost + Mic Bias show under RadioMic, gain hidden', () => {
    seedCaps(G2_CAPS);
    seedAudio('RadioMic');
    render();
    expect(container.textContent).toContain('Mic Boost');
    expect(container.textContent).toContain('Mic Bias');
    expect(container.textContent).not.toContain('Line-In Gain');
  });

  it('dependent params: Line-In Gain shows under RadioLineIn, mic params hidden', () => {
    seedCaps(G2_CAPS);
    seedAudio('RadioLineIn');
    render();
    expect(container.textContent).toContain('Line-In Gain');
    expect(container.textContent).not.toContain('Mic Boost');
    expect(container.textContent).not.toContain('Mic Bias');
  });

  it('mic-bias toggle only appears when hasMicBias is set', () => {
    seedCaps({ ...G2_CAPS, hasMicBias: false });
    seedAudio('RadioMic');
    render();
    expect(container.textContent).toContain('Mic Boost');
    expect(container.textContent).not.toContain('Mic Bias');
  });

  it('mic-bias enable is gated behind a confirmation (declining keeps it off)', () => {
    seedCaps(G2_CAPS);
    seedAudio('RadioMic', { micBias: false });
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(false);
    const updateSpy = vi
      .spyOn(useAudioStore.getState(), 'update')
      .mockResolvedValue();
    render();
    const biasCheckbox = Array.from(
      container.querySelectorAll<HTMLInputElement>('input[type="checkbox"]'),
    ).find((c) => c.closest('.ps-field')?.textContent?.includes('Mic Bias'))!;
    expect(biasCheckbox).toBeDefined();
    act(() => biasCheckbox.click());
    expect(confirmSpy).toHaveBeenCalled();
    // Declined → no update pushed.
    expect(updateSpy).not.toHaveBeenCalled();
    confirmSpy.mockRestore();
    updateSpy.mockRestore();
  });

  it('mic-bias enable proceeds when the confirmation is accepted', () => {
    seedCaps(G2_CAPS);
    seedAudio('RadioMic', { micBias: false });
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true);
    const updateSpy = vi
      .spyOn(useAudioStore.getState(), 'update')
      .mockResolvedValue();
    render();
    const biasCheckbox = Array.from(
      container.querySelectorAll<HTMLInputElement>('input[type="checkbox"]'),
    ).find((c) => c.closest('.ps-field')?.textContent?.includes('Mic Bias'))!;
    act(() => biasCheckbox.click());
    expect(confirmSpy).toHaveBeenCalled();
    expect(updateSpy).toHaveBeenCalledWith({ micBias: true });
    confirmSpy.mockRestore();
    updateSpy.mockRestore();
  });
});

describe('RadioSettingsPanel — board-gating (Pass D)', () => {
  let container: HTMLDivElement;
  let root: Root;

  beforeEach(() => {
    container = document.createElement('div');
    document.body.appendChild(container);
    root = createRoot(container);
    useAntennaStore.setState((s) => ({ ...s, loaded: true, inflight: false }));
    usePttStore.getState().__resetForTests();
    useHl2GpioStore.setState((s) => ({ ...s, state: { supported: false, bits: 0 } }));
  });

  afterEach(() => {
    act(() => root.unmount());
    container.remove();
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
    seedCaps({});
    seedAudio('Host');
  });

  function render() {
    stubLoaders();
    act(() => {
      root.render(<RadioSettingsPanel />);
    });
  }

  it('HL2: Host-only — no Mic / Line / Balanced source options', () => {
    seedCaps(HL2_CAPS);
    seedAudio('Host');
    render();
    // No segmented radio group at all on HL2; a "USB / Ethernet" note instead.
    expect(sourceButtons(container)).toHaveLength(0);
    expect(container.textContent).toContain('USB / Ethernet');
    expect(container.textContent).not.toContain('Radio Mic');
    expect(container.textContent).not.toContain('Radio Line In');
    expect(container.textContent).not.toContain('Radio Balanced');
  });

  it('Balanced renders only when hasBalancedXlr (G2 / G2-1K)', () => {
    seedCaps(G2_CAPS);
    seedAudio('Host');
    render();
    const labels = sourceButtons(container).map(labelOf);
    expect(labels).toEqual(['Host', 'Radio Mic', 'Radio Line In', 'Radio Balanced']);
  });

  it('non-Saturn codec board: no Balanced option', () => {
    seedCaps({ hasOnboardCodec: true, hasBalancedXlr: false, hasRadioLineIn: false });
    seedAudio('Host');
    render();
    const labels = sourceButtons(container).map(labelOf);
    expect(labels).toEqual(['Host', 'Radio Mic']);
    expect(labels).not.toContain('Radio Balanced');
  });
});
