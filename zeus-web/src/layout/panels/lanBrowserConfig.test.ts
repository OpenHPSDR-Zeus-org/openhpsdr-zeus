// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.

import { describe, it, expect } from 'vitest';
import {
  lanBookmarkLabel,
  normalizeLanUrl,
  parseLanBrowserConfig,
} from './lanBrowserConfig';

describe('normalizeLanUrl', () => {
  it('defaults a bare host to http (device pages are plain HTTP)', () => {
    expect(normalizeLanUrl('192.168.1.1')).toBe('http://192.168.1.1/');
  });

  it('keeps an explicit https scheme', () => {
    expect(normalizeLanUrl('https://192.168.1.50:8443/admin')).toBe(
      'https://192.168.1.50:8443/admin',
    );
  });

  it('rejects non-http(s) schemes', () => {
    expect(normalizeLanUrl('javascript:alert(1)')).toBeNull();
    expect(normalizeLanUrl('data:text/html,x')).toBeNull();
    expect(normalizeLanUrl('')).toBeNull();
  });
});

describe('parseLanBrowserConfig', () => {
  it('defaults bookmarks to an empty array for a legacy blob', () => {
    const c = parseLanBrowserConfig({ url: 'http://192.168.1.1/', title: '' });
    expect(c.url).toBe('http://192.168.1.1/');
    expect(c.bookmarks).toEqual([]);
  });

  it('sanitises, normalises and keeps valid bookmarks', () => {
    const c = parseLanBrowserConfig({
      bookmarks: [
        { url: '192.168.1.1', label: 'Router' },
        { url: 'https://10.0.0.5/status', label: '' },
      ],
    });
    expect(c.bookmarks).toEqual([
      { url: 'http://192.168.1.1/', label: 'Router' },
      { url: 'https://10.0.0.5/status', label: '' },
    ]);
  });

  it('drops malformed / unsafe bookmarks and de-dupes by URL', () => {
    const c = parseLanBrowserConfig({
      bookmarks: [
        { url: 'javascript:alert(1)', label: 'evil' },
        { url: '192.168.1.1' },
        { url: 'http://192.168.1.1/' }, // dup of the normalised entry above
        'not-an-object',
        { label: 'no url' },
      ],
    });
    expect(c.bookmarks).toEqual([{ url: 'http://192.168.1.1/', label: '' }]);
  });

  it('tolerates a non-array bookmarks field', () => {
    expect(parseLanBrowserConfig({ bookmarks: 'nope' }).bookmarks).toEqual([]);
    expect(parseLanBrowserConfig(null).bookmarks).toEqual([]);
  });
});

describe('lanBookmarkLabel', () => {
  it('prefers an explicit label', () => {
    expect(lanBookmarkLabel({ url: 'http://192.168.1.1/', label: 'Router' })).toBe(
      'Router',
    );
  });

  it('falls back to the host', () => {
    expect(lanBookmarkLabel({ url: 'http://192.168.1.50:8080/x', label: '' })).toBe(
      '192.168.1.50:8080',
    );
  });
});
