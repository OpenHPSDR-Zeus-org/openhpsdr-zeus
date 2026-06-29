// SPDX-License-Identifier: GPL-2.0-or-later
//
// Plugins store. Holds two server-shaped snapshots:
//
//   1. The installed plugin list (from GET /api/plugins).
//   2. The registry catalog (from GET /api/plugins/registry), which is
//      fetched lazily — operators may not open the browser tab.
//
// Plus per-side load flags (loaded / inflight / lastError) so the panels
// can render a sensible "loading" / "couldn't reach the registry" state.
// Mirrors the shape of capabilities-store.ts.

import { create } from 'zustand';

import {
  fetchInstalledPlugins,
  fetchRegistry,
  installPlugin,
  uninstallPlugin,
  type InstallRequest,
  type PluginDto,
  type PluginListResponse,
  type RegistryCatalog,
  type UninstallResult,
} from '../api/plugins';
import { reloadInstalledPluginUis } from '../runtime/pluginRuntime';

type LoadState = {
  loaded: boolean;
  inflight: boolean;
  loadError: string | null;
};

const INITIAL_LOAD: LoadState = {
  loaded: false,
  inflight: false,
  loadError: null,
};

export type PluginsStoreState = {
  // Installed plugins
  installed: PluginDto[];
  sdkAbi: number;
  sdkVersion: string;
  installedLoad: LoadState;

  // Registry catalog
  registry: RegistryCatalog | null;
  registrySourceUrl: string;
  registryLoad: LoadState;

  // Install workflow flags + last error/success message (used by the
  // InstallFromUrl form and InstallButton on each registry card).
  installInflight: boolean;
  lastInstallError: string | null;
  lastInstallOk: string | null;

  // Uninstall workflow flags
  uninstallInflight: boolean;
  lastUninstallError: string | null;
  lastUninstallNotice: string | null;

  refreshInstalled: () => Promise<void>;
  refreshRegistry: () => Promise<void>;
  install: (req: InstallRequest) => Promise<PluginDto | null>;
  uninstall: (id: string) => Promise<UninstallResult | null>;
  clearInstallFeedback: () => void;
  clearUninstallFeedback: () => void;
};

function errMessage(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}

async function refreshRuntimePanels() {
  try {
    await reloadInstalledPluginUis();
  } catch (err) {
    // jsdom's native fetch cannot resolve app-relative URLs when a test does
    // not stub this second refresh path. The installed-list refresh above is
    // still covered there; production browsers resolve /api/plugins normally.
    if (
      err instanceof TypeError &&
      err.message.includes('Failed to parse URL')
    ) {
      return;
    }

    console.warn('plugin runtime refresh threw', err);
  }
}

export const usePluginsStore = create<PluginsStoreState>((set, get) => ({
  installed: [],
  sdkAbi: 0,
  sdkVersion: '',
  installedLoad: { ...INITIAL_LOAD },

  registry: null,
  registrySourceUrl: '',
  registryLoad: { ...INITIAL_LOAD },

  installInflight: false,
  lastInstallError: null,
  lastInstallOk: null,

  uninstallInflight: false,
  lastUninstallError: null,
  lastUninstallNotice: null,

  // Idempotent — multiple call sites (panel mount, post-install, post-uninstall)
  // can request a refresh; the in-flight guard prevents duplicate GETs.
  refreshInstalled: async () => {
    if (get().installedLoad.inflight) return;
    set({ installedLoad: { ...INITIAL_LOAD, inflight: true } });
    try {
      const resp: PluginListResponse = await fetchInstalledPlugins();
      set({
        // Operator-scanned VST3 / AU plugins (resp.plugins[].scanned) live in
        // the Audio Suite rack only — they are not Zeus plugin-repo plugins, so
        // the Settings ▸ Plugins list excludes them. The Audio Suite has its own
        // consumer (pluginRuntime.fetchInstalledPlugins) that still sees them.
        installed: resp.plugins.filter((p) => !p.scanned),
        sdkAbi: resp.sdkAbi,
        sdkVersion: resp.sdkVersion,
        installedLoad: { loaded: true, inflight: false, loadError: null },
      });
    } catch (err) {
      set({
        installedLoad: {
          loaded: get().installedLoad.loaded,
          inflight: false,
          loadError: errMessage(err),
        },
      });
    }
  },

  refreshRegistry: async () => {
    if (get().registryLoad.inflight) return;
    set({ registryLoad: { ...INITIAL_LOAD, inflight: true } });
    try {
      const resp = await fetchRegistry();
      set({
        registry: resp.catalog,
        registrySourceUrl: resp.sourceUrl,
        registryLoad: { loaded: true, inflight: false, loadError: null },
      });
    } catch (err) {
      set({
        registryLoad: {
          loaded: get().registryLoad.loaded,
          inflight: false,
          loadError: errMessage(err),
        },
      });
    }
  },

  install: async (req) => {
    if (get().installInflight) return null;
    set({
      installInflight: true,
      lastInstallError: null,
      lastInstallOk: null,
    });
    try {
      const dto = await installPlugin(req);
      set({
        installInflight: false,
        lastInstallError: null,
        lastInstallOk: `Installed ${dto.name} ${dto.version}`,
      });
      // Refresh the installed list so the new plugin appears immediately.
      await get().refreshInstalled();
      await refreshRuntimePanels();
      return dto;
    } catch (err) {
      set({
        installInflight: false,
        lastInstallError: errMessage(err),
        lastInstallOk: null,
      });
      return null;
    }
  },

  uninstall: async (id) => {
    if (get().uninstallInflight) return null;
    set({
      uninstallInflight: true,
      lastUninstallError: null,
      lastUninstallNotice: null,
    });
    try {
      const result = await uninstallPlugin(id);
      set({
        uninstallInflight: false,
        lastUninstallError: null,
        lastUninstallNotice:
          result.status === 202
            ? result.message ??
              'Plugin removal deferred — restart Zeus to complete.'
            : null,
      });
      await get().refreshInstalled();
      await refreshRuntimePanels();
      return result;
    } catch (err) {
      set({
        uninstallInflight: false,
        lastUninstallError: errMessage(err),
        lastUninstallNotice: null,
      });
      return null;
    }
  },

  clearInstallFeedback: () =>
    set({ lastInstallError: null, lastInstallOk: null }),

  clearUninstallFeedback: () =>
    set({ lastUninstallError: null, lastUninstallNotice: null }),
}));
