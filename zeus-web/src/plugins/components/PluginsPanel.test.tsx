// SPDX-License-Identifier: GPL-2.0-or-later
//
// Component-level smoke tests for the Plugins panel and its three children.
// We mirror the dependency-free render harness used by SettingsMenu.test.tsx
// (raw createRoot + React.act) so the new tests don't introduce
// @testing-library/react.
//
// Pattern: pre-seed the store so the mount useEffect (which auto-refreshes
// when `loaded === false`) sees a settled state and skips its fetch. That
// keeps every render synchronous — no chained Promise.resolve() flushes
// inside act(), no risk of an async-act loop if a future test stub fails.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act } from 'react';
import { createRoot, type Root } from 'react-dom/client';

import { PluginsPanel } from './PluginsPanel';
import { InstalledPlugins } from './InstalledPlugins';
import { PluginBrowser } from './PluginBrowser';
import { InstallFromUrl } from './InstallFromUrl';
import { usePluginsStore } from '../state/plugins-store';
import type { PluginDto, RegistryCatalog } from '../api/plugins';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

const EMPTY_INSTALLED = {
  installed: [] as PluginDto[],
  sdkAbi: 1,
  sdkVersion: '0.6.0',
  installedLoad: { loaded: true, inflight: false, loadError: null },
};

const EMPTY_REGISTRY = {
  registry: {
    schemaVersion: 1,
    generated: '2026-05-17T00:00:00Z',
    plugins: [],
  } as RegistryCatalog,
  registrySourceUrl: 'https://example.com/registry.json',
  registryLoad: { loaded: true, inflight: false, loadError: null },
};

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

function makeRoot() {
  const container = document.createElement('div');
  document.body.appendChild(container);
  const root = createRoot(container);
  return { container, root };
}

// React 19's controlled-input flow tracks the value via the native
// HTMLInputElement.prototype setter. Setting `input.value = '...'`
// directly bypasses React's instrumentation and the synthetic onChange
// never fires — so we go through the prototype descriptor.
function typeInto(input: HTMLInputElement, value: string) {
  const setter = Object.getOwnPropertyDescriptor(
    HTMLInputElement.prototype,
    'value',
  )!.set!;
  setter.call(input, value);
  input.dispatchEvent(new Event('input', { bubbles: true }));
}

// Flush queued work after an async user action (click / form submit).
// Several plugin flows do fetch() -> .json() -> setState and then dynamic
// import() the runtime registry refresh, so include one macrotask turn.
async function flush() {
  for (let i = 0; i < 4; i += 1) {
    await act(async () => {
      await Promise.resolve();
    });
  }
  await act(async () => {
    await new Promise((resolve) => window.setTimeout(resolve, 0));
  });
}

describe('PluginsPanel — sub-tab routing', () => {
  let container: HTMLDivElement;
  let root: Root;

  beforeEach(() => {
    resetStore();
    // Seed both load slices as "loaded" so the auto-refresh effects on
    // InstalledPlugins / PluginBrowser become no-ops. The default tab is
    // Installed; the user clicks through to the others.
    usePluginsStore.setState({ ...EMPTY_INSTALLED, ...EMPTY_REGISTRY });
    const m = makeRoot();
    container = m.container;
    root = m.root;
  });

  afterEach(() => {
    act(() => root.unmount());
    container.remove();
    vi.unstubAllGlobals();
  });

  it('defaults to the Installed tab', () => {
    act(() => {
      root.render(<PluginsPanel />);
    });
    expect(
      container.querySelector('[data-testid="plugins-installed"]'),
    ).not.toBeNull();
    expect(container.querySelector('[data-testid="plugins-browser"]')).toBeNull();
  });

  it('switches to the Browse tab on click', () => {
    act(() => {
      root.render(<PluginsPanel />);
    });
    const browseTab = Array.from(
      container.querySelectorAll<HTMLButtonElement>('[role="tab"]'),
    ).find((b) => b.textContent?.includes('BROWSE'));
    expect(browseTab).toBeDefined();
    act(() => {
      browseTab!.click();
    });
    expect(
      container.querySelector('[data-testid="plugins-browser"]'),
    ).not.toBeNull();
  });

  it('switches to the InstallFromUrl tab on click', () => {
    act(() => {
      root.render(<PluginsPanel />);
    });
    const fromUrlTab = Array.from(
      container.querySelectorAll<HTMLButtonElement>('[role="tab"]'),
    ).find((b) => b.textContent?.includes('INSTALL FROM URL'));
    expect(fromUrlTab).toBeDefined();
    act(() => {
      fromUrlTab!.click();
    });
    expect(
      container.querySelector('[data-testid="plugins-install-from-url"]'),
    ).not.toBeNull();
  });
});

describe('InstalledPlugins', () => {
  let container: HTMLDivElement;
  let root: Root;

  beforeEach(() => {
    resetStore();
    const m = makeRoot();
    container = m.container;
    root = m.root;
  });

  afterEach(() => {
    act(() => root.unmount());
    container.remove();
    vi.unstubAllGlobals();
  });

  it('renders a card per installed plugin', () => {
    usePluginsStore.setState({
      ...EMPTY_INSTALLED,
      installed: [
        {
          id: 'demo',
          scanned: false,
          name: 'Demo Plugin',
          version: '0.1.0',
          author: 'EI6LF',
          description: 'A demo.',
          homepage: null,
          license: 'GPL-2.0-or-later',
          capabilities: ['hub:emit'],
          ui: null,
          audio: null,
        },
      ],
    });

    act(() => {
      root.render(<InstalledPlugins />);
    });

    expect(container.textContent).toContain('Demo Plugin');
    expect(container.textContent).toContain('hub:emit');
    expect(container.textContent).toContain('SDK ABI v1');
  });

  it('renders the empty state when loaded with no plugins', () => {
    usePluginsStore.setState(EMPTY_INSTALLED);
    act(() => {
      root.render(<InstalledPlugins />);
    });
    expect(container.textContent).toContain('No plugins installed yet');
  });

  it('exposes the loadError surface on the store', () => {
    // The panel renders role="alert" with the loadError message; we verify
    // the slice that drives that render here. The full DOM path is
    // exercised under the post-uninstall test, which exits via state too.
    usePluginsStore.setState({
      ...EMPTY_INSTALLED,
      installedLoad: {
        loaded: true,
        inflight: false,
        loadError: 'connection refused',
      },
    });
    act(() => {
      root.render(<InstalledPlugins />);
    });
    expect(container.textContent).toContain('connection refused');
    expect(
      container.querySelector<HTMLElement>('[role="alert"]'),
    ).not.toBeNull();
  });

  it('confirms before uninstalling, then calls DELETE /api/plugins/{id}', async () => {
    usePluginsStore.setState({
      ...EMPTY_INSTALLED,
      installed: [
        {
          id: 'demo',
          scanned: false,
          name: 'Demo Plugin',
          version: '0.1.0',
          author: '',
          description: '',
          homepage: null,
          license: '',
          capabilities: [],
          ui: null,
          audio: null,
        },
      ],
    });
    // Each call returns a FRESH Response — Response bodies are single-use
    // streams in jsdom, so re-using one across calls would throw on the
    // second .json() and re-arm the useEffect via loadError.
    const fetchMock = vi.fn<typeof fetch>().mockImplementation((_input, init) => {
      if (init?.method === 'DELETE') {
        return Promise.resolve(new Response(null, { status: 204 }));
      }
      return Promise.resolve(
        jsonResponse({ sdkAbi: 1, sdkVersion: '', plugins: [] }),
      );
    });
    vi.stubGlobal('fetch', fetchMock);
    act(() => {
      root.render(<InstalledPlugins />);
    });
    const findButton = (label: string) =>
      Array.from(container.querySelectorAll<HTMLButtonElement>('button')).find(
        (b) => b.textContent?.trim() === label,
      );

    const uninstallBtn = findButton('UNINSTALL');
    expect(uninstallBtn).toBeDefined();
    await act(async () => {
      uninstallBtn!.click();
    });
    await flush();

    const cancelDialog = container.querySelector<HTMLElement>(
      '[role="alertdialog"]',
    );
    expect(cancelDialog?.textContent).toContain('Uninstall Demo Plugin');
    const cancelBtn = findButton('Cancel');
    expect(cancelBtn).toBeDefined();
    await act(async () => {
      cancelBtn!.click();
    });
    await flush();
    expect(
      fetchMock.mock.calls.some(
        (c) => (c[1] as RequestInit | undefined)?.method === 'DELETE',
      ),
    ).toBe(false);

    await act(async () => {
      uninstallBtn!.click();
    });
    await flush();
    const confirmBtn = findButton('Uninstall');
    expect(confirmBtn).toBeDefined();
    await act(async () => {
      confirmBtn!.click();
    });
    await flush();

    const deleteCall = fetchMock.mock.calls.find(
      (c) => (c[1] as RequestInit | undefined)?.method === 'DELETE',
    );
    expect(deleteCall).toBeDefined();
    expect(deleteCall![0]).toBe('/api/plugins/demo');
  });
});

describe('PluginBrowser', () => {
  let container: HTMLDivElement;
  let root: Root;

  beforeEach(() => {
    resetStore();
    const m = makeRoot();
    container = m.container;
    root = m.root;
  });

  afterEach(() => {
    act(() => root.unmount());
    container.remove();
    vi.unstubAllGlobals();
  });

  it('shows the source URL and a card per registry entry', () => {
    usePluginsStore.setState({
      ...EMPTY_REGISTRY,
      registry: {
        schemaVersion: 1,
        generated: '2026-05-17T00:00:00Z',
        plugins: [
          {
            id: 'demo',
            name: 'Demo',
            description: 'A demo entry.',
            author: 'EI6LF',
            license: 'GPL-2.0-or-later',
            homepage: null,
            categories: ['rx'],
            verified: true,
            versions: [
              {
                version: '0.1.0',
                sdkAbi: 1,
                sdkMinVersion: '0.6.0',
                platforms: ['any'],
                downloadUrl: 'https://example.com/demo-0.1.0.zip',
                sha256: 'a'.repeat(64),
              },
            ],
          },
        ],
      },
    });
    act(() => {
      root.render(<PluginBrowser />);
    });
    expect(container.textContent).toContain('https://example.com/registry.json');
    expect(container.textContent).toContain('Demo');
    expect(container.textContent).toContain('VERIFIED');
    expect(container.textContent).toContain('latest v0.1.0');
  });

  it('install button posts a registry-source payload', async () => {
    usePluginsStore.setState({
      ...EMPTY_INSTALLED,
      ...EMPTY_REGISTRY,
      registry: {
        schemaVersion: 1,
        generated: '2026-05-17T00:00:00Z',
        plugins: [
          {
            id: 'demo',
            name: 'Demo',
            description: '',
            author: '',
            license: '',
            homepage: null,
            categories: [],
            verified: false,
            versions: [
              {
                version: '0.1.0',
                sdkAbi: 1,
                sdkMinVersion: '0.6.0',
                platforms: ['any'],
                downloadUrl: 'https://example.com/demo.zip',
                sha256: 'a'.repeat(64),
              },
            ],
          },
        ],
      },
    });
    const fetchMock = vi.fn<typeof fetch>().mockImplementation((input) => {
      if (typeof input === 'string' && input === '/api/plugins/install') {
        return Promise.resolve(
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
      }
      return Promise.resolve(
        jsonResponse({ sdkAbi: 1, sdkVersion: '', plugins: [] }),
      );
    });
    vi.stubGlobal('fetch', fetchMock);

    act(() => {
      root.render(<PluginBrowser />);
    });

    const installBtn = Array.from(
      container.querySelectorAll<HTMLButtonElement>('button'),
    ).find((b) => b.textContent?.trim() === 'INSTALL');
    expect(installBtn).toBeDefined();
    await act(async () => {
      installBtn!.click();
    });
    await flush();

    const installCall = fetchMock.mock.calls.find(
      (c) => c[0] === '/api/plugins/install',
    );
    expect(installCall).toBeDefined();
    const body = JSON.parse(String(installCall![1]!.body)) as Record<
      string,
      unknown
    >;
    expect(body.source).toBe('registry');
    expect(body.id).toBe('demo');
    expect(body.version).toBe('0.1.0');
  });

  it('renders a registry load error when the slice has one', () => {
    usePluginsStore.setState({
      ...EMPTY_REGISTRY,
      registry: null,
      registryLoad: {
        loaded: true,
        inflight: false,
        loadError: 'upstream offline',
      },
    });
    act(() => {
      root.render(<PluginBrowser />);
    });
    expect(container.textContent).toContain('upstream offline');
  });
});

describe('InstallFromUrl', () => {
  let container: HTMLDivElement;
  let root: Root;

  beforeEach(() => {
    resetStore();
    const m = makeRoot();
    container = m.container;
    root = m.root;
  });

  afterEach(() => {
    act(() => root.unmount());
    container.remove();
    vi.unstubAllGlobals();
  });

  it('disables the submit button until a valid URL is entered', () => {
    act(() => {
      root.render(<InstallFromUrl />);
    });
    const submit = container.querySelector<HTMLButtonElement>(
      'button[type="submit"]',
    );
    expect(submit).not.toBeNull();
    expect(submit!.disabled).toBe(true);

    const urlInput = container.querySelector<HTMLInputElement>(
      '#plugin-install-url',
    );
    expect(urlInput).not.toBeNull();
    act(() => {
      typeInto(urlInput!, 'https://example.com/demo.zip');
    });
    expect(submit!.disabled).toBe(false);
  });

  it('rejects a non-hex SHA-256 with an inline message', () => {
    act(() => {
      root.render(<InstallFromUrl />);
    });
    const urlInput = container.querySelector<HTMLInputElement>(
      '#plugin-install-url',
    )!;
    const shaInput = container.querySelector<HTMLInputElement>(
      '#plugin-install-sha',
    )!;
    act(() => {
      typeInto(urlInput, 'https://example.com/demo.zip');
    });
    act(() => {
      typeInto(shaInput, 'not-hex');
    });
    expect(container.textContent).toContain(
      'SHA-256 must be 64 hex characters',
    );
    const submit = container.querySelector<HTMLButtonElement>(
      'button[type="submit"]',
    )!;
    expect(submit.disabled).toBe(true);
  });

  it('posts the install request on submit', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockImplementation((input) => {
      if (typeof input === 'string' && input === '/api/plugins/install') {
        return Promise.resolve(
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
      }
      return Promise.resolve(
        jsonResponse({ sdkAbi: 1, sdkVersion: '', plugins: [] }),
      );
    });
    vi.stubGlobal('fetch', fetchMock);

    act(() => {
      root.render(<InstallFromUrl />);
    });
    const urlInput = container.querySelector<HTMLInputElement>(
      '#plugin-install-url',
    )!;
    act(() => {
      typeInto(urlInput, 'https://example.com/demo.zip');
    });

    const form = container.querySelector('form')!;
    await act(async () => {
      form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    });
    await flush();

    const postCall = fetchMock.mock.calls.find(
      (c) => c[0] === '/api/plugins/install',
    );
    expect(postCall).toBeDefined();
    const body = JSON.parse(String(postCall![1]!.body)) as Record<
      string,
      unknown
    >;
    expect(body.source).toBe('url');
    expect(body.url).toBe('https://example.com/demo.zip');
    expect('sha256' in body).toBe(false); // empty SHA omitted
  });
});
