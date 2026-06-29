// SPDX-License-Identifier: GPL-2.0-or-later
//
// MidiSettingsPanel — MIDI tab settings test (issue #18). Verifies the
// default-OFF form, that the command catalogue + device list render, that the
// enable checkbox calls the store, and that a learned control can be bound. The
// harness import installs the localStorage polyfill + act flag before the store
// loads; store methods are stubbed so no real fetch fires.

import { describe, expect, it, beforeEach, vi } from 'vitest';
import { createElement } from 'react';
import { act, render } from './meters/__tests__/harness';
import { MidiSettingsPanel } from './MidiSettingsPanel';
import { useMidiStore } from '../state/midi-store';
import { EMPTY_BINDINGS, type MidiCommandInfo, type MidiStatus } from '../api/midi';

const COMMANDS: MidiCommandInfo[] = [
  { command: 'Band40m', label: 'Band 40m', controlType: 'Button', isToggle: false, supported: true },
  { command: 'SetAfGain', label: 'AF Gain', controlType: 'KnobOrSlider', isToggle: false, supported: true },
  { command: 'Band2m', label: 'Band 2m', controlType: 'Button', isToggle: false, supported: false },
];

const STATUS: MidiStatus = {
  enabled: false,
  midiEngineAvailable: true,
  streamDeckEngineAvailable: false,
  midiDevices: [{ name: 'DJ2GO2', connected: true }],
  streamDeckDevices: [],
  learning: false,
};

function clickByText(container: HTMLElement, text: string) {
  const btn = Array.from(container.querySelectorAll('button')).find((b) => b.textContent === text);
  expect(btn, `button "${text}"`).toBeTruthy();
  act(() => (btn as HTMLButtonElement).click());
}

describe('MidiSettingsPanel', () => {
  beforeEach(() => {
    act(() => {
      useMidiStore.setState({
        config: { enabled: false, bindings: EMPTY_BINDINGS },
        status: STATUS,
        commands: COMMANDS,
        lastLearn: null,
        refreshStatus: async () => {},
        refreshCommands: async () => {},
        refreshConfig: async () => {},
        setEnabled: async () => {},
        upsertMapping: async () => {},
        removeMapping: async () => {},
        upsertStreamDeckMapping: async () => {},
        removeStreamDeckMapping: async () => {},
        startLearn: async () => {},
        stopLearn: async () => {},
      } as never);
    });
  });

  it('renders the MIDI section with the device list, default disabled', () => {
    const { container, unmount } = render(createElement(MidiSettingsPanel));
    expect(container.textContent).toContain('MIDI Controller');
    expect(container.textContent).toContain('DJ2GO2');
    const enabled = container.querySelector('input[type="checkbox"]') as HTMLInputElement;
    expect(enabled.checked).toBe(false);
    unmount();
  });

  it('toggling enable calls the store', () => {
    const setEnabled = vi.fn(async () => {});
    act(() => useMidiStore.setState({ setEnabled } as never));
    const { container, unmount } = render(createElement(MidiSettingsPanel));
    const enabled = container.querySelector('input[type="checkbox"]') as HTMLInputElement;
    act(() => enabled.click());
    expect(setEnabled).toHaveBeenCalledWith(true);
    unmount();
  });

  it('LEARN button calls startLearn', () => {
    const startLearn = vi.fn(async () => {});
    act(() => useMidiStore.setState({ startLearn } as never));
    const { container, unmount } = render(createElement(MidiSettingsPanel));
    clickByText(container, 'LEARN');
    expect(startLearn).toHaveBeenCalled();
    unmount();
  });

  it('binds a learned control to a chosen command', () => {
    const upsertMapping = vi.fn(async () => {});
    act(() =>
      useMidiStore.setState({
        upsertMapping,
        status: { ...STATUS, learning: true },
        lastLearn: { deviceName: 'DJ2GO2', controlId: 'cc:0:7', controlType: 'KnobOrSlider', value: 80, delta: 0 },
      } as never),
    );
    const { container, unmount } = render(createElement(MidiSettingsPanel));
    // The learn card shows the detected control + a command picker.
    expect(container.textContent).toContain('cc:0:7');
    const select = container.querySelector('select') as HTMLSelectElement;
    const proto = window.HTMLSelectElement.prototype;
    const setter = Object.getOwnPropertyDescriptor(proto, 'value')!.set!;
    act(() => {
      setter.call(select, 'SetAfGain');
      select.dispatchEvent(new Event('change', { bubbles: true }));
    });
    clickByText(container, 'BIND');
    expect(upsertMapping).toHaveBeenCalledWith(
      expect.objectContaining({ deviceName: 'DJ2GO2', controlId: 'cc:0:7', command: 'SetAfGain' }),
    );
    unmount();
  });

  it('renders parity commands flagged in the picker', () => {
    act(() =>
      useMidiStore.setState({
        status: { ...STATUS, learning: true },
        lastLearn: { deviceName: 'DJ2GO2', controlId: 'note:0:1', controlType: 'Button', value: 127, delta: 0 },
      } as never),
    );
    const { container, unmount } = render(createElement(MidiSettingsPanel));
    const select = container.querySelector('select') as HTMLSelectElement;
    const opts = Array.from(select.querySelectorAll('option')).map((o) => o.textContent);
    expect(opts.some((t) => t?.includes('Band 2m') && t?.includes('(parity)'))).toBe(true);
    unmount();
  });
});
