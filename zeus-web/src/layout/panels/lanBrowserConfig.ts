// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// Per-tile config shape for the LAN Browser panel. It builds on the URL-Embed
// shape (assigned url + title) and adds a persisted bookmark bar so an operator
// can pin the device pages they reach often (router, rotator, amp, another
// SDR's console) and jump between them with one click — handy when operating
// remotely, where each page is fetched through the radio host.
//
// Bookmarks persist in this tile's instance config, so they survive a reload /
// reconnect. Each bookmark is sanitised through normalizeLanUrl on the way in
// (both when the operator adds one and when a stored blob is parsed) so a
// tampered blob can never smuggle a non-http(s) scheme into the address bar.

import {
  EMPTY_URL_EMBED_CONFIG,
  parseUrlEmbedConfig,
  type UrlEmbedConfig,
} from './urlEmbedConfig';

/** A single pinned LAN page: the (sanitised) URL plus an optional label. */
export interface LanBookmark {
  url: string;
  /** Operator label override. Empty = derive from the URL host. */
  label: string;
}

export interface LanBrowserConfig extends UrlEmbedConfig {
  /** Pinned LAN pages, in operator order. */
  bookmarks: LanBookmark[];
}

export const EMPTY_LAN_BROWSER_CONFIG: LanBrowserConfig = {
  ...EMPTY_URL_EMBED_CONFIG,
  bookmarks: [],
};

// A device address can sit on any private host; the LAN Browser only ever
// reaches RFC1918 / IPv6-ULA targets (the SERVER is the real guard). Cap the
// stored list so a tampered blob can't bloat the layout store unboundedly.
const MAX_BOOKMARKS = 64;

/** Normalise operator input into a private-LAN http(s) URL, defaulting a bare
 *  host to http:// (device admin pages are overwhelmingly plain HTTP). Returns
 *  null for anything that isn't an http/https URL. The SERVER is the real guard
 *  (rejects non-private targets); this is just input hygiene. Shared by the
 *  panel's address bar and the bookmark parser so both sanitise identically. */
export function normalizeLanUrl(raw: string): string | null {
  const trimmed = raw.trim();
  if (!trimmed) return null;
  const candidate = /^[a-zA-Z][a-zA-Z0-9+.-]*:\/\//.test(trimmed)
    ? trimmed
    : `http://${trimmed}`;
  try {
    const parsed = new URL(candidate);
    if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') return null;
    if (!parsed.hostname) return null;
    return parsed.toString();
  } catch {
    return null;
  }
}

/** Best-effort parse of an unknown JSON blob from the workspace store. Reuses
 *  the URL-Embed parser for url/title, then layers on a sanitised, de-duped
 *  bookmark list. Tolerates a missing/legacy blob (no bookmarks field). */
export function parseLanBrowserConfig(raw: unknown): LanBrowserConfig {
  const base = parseUrlEmbedConfig(raw);
  const bookmarks: LanBookmark[] = [];
  const seen = new Set<string>();

  if (raw && typeof raw === 'object') {
    const list = (raw as Record<string, unknown>).bookmarks;
    if (Array.isArray(list)) {
      for (const entry of list) {
        if (bookmarks.length >= MAX_BOOKMARKS) break;
        if (!entry || typeof entry !== 'object') continue;
        const e = entry as Record<string, unknown>;
        const url = typeof e.url === 'string' ? normalizeLanUrl(e.url) : null;
        if (!url || seen.has(url)) continue;
        seen.add(url);
        const label = typeof e.label === 'string' ? e.label.trim() : '';
        bookmarks.push({ url, label });
      }
    }
  }

  return { ...base, bookmarks };
}

/** Display label for a bookmark chip: operator label if set, else the host. */
export function lanBookmarkLabel(bookmark: LanBookmark): string {
  if (bookmark.label) return bookmark.label;
  try {
    return new URL(bookmark.url).host || bookmark.url;
  } catch {
    return bookmark.url;
  }
}
