// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// Per-tile config shape for the URL Embed panel — a multi-instance
// workspace tile that frames an arbitrary web page in an iframe. The
// operator types a URL into the panel's address bar; submitting it
// persists that URL to this tile's instance config, so the page is
// reloaded on the next session. Each instance holds its own URL, so an
// operator can pin as many pages as they like.
//
// Persistence shape stays minimal: the assigned URL plus an optional
// custom title. The URL is the source of truth; the iframe src is driven
// straight from `config.url`.

export interface UrlEmbedConfig {
  schemaVersion: 1;
  /** Assigned page URL. Empty string = no page pinned yet. */
  url: string;
  /** Operator label override. Empty = derive from the URL host. */
  title: string;
}

export const EMPTY_URL_EMBED_CONFIG: UrlEmbedConfig = {
  schemaVersion: 1,
  url: '',
  title: '',
};

/** Best-effort parse of an unknown JSON blob from the workspace store.
 *  Tolerates missing fields by filling defaults; re-runs the assigned URL
 *  through the sanitizer so a tampered blob can't smuggle a `javascript:`
 *  scheme into the iframe. */
export function parseUrlEmbedConfig(raw: unknown): UrlEmbedConfig {
  if (!raw || typeof raw !== 'object') return { ...EMPTY_URL_EMBED_CONFIG };
  const r = raw as Record<string, unknown>;
  const url =
    typeof r.url === 'string' ? normalizeEmbedUrl(r.url) ?? '' : '';
  const title = typeof r.title === 'string' ? r.title.trim() : '';
  return { schemaVersion: 1, url, title };
}

/** Normalise operator-typed input into a safe http(s) URL, or null when it
 *  can't be one. A bare host (`example.com`) gets an `https://` prefix;
 *  anything that doesn't resolve to an http/https origin (javascript:,
 *  data:, file:, malformed) is rejected so it never reaches the iframe
 *  `src`. */
export function normalizeEmbedUrl(raw: string): string | null {
  const trimmed = raw.trim();
  if (!trimmed) return null;
  // No scheme → assume https. The scheme test wants `name://` so a
  // scheme-only string like `javascript:alert(1)` (no `//`) falls through
  // to the https prefix and then fails URL parsing below.
  const candidate = /^[a-zA-Z][a-zA-Z0-9+.-]*:\/\//.test(trimmed)
    ? trimmed
    : `https://${trimmed}`;
  let parsed: URL;
  try {
    parsed = new URL(candidate);
  } catch {
    return null;
  }
  if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') return null;
  if (!parsed.hostname) return null;
  return parsed.toString();
}

/** Tile-header label for a pinned URL: operator title if set, else the
 *  page host, else a generic fallback. */
export function urlEmbedTitle(config: UrlEmbedConfig): string {
  if (config.title) return config.title;
  if (config.url) {
    try {
      return new URL(config.url).host || 'URL Embed';
    } catch {
      return 'URL Embed';
    }
  }
  return 'URL Embed';
}
