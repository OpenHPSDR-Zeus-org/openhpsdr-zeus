// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

import { describe, it, expect } from 'vitest';
import {
  normalizeEmbedUrl,
  parseUrlEmbedConfig,
  urlEmbedTitle,
} from './urlEmbedConfig';

describe('normalizeEmbedUrl', () => {
  it('keeps a well-formed https URL', () => {
    expect(normalizeEmbedUrl('https://example.com/path')).toBe(
      'https://example.com/path',
    );
  });

  it('keeps http URLs', () => {
    expect(normalizeEmbedUrl('http://192.168.1.50:8080/')).toBe(
      'http://192.168.1.50:8080/',
    );
  });

  it('prefixes a bare host with https', () => {
    expect(normalizeEmbedUrl('example.com')).toBe('https://example.com/');
  });

  it('trims surrounding whitespace', () => {
    expect(normalizeEmbedUrl('  https://example.com  ')).toBe(
      'https://example.com/',
    );
  });

  it('rejects empty / whitespace-only input', () => {
    expect(normalizeEmbedUrl('')).toBeNull();
    expect(normalizeEmbedUrl('   ')).toBeNull();
  });

  it('rejects javascript: scheme', () => {
    expect(normalizeEmbedUrl('javascript:alert(1)')).toBeNull();
  });

  it('rejects data: scheme', () => {
    expect(normalizeEmbedUrl('data:text/html,<script>alert(1)</script>')).toBeNull();
  });

  it('rejects file: scheme', () => {
    expect(normalizeEmbedUrl('file:///etc/passwd')).toBeNull();
  });
});

describe('parseUrlEmbedConfig', () => {
  it('returns an empty config for non-objects', () => {
    expect(parseUrlEmbedConfig(null)).toEqual({
      schemaVersion: 1,
      url: '',
      title: '',
    });
    expect(parseUrlEmbedConfig('nope')).toEqual({
      schemaVersion: 1,
      url: '',
      title: '',
    });
  });

  it('re-sanitises a stored URL so a tampered blob cannot smuggle a scheme', () => {
    expect(parseUrlEmbedConfig({ url: 'javascript:alert(1)' }).url).toBe('');
  });

  it('preserves a valid stored URL and title', () => {
    const c = parseUrlEmbedConfig({ url: 'https://example.com', title: 'Docs' });
    expect(c.url).toBe('https://example.com/');
    expect(c.title).toBe('Docs');
  });
});

describe('urlEmbedTitle', () => {
  it('prefers an explicit title', () => {
    expect(
      urlEmbedTitle({ schemaVersion: 1, url: 'https://example.com', title: 'My Page' }),
    ).toBe('My Page');
  });

  it('falls back to the host', () => {
    expect(
      urlEmbedTitle({ schemaVersion: 1, url: 'https://example.com/x', title: '' }),
    ).toBe('example.com');
  });

  it('falls back to a generic label when no URL is pinned', () => {
    expect(urlEmbedTitle({ schemaVersion: 1, url: '', title: '' })).toBe(
      'URL Embed',
    );
  });
});
