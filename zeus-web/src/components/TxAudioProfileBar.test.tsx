// SPDX-License-Identifier: GPL-2.0-or-later

/** @vitest-environment jsdom */

import { createElement } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { act, render } from './meters/__tests__/harness';
import { useTxAudioProfileStore } from '../state/tx-audio-profile-store';
import { TxAudioProfileBar } from './TxAudioProfileBar';

async function flush(): Promise<void> {
  for (let i = 0; i < 8; i++) await Promise.resolve();
}

describe('TxAudioProfileBar', () => {
  beforeEach(() => {
    useTxAudioProfileStore.setState({
      profiles: [
        {
          id: 'studio-ssb',
          name: 'Studio SSB',
          micGainDb: 0,
          levelerMaxGainDb: 8,
          txLeveling: {
            alcMaxGainDb: 3,
            alcDecayMs: 10,
            levelerEnabled: true,
            levelerDecayMs: 100,
            compressorEnabled: false,
            compressorGainDb: 0,
          },
          cfcConfig: { enabled: false, postEqEnabled: false, preCompDb: 0, prePeqDb: 0, bands: [] },
          lowCutHz: 150,
          highCutHz: 2850,
          processingMode: 'native',
          masterBypass: false,
          chainOrder: [],
          chainParked: [],
          vstPluginStates: {},
          nativePluginStates: {},
          targetSpectralDensity: 55,
          createdUtc: '',
          updatedUtc: '',
        },
      ],
      loaded: true,
      lastLoadedId: 'studio-ssb',
      busy: false,
      load: vi.fn(async () => {}),
      save: vi.fn(async () => ({ ok: true })),
      apply: vi.fn(async () => ({ ok: true })),
      remove: vi.fn(async () => ({ ok: true })),
    });
  });

  afterEach(() => {
    vi.clearAllMocks();
  });

  it('renders the saved profiles and shows last-loaded as selected', async () => {
    const { container, unmount } = render(createElement(TxAudioProfileBar));
    await act(flush);
    const trigger = container.querySelector('[aria-label="TX audio profile"]') as HTMLButtonElement;
    expect(trigger.textContent).toContain('Studio SSB [Native]');
    unmount();
  });

  it('applies the profile when a dropdown option is chosen', async () => {
    const apply = vi.fn(async () => ({ ok: true }));
    useTxAudioProfileStore.setState({ apply });
    const { container, unmount } = render(createElement(TxAudioProfileBar));
    await act(flush);
    const trigger = container.querySelector('[aria-label="TX audio profile"]') as HTMLButtonElement;
    await act(async () => {
      trigger.click();
      await flush();
    });
    const option = Array.from(
      container.querySelectorAll<HTMLButtonElement>('.tx-audio-profile-option:not(.tx-audio-profile-option--placeholder)'),
    ).find((el) => el.textContent === 'Studio SSB [Native]')!;
    await act(async () => {
      option.click();
      await flush();
    });
    expect(apply).toHaveBeenCalledWith('studio-ssb');
    unmount();
  });

  it('saves the selected profile in place when Save is pressed', async () => {
    const save = vi.fn(async () => ({ ok: true }));
    useTxAudioProfileStore.setState({ save });
    const { container, unmount } = render(createElement(TxAudioProfileBar));
    await act(flush);

    // A profile is selected (lastLoadedId = studio-ssb), so Save overwrites it
    // directly by name — no name dialog.
    const saveBtn = container.querySelector('[aria-label="Save TX audio profile"]') as HTMLButtonElement;
    await act(async () => {
      saveBtn.click();
      await flush();
    });
    expect(save).toHaveBeenCalledWith('Studio SSB');
    expect(document.querySelector('.text-input-dialog-field input')).toBeNull();
    unmount();
  });

  it('opens the name dialog and saves by name via the New button', async () => {
    const save = vi.fn(async () => ({ ok: true }));
    useTxAudioProfileStore.setState({ save });
    const { container, unmount } = render(createElement(TxAudioProfileBar));
    await act(flush);

    const newBtn = container.querySelector('[aria-label="New TX audio profile"]') as HTMLButtonElement;
    await act(async () => {
      newBtn.click();
      await flush();
    });

    // The name dialog appears (rendered into the document body via the modal).
    const input = document.querySelector('.text-input-dialog-field input') as HTMLInputElement;
    expect(input).not.toBeNull();
    await act(async () => {
      // Use the native value setter so React's onChange sees the update.
      const setter = Object.getOwnPropertyDescriptor(
        window.HTMLInputElement.prototype,
        'value',
      )!.set!;
      setter.call(input, 'My Voice');
      input.dispatchEvent(new Event('input', { bubbles: true }));
      await flush();
    });
    const confirm = Array.from(document.querySelectorAll('button')).find(
      (b) => b.textContent === 'Save Profile',
    ) as HTMLButtonElement;
    await act(async () => {
      confirm.click();
      await flush();
    });
    expect(save).toHaveBeenCalledWith('My Voice');
    unmount();
  });
});
