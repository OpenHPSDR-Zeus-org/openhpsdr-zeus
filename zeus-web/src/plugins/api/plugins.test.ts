// SPDX-License-Identifier: GPL-2.0-or-later
//
// Tests for the plugins REST client. Covers happy-path parsing for each
// endpoint plus the error envelope helpers.

import { afterEach, describe, expect, it, vi } from 'vitest';

import {
  fetchInstalledPlugins,
  fetchPlugin,
  fetchRegistry,
  installPlugin,
  parsePluginDto,
  parsePluginList,
  parseRegistryResponse,
  PluginsApiError,
  uninstallPlugin,
} from './plugins';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

describe('parsePluginDto', () => {
  it('coerces a complete payload', () => {
    const dto = parsePluginDto({
      id: 'hello',
      name: 'Hello World',
      version: '1.2.3',
      author: 'EI6LF',
      description: 'A sample plugin.',
      license: 'GPL-2.0-or-later',
      homepage: 'https://example.com',
      capabilities: ['hub:emit', 'storage'],
      ui: {
        modules: ['hello-ui.mjs'],
        panels: [{ id: 'p', title: 'Hello', icon: 'star', slot: 'workspace' }],
      },
      audio: {
        vst3Path: '/plugins/effect.vst3',
        slot: 'rx-post',
        channels: 2,
        sampleRate: 48000,
      },
    });
    expect(dto.id).toBe('hello');
    expect(dto.capabilities).toEqual(['hub:emit', 'storage']);
    expect(dto.ui?.panels[0]?.slot).toBe('workspace');
    expect(dto.audio?.sampleRate).toBe(48000);
  });

  it('falls back to safe defaults on garbage input', () => {
    const dto = parsePluginDto({});
    expect(dto.id).toBe('');
    expect(dto.capabilities).toEqual([]);
    expect(dto.ui).toBeNull();
    expect(dto.audio).toBeNull();
  });

  it('drops non-string entries from capability arrays', () => {
    const dto = parsePluginDto({ capabilities: ['ok', 42, null, 'also-ok'] });
    expect(dto.capabilities).toEqual(['ok', 'also-ok']);
  });
});

describe('parsePluginList', () => {
  it('shapes an empty list correctly', () => {
    const list = parsePluginList({ sdkAbi: 1, sdkVersion: '0.1', plugins: [] });
    expect(list.plugins).toEqual([]);
    expect(list.sdkAbi).toBe(1);
  });

  it('tolerates a missing plugins array', () => {
    const list = parsePluginList({ sdkAbi: 1 });
    expect(list.plugins).toEqual([]);
  });
});

describe('parseRegistryResponse', () => {
  it('parses catalog entries with versions', () => {
    const resp = parseRegistryResponse({
      sourceUrl: 'https://example.com/registry.json',
      catalog: {
        schemaVersion: 1,
        generated: '2026-05-17T00:00:00Z',
        plugins: [
          {
            id: 'demo',
            name: 'Demo',
            description: 'A demo entry.',
            author: 'EI6LF',
            license: 'GPL-2.0-or-later',
            categories: ['rx', 'ui'],
            verified: true,
            versions: [
              {
                version: '0.1.0',
                sdkAbi: 1,
                sdkMinVersion: '0.6.0',
                platforms: ['any'],
                downloadUrl: 'https://example.com/demo-0.1.0.zip',
                sha256: 'deadbeef',
              },
            ],
          },
        ],
      },
    });
    expect(resp.sourceUrl).toBe('https://example.com/registry.json');
    expect(resp.catalog.plugins).toHaveLength(1);
    expect(resp.catalog.plugins[0]?.verified).toBe(true);
    expect(resp.catalog.plugins[0]?.versions[0]?.downloadUrl).toContain('demo');
  });
});

describe('fetchers', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('fetchInstalledPlugins parses a 200 response', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>().mockResolvedValue(
        jsonResponse({
          sdkAbi: 1,
          sdkVersion: '0.6.0',
          plugins: [
            {
              id: 'a',
              name: 'A',
              version: '1.0.0',
              author: '',
              description: '',
              license: '',
              capabilities: [],
            },
          ],
        }),
      ),
    );
    const list = await fetchInstalledPlugins();
    expect(list.plugins).toHaveLength(1);
    expect(list.plugins[0]?.id).toBe('a');
  });

  it('fetchPlugin throws PluginsApiError on 404 with a json detail', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>().mockResolvedValue(
        new Response(JSON.stringify({ detail: 'no such plugin' }), {
          status: 404,
          headers: { 'content-type': 'application/json' },
        }),
      ),
    );
    await expect(fetchPlugin('missing')).rejects.toBeInstanceOf(PluginsApiError);
  });

  it('fetchRegistry surfaces a 502 with body detail', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>().mockResolvedValue(
        new Response(JSON.stringify({ detail: 'upstream offline' }), {
          status: 502,
          headers: { 'content-type': 'application/json' },
        }),
      ),
    );
    try {
      await fetchRegistry();
      throw new Error('expected throw');
    } catch (e) {
      expect(e).toBeInstanceOf(PluginsApiError);
      expect((e as PluginsApiError).status).toBe(502);
      expect((e as PluginsApiError).detail).toBe('upstream offline');
    }
  });

  it('installPlugin returns the parsed DTO and posts the camelCase body', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(
      jsonResponse({
        id: 'demo',
        name: 'Demo',
        version: '0.1.0',
        author: '',
        description: '',
        license: '',
        capabilities: [],
      }),
    );
    vi.stubGlobal('fetch', fetchMock);

    const dto = await installPlugin({
      source: 'url',
      url: 'https://example.com/demo.zip',
      sha256: 'a'.repeat(64),
    });

    expect(dto.id).toBe('demo');
    const firstCall = fetchMock.mock.calls[0];
    expect(firstCall).toBeDefined();
    const init = firstCall![1] as RequestInit;
    const body = JSON.parse(String(init.body)) as Record<string, unknown>;
    expect(body.source).toBe('url');
    expect(body.url).toBe('https://example.com/demo.zip');
    expect(body.sha256).toBe('a'.repeat(64));
  });

  it('installPlugin surfaces the install endpoint\'s { error } envelope on 400', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>().mockResolvedValue(
        new Response(JSON.stringify({ error: 'bad zip' }), {
          status: 400,
          headers: { 'content-type': 'application/json' },
        }),
      ),
    );
    try {
      await installPlugin({ source: 'url', url: 'https://example.com/x' });
      throw new Error('expected throw');
    } catch (e) {
      expect(e).toBeInstanceOf(PluginsApiError);
      expect((e as PluginsApiError).detail).toBe('bad zip');
    }
  });

  it('uninstallPlugin reports a 204 as immediate', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>().mockResolvedValue(new Response(null, { status: 204 })),
    );
    const r = await uninstallPlugin('demo');
    expect(r.status).toBe(204);
  });

  it('uninstallPlugin maps a 202 to a deferred result with a message', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>().mockResolvedValue(
        new Response(
          JSON.stringify({ detail: 'restart required', title: 'deferred' }),
          {
            status: 202,
            headers: { 'content-type': 'application/json' },
          },
        ),
      ),
    );
    const r = await uninstallPlugin('demo');
    expect(r.status).toBe(202);
    expect(r.message).toBe('restart required');
  });
});
