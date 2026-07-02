// SPDX-License-Identifier: GPL-2.0-or-later
//
// digital-plugin-store tests — the Zeus Digital mode gate. installed = the
// plugin id appears in the installed list; live = GET /status answers 2xx
// (404 = not activated this boot, 503 = shut-down instance — both NOT live).
// Re-probe triggers: installed-list change and app-WS reconnect.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  isDigitalPluginReady,
  useDigitalPluginStore,
  wsjtxLiveSubset,
} from './digital-plugin-store';
import { usePluginsStore } from '../plugins/state/plugins-store';
import { useDisplayStore } from './display-store';
import { DIGITAL_PLUGIN_BASE, DIGITAL_PLUGIN_ID } from '../api/digital-plugin';
import { parsePluginDto } from '../plugins/api/plugins';

const flush = () => new Promise((r) => setTimeout(r, 0));

/** Route-aware fetch stub: /status answers `statusCode`; /api/plugins answers
 *  the given installed list; anything else 200-empty (config pushes etc.). */
function stubFetch(statusCode: number, pluginIds: string[] = []) {
  const fn = vi.fn(async (input: RequestInfo | URL) => {
    const url = String(input);
    if (url === `${DIGITAL_PLUGIN_BASE}/status`) {
      return {
        ok: statusCode >= 200 && statusCode < 300,
        status: statusCode,
        headers: new Headers(),
        json: async () => ({ ok: true }),
      };
    }
    if (url === '/api/plugins') {
      return {
        ok: true,
        status: 200,
        headers: new Headers(),
        json: async () => ({
          sdkAbi: 1,
          sdkVersion: '1.2.0',
          plugins: pluginIds.map((id) => ({ id, scanned: false, name: id, version: '1.0.0' })),
        }),
      };
    }
    return { ok: true, status: 200, headers: new Headers(), json: async () => ({}) };
  });
  vi.stubGlobal('fetch', fn as never);
  return fn;
}

describe('digital-plugin-store', () => {
  beforeEach(() => {
    stubFetch(200);
    useDigitalPluginStore.setState({ installed: false, live: false, probed: false, sseConnected: false });
    usePluginsStore.setState({ installed: [] });
    useDisplayStore.setState({ connected: false });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('probe marks live on a 2xx /status', async () => {
    await useDigitalPluginStore.getState().probe();
    expect(useDigitalPluginStore.getState().live).toBe(true);
    expect(useDigitalPluginStore.getState().probed).toBe(true);
  });

  it('probe marks NOT live on 404 (installed but not restarted)', async () => {
    stubFetch(404);
    await useDigitalPluginStore.getState().probe();
    expect(useDigitalPluginStore.getState().live).toBe(false);
    expect(useDigitalPluginStore.getState().probed).toBe(true);
  });

  it('probe marks NOT live on 503 (zombie-route guard after shutdown)', async () => {
    stubFetch(503);
    await useDigitalPluginStore.getState().probe();
    expect(useDigitalPluginStore.getState().live).toBe(false);
  });

  it('probe marks NOT live on a network error', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => Promise.reject(new Error('offline'))) as never);
    await useDigitalPluginStore.getState().probe();
    expect(useDigitalPluginStore.getState().live).toBe(false);
    expect(useDigitalPluginStore.getState().probed).toBe(true);
  });

  it('installed follows the plugins-store list and re-probes on change', async () => {
    const fn = stubFetch(200);
    usePluginsStore.setState({
      installed: [parsePluginDto({ id: DIGITAL_PLUGIN_ID, name: 'Zeus Digital' })],
    });
    await flush();
    expect(useDigitalPluginStore.getState().installed).toBe(true);
    expect(fn.mock.calls.some((c) => String(c[0]) === `${DIGITAL_PLUGIN_BASE}/status`)).toBe(true);

    usePluginsStore.setState({ installed: [] });
    await flush();
    expect(useDigitalPluginStore.getState().installed).toBe(false);
  });

  it('another plugin id does not open the gate', async () => {
    usePluginsStore.setState({
      installed: [parsePluginDto({ id: 'com.kb2uka.rf2k', name: 'RF2K' })],
    });
    await flush();
    expect(useDigitalPluginStore.getState().installed).toBe(false);
  });

  it('isDigitalPluginReady requires BOTH installed and live', () => {
    useDigitalPluginStore.setState({ installed: true, live: false });
    expect(isDigitalPluginReady()).toBe(false);
    useDigitalPluginStore.setState({ installed: false, live: true });
    expect(isDigitalPluginReady()).toBe(false);
    useDigitalPluginStore.setState({ installed: true, live: true });
    expect(isDigitalPluginReady()).toBe(true);
  });

  it('re-probes on an app-WS reconnect (server restarted under the tab)', async () => {
    const fn = stubFetch(200, [DIGITAL_PLUGIN_ID]);
    useDisplayStore.setState({ connected: true }); // rising edge
    await flush();
    const urls = fn.mock.calls.map((c) => String(c[0]));
    expect(urls).toContain('/api/plugins');
    expect(urls).toContain(`${DIGITAL_PLUGIN_BASE}/status`);
    expect(useDigitalPluginStore.getState().live).toBe(true);
  });
});

describe('wsjtxLiveSubset', () => {
  const base = {
    enabled: true,
    host: '127.0.0.1',
    port: 2237,
    instanceId: 'WSJT-X',
    transport: 'unicast' as const,
    multicastGroup: '224.0.0.73',
    multicastTtl: 1,
    sendLiveDecodes: true,
  };

  it('unicast: forwards host, enabled = enabled && sendLiveDecodes', () => {
    expect(wsjtxLiveSubset(base)).toEqual({
      enabled: true,
      host: '127.0.0.1',
      port: 2237,
      multicast: false,
      instanceId: 'WSJT-X',
      multicastTtl: 1,
    });
  });

  it('multicast: the group becomes the host, id + TTL forwarded', () => {
    expect(wsjtxLiveSubset({ ...base, transport: 'multicast', instanceId: 'ZeusDigi', multicastTtl: 4 })).toEqual({
      enabled: true,
      host: '224.0.0.73',
      port: 2237,
      multicast: true,
      instanceId: 'ZeusDigi',
      multicastTtl: 4,
    });
  });

  it('live decodes off ⇒ disabled even when the broadcaster is enabled', () => {
    expect(wsjtxLiveSubset({ ...base, sendLiveDecodes: false }).enabled).toBe(false);
  });
});
