// SPDX-License-Identifier: GPL-2.0-or-later
//
// Plugin REST client. Mirrors the endpoints exposed by Zeus.Plugins.Host
// (PluginEndpoints.cs). All five operators land here so the UI never
// touches /api/plugins directly:
//
//   GET    /api/plugins             → PluginListResponse
//   GET    /api/plugins/{id}        → PluginDto
//   GET    /api/plugins/registry    → RegistryResponse
//   POST   /api/plugins/install     → PluginDto | { error }
//   DELETE /api/plugins/{id}        → 204 / 202 (deferred)
//
// The web build defaults to same-origin (`/api/...`); Capacitor builds and
// the Settings → Server URL panel can swap to a LAN base, and the
// installFetchInterceptor() patch in serverUrl.ts rewrites our paths
// transparently. We use plain `fetch` here so that path stays honoured.

// ---------------------------------------------------------------------------
// Wire DTOs. Pascal-case property names on the .NET side serialise to
// camelCase via ASP.NET's default JsonNamingPolicy, so we mirror in camelCase
// here. Shape parity with Zeus.Plugins.Host/PluginEndpoints.cs and
// Zeus.Plugins.Contracts/Registry/RegistryCatalog.cs.
// ---------------------------------------------------------------------------

export type PluginPanelDto = {
  id: string;
  title: string;
  icon: string;
  slot: string;
  /** Add Panel modal category — see PanelCategory in layout/panels.ts. */
  category: string;
};

export type PluginUiDto = {
  modules: string[];
  panels: PluginPanelDto[];
};

export type PluginAudioDto = {
  vst3Path?: string | null;
  slot: string;
  channels: number;
  sampleRate: number;
};

export type PluginDto = {
  id: string;
  /**
   * True for operator-scanned VST3 / Audio Unit plugins (server id
   * namespaces `com.openhpsdr.zeus.{vst,rxvst,au,rxau}.*`). These live only in
   * the Audio Suite rack; the Settings ▸ Plugins list filters them out so it
   * shows just Zeus plugin-repo plugins. Defaults false for older servers.
   */
  scanned: boolean;
  name: string;
  version: string;
  author: string;
  description: string;
  homepage?: string | null;
  license: string;
  capabilities: string[];
  ui?: PluginUiDto | null;
  audio?: PluginAudioDto | null;
};

export type PluginListResponse = {
  sdkAbi: number;
  sdkVersion: string;
  plugins: PluginDto[];
};

export type RegistryPluginVersion = {
  version: string;
  sdkAbi: number;
  sdkMinVersion: string;
  platforms: string[];
  downloadUrl: string;
  sha256: string;
};

export type RegistryPluginEntry = {
  id: string;
  name: string;
  description: string;
  author: string;
  license: string;
  homepage?: string | null;
  categories: string[];
  verified: boolean;
  versions: RegistryPluginVersion[];
};

export type RegistryCatalog = {
  schemaVersion: number;
  generated: string;
  plugins: RegistryPluginEntry[];
};

export type RegistryResponse = {
  sourceUrl: string;
  catalog: RegistryCatalog;
};

export type InstallSource = 'url' | 'file' | 'registry';

export type InstallRequest = {
  source: InstallSource;
  url?: string;
  filePath?: string;
  sha256?: string;
  id?: string;
  version?: string;
};

// ---------------------------------------------------------------------------
// Coercion helpers — shape every payload defensively. A missing field falls
// back to a typed default so consumers can rely on the model contract.
// ---------------------------------------------------------------------------

function asString(v: unknown, fallback = ''): string {
  return typeof v === 'string' ? v : fallback;
}

function asNumber(v: unknown, fallback = 0): number {
  return typeof v === 'number' && Number.isFinite(v) ? v : fallback;
}

function asBool(v: unknown, fallback = false): boolean {
  return typeof v === 'boolean' ? v : fallback;
}

function asStringArray(v: unknown): string[] {
  if (!Array.isArray(v)) return [];
  return v.filter((x): x is string => typeof x === 'string');
}

function parsePanel(raw: unknown): PluginPanelDto {
  const o = (raw ?? {}) as Record<string, unknown>;
  return {
    id: asString(o.id),
    title: asString(o.title),
    icon: asString(o.icon),
    slot: asString(o.slot),
    category: asString(o.category, 'plugins'),
  };
}

function parseUi(raw: unknown): PluginUiDto | null {
  if (raw == null) return null;
  const o = raw as Record<string, unknown>;
  return {
    modules: asStringArray(o.modules),
    panels: Array.isArray(o.panels) ? o.panels.map(parsePanel) : [],
  };
}

function parseAudio(raw: unknown): PluginAudioDto | null {
  if (raw == null) return null;
  const o = raw as Record<string, unknown>;
  return {
    vst3Path: typeof o.vst3Path === 'string' ? o.vst3Path : null,
    slot: asString(o.slot),
    channels: asNumber(o.channels),
    sampleRate: asNumber(o.sampleRate),
  };
}

export function parsePluginDto(raw: unknown): PluginDto {
  const o = (raw ?? {}) as Record<string, unknown>;
  return {
    id: asString(o.id),
    scanned: asBool(o.scanned),
    name: asString(o.name),
    version: asString(o.version),
    author: asString(o.author),
    description: asString(o.description),
    homepage: typeof o.homepage === 'string' ? o.homepage : null,
    license: asString(o.license),
    capabilities: asStringArray(o.capabilities),
    ui: parseUi(o.ui),
    audio: parseAudio(o.audio),
  };
}

export function parsePluginList(raw: unknown): PluginListResponse {
  const o = (raw ?? {}) as Record<string, unknown>;
  const plugins = Array.isArray(o.plugins) ? o.plugins.map(parsePluginDto) : [];
  return {
    sdkAbi: asNumber(o.sdkAbi),
    sdkVersion: asString(o.sdkVersion),
    plugins,
  };
}

function parseRegistryVersion(raw: unknown): RegistryPluginVersion {
  const o = (raw ?? {}) as Record<string, unknown>;
  return {
    version: asString(o.version),
    sdkAbi: asNumber(o.sdkAbi),
    sdkMinVersion: asString(o.sdkMinVersion),
    platforms: asStringArray(o.platforms),
    downloadUrl: asString(o.downloadUrl),
    sha256: asString(o.sha256),
  };
}

function parseRegistryEntry(raw: unknown): RegistryPluginEntry {
  const o = (raw ?? {}) as Record<string, unknown>;
  return {
    id: asString(o.id),
    name: asString(o.name),
    description: asString(o.description),
    author: asString(o.author),
    license: asString(o.license),
    homepage: typeof o.homepage === 'string' ? o.homepage : null,
    categories: asStringArray(o.categories),
    verified: asBool(o.verified),
    versions: Array.isArray(o.versions)
      ? o.versions.map(parseRegistryVersion)
      : [],
  };
}

function parseRegistryCatalog(raw: unknown): RegistryCatalog {
  const o = (raw ?? {}) as Record<string, unknown>;
  return {
    schemaVersion: asNumber(o.schemaVersion, 1),
    generated: asString(o.generated),
    plugins: Array.isArray(o.plugins) ? o.plugins.map(parseRegistryEntry) : [],
  };
}

export function parseRegistryResponse(raw: unknown): RegistryResponse {
  const o = (raw ?? {}) as Record<string, unknown>;
  return {
    sourceUrl: asString(o.sourceUrl),
    catalog: parseRegistryCatalog(o.catalog),
  };
}

// ---------------------------------------------------------------------------
// Fetch wrappers.
// ---------------------------------------------------------------------------

/** Thrown by every helper when the server responds non-2xx. */
export class PluginsApiError extends Error {
  readonly status: number;
  readonly detail?: string;

  constructor(message: string, status: number, detail?: string) {
    super(message);
    this.name = 'PluginsApiError';
    this.status = status;
    this.detail = detail;
  }
}

async function readErrorDetail(res: Response): Promise<string | undefined> {
  // The install endpoint emits `{ error: "..." }` for validation failures;
  // other endpoints may use `{ detail, title }` (Results.Problem). Read
  // whichever we get and surface a readable string to the caller.
  try {
    const ct = res.headers.get('content-type') ?? '';
    if (ct.includes('application/json')) {
      const body = (await res.json()) as Record<string, unknown>;
      const err = body.error ?? body.detail ?? body.title;
      if (typeof err === 'string' && err.length > 0) return err;
    } else {
      const text = await res.text();
      if (text.trim().length > 0) return text.slice(0, 400);
    }
  } catch {
    // Body unavailable — caller still gets status + statusText.
  }
  return undefined;
}

async function failWith(res: Response, path: string): Promise<never> {
  const detail = await readErrorDetail(res);
  const detailSuffix = detail ? ` — ${detail}` : '';
  throw new PluginsApiError(
    `${path} ${res.status} ${res.statusText}${detailSuffix}`,
    res.status,
    detail,
  );
}

export async function fetchInstalledPlugins(
  signal?: AbortSignal,
): Promise<PluginListResponse> {
  const res = await fetch('/api/plugins', { signal });
  if (!res.ok) await failWith(res, '/api/plugins');
  return parsePluginList(await res.json());
}

export async function fetchPlugin(
  id: string,
  signal?: AbortSignal,
): Promise<PluginDto> {
  const path = `/api/plugins/${encodeURIComponent(id)}`;
  const res = await fetch(path, { signal });
  if (!res.ok) await failWith(res, path);
  return parsePluginDto(await res.json());
}

export async function fetchRegistry(
  signal?: AbortSignal,
): Promise<RegistryResponse> {
  const res = await fetch('/api/plugins/registry', { signal });
  if (!res.ok) await failWith(res, '/api/plugins/registry');
  return parseRegistryResponse(await res.json());
}

export async function installPlugin(
  req: InstallRequest,
  signal?: AbortSignal,
): Promise<PluginDto> {
  const res = await fetch('/api/plugins/install', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(req),
    signal,
  });
  if (!res.ok) await failWith(res, '/api/plugins/install');
  return parsePluginDto(await res.json());
}

export type UninstallResult = {
  /**
   * 204 — host removed the plugin immediately.
   * 202 — host deferred the removal (restart required, e.g. assembly is
   *       still loaded). UI should advise the operator to restart.
   */
  status: 204 | 202;
  /** Optional message returned with a 202. */
  message?: string;
};

export async function uninstallPlugin(
  id: string,
  signal?: AbortSignal,
): Promise<UninstallResult> {
  const path = `/api/plugins/${encodeURIComponent(id)}`;
  const res = await fetch(path, { method: 'DELETE', signal });
  if (res.status === 204) return { status: 204 };
  if (res.status === 202) {
    const message = await readErrorDetail(res);
    return { status: 202, message };
  }
  await failWith(res, path);
  // Unreachable — failWith throws.
  throw new PluginsApiError('unreachable', res.status);
}
