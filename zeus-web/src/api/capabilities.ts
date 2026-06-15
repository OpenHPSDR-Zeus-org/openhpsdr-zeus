// SPDX-License-Identifier: GPL-2.0-or-later
//
// Capabilities REST client. Mirrors GET /api/capabilities — host-mode +
// platform + per-feature gates. Fetched once on app mount; the response
// is treated as static for the lifetime of the page (the backend probe
// runs at startup and doesn't change without a server restart).

export type ZeusHostMode = 'desktop' | 'server';
export type ZeusPlatform = 'linux' | 'darwin' | 'windows' | 'unknown';

// Feature-gate fields will return as the new plugin system lands. Today
// the matrix is empty — kept as a stable shape so callers can read
// `capabilities.features` without runtime guards.
export type CapabilitiesFeatures = Record<string, never>;

export type Capabilities = {
  host: ZeusHostMode;
  platform: ZeusPlatform;
  architecture: string;
  version: string;
  lanHttpsUrls: string[];
  features: CapabilitiesFeatures;
};

function asString(v: unknown, fallback = ''): string {
  return typeof v === 'string' ? v : fallback;
}

function parseHostMode(v: unknown): ZeusHostMode {
  return v === 'desktop' ? 'desktop' : 'server';
}

function parsePlatform(v: unknown): ZeusPlatform {
  if (v === 'linux' || v === 'darwin' || v === 'windows') return v;
  return 'unknown';
}

function asStringArray(v: unknown): string[] {
  return Array.isArray(v) ? v.filter((item): item is string => typeof item === 'string') : [];
}

export function parseCapabilities(raw: unknown): Capabilities {
  const o = (raw ?? {}) as Record<string, unknown>;
  return {
    host: parseHostMode(o.host),
    platform: parsePlatform(o.platform),
    architecture: asString(o.architecture, 'unknown'),
    version: asString(o.version, 'unknown'),
    lanHttpsUrls: asStringArray(o.lanHttpsUrls),
    features: {} as CapabilitiesFeatures,
  };
}

export async function fetchCapabilities(
  signal?: AbortSignal,
): Promise<Capabilities> {
  const res = await fetch('/api/capabilities', { signal });
  if (!res.ok) {
    throw new Error(`/api/capabilities ${res.status} ${res.statusText}`);
  }
  return parseCapabilities(await res.json());
}

// ---------------------------------------------------------------- locality

const LOCALHOST_HOSTS = new Set([
  'localhost',
  '127.0.0.1',
  '::1',
  '[::1]',
  '',
]);

/**
 * True when the operator's browser is reaching Zeus via a loopback name —
 * i.e. the browser and the host process are on the same box. Plugin GUIs
 * (which open as native OS windows on the host's display) are reachable in
 * that case.
 *
 * Pass `getServerBaseUrl()` to honour Capacitor / standalone-host overrides.
 * Empty string falls back to the page's own host (the same-origin web flow).
 */
export function isLoopbackHost(serverBaseUrl: string): boolean {
  if (typeof window === 'undefined') return false;
  let hostname: string;
  try {
    const target = serverBaseUrl?.trim()
      ? new URL(serverBaseUrl)
      : new URL(window.location.href);
    hostname = target.hostname.toLowerCase();
  } catch {
    return false;
  }
  return LOCALHOST_HOSTS.has(hostname);
}
