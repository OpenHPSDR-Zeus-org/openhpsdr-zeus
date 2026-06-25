// SPDX-License-Identifier: GPL-2.0-or-later

/** @vitest-environment jsdom */

// Regression: scanned third-party VST3 / Audio Unit effects must NOT appear in
// the workspace Add Panel ("+") menu — they live only in the Audio Suite (VST
// host) rack. Those are editor-backed synthetic panels. First-party native
// audio-suite plugins (real UI modules, not editor-backed) MAY still appear.
// Guards the getAllPanels filter against a full AU-registry scan flooding the
// workspace picker with every system effect (issue #827 / #844 follow-up).

import '../../components/meters/__tests__/harness'; // localStorage polyfill side-effect
import { describe, expect, it, beforeEach, vi } from 'vitest';

const mock = vi.hoisted(() => ({ list: [] as unknown[] }));
vi.mock('../../plugins/runtime/pluginRuntime', () => ({
  listRegisteredPanels: () => mock.list,
}));

import { getAllPanels, PANELS } from '../panels';

const builtInCount = Object.values(PANELS).length;

function panel(over: Record<string, unknown>) {
  return {
    panelId: 'generic',
    pluginId: 'x',
    title: 'X',
    icon: '',
    category: 'audio',
    slot: 'tx-audio-tools.chain',
    component: () => null,
    ...over,
  };
}

describe('getAllPanels — third-party VST/AU exclusion from the workspace menu', () => {
  beforeEach(() => {
    mock.list = [];
  });

  it('excludes editor-backed scanned VST3 / AU plugin panels', () => {
    mock.list = [
      panel({ pluginId: 'com.openhpsdr.zeus.au.someverb', title: 'SomeVerb', editorBacked: true }),
      panel({ pluginId: 'com.openhpsdr.zeus.vst.somecomp', title: 'SomeComp', editorBacked: true, slot: 'rx-audio-tools.chain' }),
    ];
    const all = getAllPanels();
    expect(all.length).toBe(builtInCount); // only built-ins survive
    expect(all.some((p) => p.tags.includes('com.openhpsdr.zeus.au.someverb'))).toBe(false);
    expect(all.some((p) => p.tags.includes('com.openhpsdr.zeus.vst.somecomp'))).toBe(false);
  });

  it('keeps first-party native audio-suite plugin panels (not editor-backed)', () => {
    mock.list = [
      panel({ pluginId: 'com.openhpsdr.zeus.samples.eq', title: 'EQ', editorBacked: false }),
    ];
    const all = getAllPanels();
    expect(all.length).toBe(builtInCount + 1);
    expect(all.some((p) => p.tags.includes('com.openhpsdr.zeus.samples.eq'))).toBe(true);
  });

  it('mixes correctly: native stays, third-party drops', () => {
    mock.list = [
      panel({ pluginId: 'native.gate', editorBacked: false }),
      panel({ pluginId: 'au.thirdparty', editorBacked: true }),
    ];
    const tags = getAllPanels().flatMap((p) => p.tags);
    expect(tags).toContain('native.gate');
    expect(tags).not.toContain('au.thirdparty');
  });
});
