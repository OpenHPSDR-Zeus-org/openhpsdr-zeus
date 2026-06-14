// SPDX-License-Identifier: GPL-2.0-or-later
//
// usePluginsStore — happy/error paths for the four async actions and the
// post-install refresh chain.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { usePluginsStore } from './plugins-store';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

function resetStore() {
  usePluginsStore.setState({
    installed: [],
    sdkAbi: 0,
    sdkVersion: '',
    installedLoad: { loaded: false, inflight: false, loadError: null },
    registry: null,
    registrySourceUrl: '',
    registryLoad: { loaded: false, inflight: false, loadError: null },
    installInflight: false,
    lastInstallError: null,
    lastInstallOk: null,
    uninstallInflight: false,
    lastUninstallError: null,
    lastUninstallNotice: null,
  });
}

describe('usePluginsStore', () => {
  beforeEach(() => resetStore());
  afterEach(() => vi.unstubAllGlobals());

  it('refreshInstalled hydrates the installed list', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>().mockResolvedValue(
        jsonResponse({
          sdkAbi: 1,
          sdkVersion: '0.6.0',
          plugins: [
            {
              id: 'demo',
              name: 'Demo',
              version: '0.1.0',
              author: '',
              description: '',
              license: 'GPL',
              capabilities: ['hub:emit'],
            },
          ],
        }),
      ),
    );

    await usePluginsStore.getState().refreshInstalled();

    const s = usePluginsStore.getState();
    expect(s.installedLoad.loaded).toBe(true);
    expect(s.installed).toHaveLength(1);
    expect(s.installed[0]?.id).toBe('demo');
    expect(s.sdkAbi).toBe(1);
  });

  it('refreshInstalled records loadError on a 500', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>().mockResolvedValue(
        new Response('boom', { status: 500 }),
      ),
    );

    await usePluginsStore.getState().refreshInstalled();
    const s = usePluginsStore.getState();
    expect(s.installedLoad.loaded).toBe(false);
    expect(s.installedLoad.loadError).not.toBeNull();
  });

  it('refreshRegistry hydrates the catalog snapshot', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>().mockResolvedValue(
        jsonResponse({
          sourceUrl: 'https://example.com/registry.json',
          catalog: {
            schemaVersion: 1,
            generated: '2026-05-17T00:00:00Z',
            plugins: [],
          },
        }),
      ),
    );

    await usePluginsStore.getState().refreshRegistry();
    const s = usePluginsStore.getState();
    expect(s.registryLoad.loaded).toBe(true);
    expect(s.registrySourceUrl).toBe('https://example.com/registry.json');
    expect(s.registry?.schemaVersion).toBe(1);
  });

  it('install posts the request, then refreshes the installed list', async () => {
    // Three server hits: POST /install → DTO, then GET /plugins twice — once for
    // the store list (refreshInstalled), once for the runtime panel rack
    // (refreshRuntimePanels → reloadInstalledPluginUis).
    const installedList = () =>
      jsonResponse({
        sdkAbi: 1,
        sdkVersion: '0.6.0',
        plugins: [
          {
            id: 'demo',
            name: 'Demo',
            version: '0.1.0',
            author: '',
            description: '',
            license: '',
            capabilities: [],
          },
        ],
      });
    const fetchMock = vi
      .fn<typeof fetch>()
      .mockResolvedValueOnce(
        jsonResponse({
          id: 'demo',
          name: 'Demo',
          version: '0.1.0',
          author: '',
          description: '',
          license: '',
          capabilities: [],
        }),
      )
      .mockResolvedValueOnce(installedList())
      .mockResolvedValueOnce(installedList());
    vi.stubGlobal('fetch', fetchMock);

    const dto = await usePluginsStore.getState().install({
      source: 'registry',
      id: 'demo',
      version: '0.1.0',
    });

    expect(dto?.id).toBe('demo');
    const s = usePluginsStore.getState();
    expect(s.lastInstallOk).toMatch(/Installed Demo 0\.1\.0/);
    expect(s.installed).toHaveLength(1);
    expect(fetchMock).toHaveBeenCalledTimes(3);

    const installCall = fetchMock.mock.calls[0];
    expect(installCall).toBeDefined();
    const installInit = installCall![1] as RequestInit;
    expect(installInit.method).toBe('POST');
    const body = JSON.parse(String(installInit.body)) as Record<string, unknown>;
    expect(body.source).toBe('registry');
    expect(body.id).toBe('demo');
  });

  it('install records lastInstallError on a 400 envelope', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>().mockResolvedValue(
        new Response(JSON.stringify({ error: 'unknown source' }), {
          status: 400,
          headers: { 'content-type': 'application/json' },
        }),
      ),
    );

    const out = await usePluginsStore.getState().install({
      source: 'registry',
      id: 'bad',
    });
    expect(out).toBeNull();
    const s = usePluginsStore.getState();
    expect(s.lastInstallOk).toBeNull();
    expect(s.lastInstallError).toContain('unknown source');
  });

  it('uninstall surfaces a 202 as a deferred notice and triggers a refresh', async () => {
    const fetchMock = vi
      .fn<typeof fetch>()
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ detail: 'restart required' }), {
          status: 202,
          headers: { 'content-type': 'application/json' },
        }),
      )
      .mockResolvedValueOnce(
        jsonResponse({ sdkAbi: 1, sdkVersion: '0.6.0', plugins: [] }),
      );
    vi.stubGlobal('fetch', fetchMock);

    const result = await usePluginsStore.getState().uninstall('demo');
    expect(result?.status).toBe(202);
    const s = usePluginsStore.getState();
    expect(s.lastUninstallNotice).toBe('restart required');
    expect(s.installed).toEqual([]);
  });

  it('clearInstallFeedback / clearUninstallFeedback zero the flash slots', () => {
    usePluginsStore.setState({
      lastInstallError: 'x',
      lastInstallOk: 'y',
      lastUninstallError: 'z',
      lastUninstallNotice: 'q',
    });
    usePluginsStore.getState().clearInstallFeedback();
    usePluginsStore.getState().clearUninstallFeedback();
    const s = usePluginsStore.getState();
    expect(s.lastInstallError).toBeNull();
    expect(s.lastInstallOk).toBeNull();
    expect(s.lastUninstallError).toBeNull();
    expect(s.lastUninstallNotice).toBeNull();
  });
});
