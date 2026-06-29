// SPDX-License-Identifier: GPL-2.0-or-later
//
// Runtime that loads installed-plugin UI modules at app startup. The
// host serves each module at /api/plugins/{id}/ui/{file}; we
// dynamic-import them, hand the module's default export a small API
// surface (registerPanel + callBackend), and capture every panel the
// plugin registers so it can show up in the workspace Add Panel modal.

import { createElement, type ComponentType } from 'react';
import { fetchInstalledPlugins, type PluginDto, type PluginPanelDto } from '../api/plugins';
import { GenericVstPanel } from '../../components/GenericVstPanel';

// UI slots the Audio Suite rack + sidebar render. A plugin that targets
// an audio chain but ships no UI module of its own (a scanned VST)
// gets a synthetic panel on the matching slot so it still appears in the rack.
const CHAIN_UI_SLOT = 'tx-audio-tools.chain';
const RX_CHAIN_UI_SLOT = 'rx-audio-tools.chain';

export interface ZeusPluginApi {
  registerPanel(spec: { id: string; component: ComponentType }): void;
  callBackend(method: string, path: string, body?: unknown): Promise<Response>;
}

export interface RegisteredPluginPanel {
  panelId: string;
  pluginId: string;
  title: string;
  icon: string;
  category: string;
  // Manifest's panel slot — e.g. "workspace.amplifier" for an Add-Panel
  // tile, "tx-audio-tools.chain" for a TX audio-chain block. Consumers
  // filter on this when rendering plugin contributions into a specific
  // surface (the chain panel below CFC, for instance).
  slot: string;
  component: ComponentType;
  // True for a scanned VST3 with no inline UI module: its only "panel"
  // is its native editor window. The rack slot opens that window on a
  // header click instead of expanding an (empty) collapsible body.
  editorBacked?: boolean;
}

type PluginModule = {
  default?: (api: ZeusPluginApi) => void;
};

const registered = new Map<string, RegisteredPluginPanel>();
const listeners = new Set<() => void>();
let loading: Promise<void> | null = null;

function emit() {
  for (const fn of listeners) fn();
}

function makeApi(plugin: PluginDto, registerPanel: (spec: { id: string; component: ComponentType }) => void): ZeusPluginApi {
  return {
    registerPanel,
    callBackend(method, path, body) {
      const url = `/api/plugins/${encodeURIComponent(plugin.id)}${path.startsWith('/') ? path : '/' + path}`;
      const init: RequestInit = { method };
      if (body !== undefined) {
        init.headers = { 'content-type': 'application/json' };
        init.body = JSON.stringify(body);
      }
      return fetch(url, init);
    },
  };
}

/**
 * Register a synthetic generic panel for an audio plugin that ships no
 * UI module — i.e. a VST registered via "Add VST directory". TX-chain VSTs
 * get a TX rack panel; RX-chain VSTs get a receive insert rack panel.
 * Idempotent: overwrites any prior synthetic entry for the same id.
 */
function maybeRegisterGenericAudioPanel(plugin: PluginDto): void {
  const audio = plugin.audio;
  if (!audio) return;
  const audioSlot = audio.slot ?? '';
  const slot = audioSlot.startsWith('rx')
    ? RX_CHAIN_UI_SLOT
    : audioSlot.startsWith('tx')
      ? CHAIN_UI_SLOT
      : null;
  if (slot === null) return;
  const route = slot === RX_CHAIN_UI_SLOT ? 'rx' : 'tx';
  const name = plugin.name;
  const id = plugin.id;
  registered.set(`${id}::generic`, {
    panelId: 'generic',
    pluginId: id,
    title: name,
    icon: '',
    category: 'audio',
    slot,
    component: () => createElement(GenericVstPanel, { pluginId: id, name, route }),
    editorBacked: true,
  });
}

async function loadOne(plugin: PluginDto): Promise<void> {
  if (!plugin.ui || plugin.ui.modules.length === 0) {
    // No managed UI module. If it's a TX-chain audio plugin (a scanned
    // VST), give it a synthetic generic panel so it still shows in the
    // Audio Suite rack and can be reordered / parked.
    maybeRegisterGenericAudioPanel(plugin);
    return;
  }

  const panelMetaById = new Map<string, PluginPanelDto>();
  for (const p of plugin.ui.panels) panelMetaById.set(p.id, p);

  const captured = new Map<string, ComponentType>();
  const register = (spec: { id: string; component: ComponentType }) => {
    captured.set(spec.id, spec.component);
  };
  const api = makeApi(plugin, register);

  for (const modulePath of plugin.ui.modules) {
    const url = `/api/plugins/${encodeURIComponent(plugin.id)}/${modulePath.replace(/^\/+/, '')}`;
    try {
      const mod = (await import(/* @vite-ignore */ url)) as PluginModule;
      if (typeof mod.default === 'function') {
        mod.default(api);
      } else {
        console.warn(`[plugin/${plugin.id}] module ${modulePath} has no default export`);
      }
    } catch (err) {
      console.error(`[plugin/${plugin.id}] failed to load ${modulePath}`, err);
    }
  }

  for (const [panelId, component] of captured) {
    const meta = panelMetaById.get(panelId);
    if (!meta) {
      console.warn(`[plugin/${plugin.id}] registered panel '${panelId}' not declared in manifest`);
      continue;
    }
    registered.set(`${plugin.id}::${panelId}`, {
      panelId,
      pluginId: plugin.id,
      title: meta.title,
      icon: meta.icon,
      category: meta.category,
      slot: meta.slot,
      component,
    });
  }
}

export async function loadInstalledPluginUis(): Promise<void> {
  if (loading) return loading;
  loading = (async () => {
    const list = await fetchInstalledPlugins();
    await Promise.all(list.plugins.map(loadOne));
    emit();
  })();
  return loading;
}

/**
 * Re-fetch the installed-plugin list and (re)register panels — used
 * after "Add VST directory" installs new plugins so the rack updates
 * without a full page reload. loadOne is idempotent (registered.set
 * overwrites), and dynamic imports are browser-cached, so re-running is
 * cheap and safe.
 *
 * Also prunes panels whose plugin is no longer installed — so an
 * uninstalled VST drops out of the Audio Suite rack / sidebar
 * immediately instead of lingering as a ghost entry.
 */
export async function reloadInstalledPluginUis(): Promise<void> {
  const list = await fetchInstalledPlugins();
  const liveIds = new Set(list.plugins.map((p) => p.id));
  for (const [key, panel] of registered) {
    if (!liveIds.has(panel.pluginId)) registered.delete(key);
  }
  await Promise.all(list.plugins.map(loadOne));
  emit();
}

export function listRegisteredPanels(): RegisteredPluginPanel[] {
  return Array.from(registered.values());
}

export function subscribe(fn: () => void): () => void {
  listeners.add(fn);
  return () => listeners.delete(fn);
}
