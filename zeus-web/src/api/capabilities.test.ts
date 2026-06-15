// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';

import { isLoopbackHost, parseCapabilities } from './capabilities';

describe('parseCapabilities', () => {
  it('coerces a well-formed snapshot', () => {
    const caps = parseCapabilities({
      host: 'desktop',
      platform: 'linux',
      architecture: 'x64',
      version: '0.6.0-alpha',
      lanHttpsUrls: ['https://192.168.1.23:6443'],
      features: {},
    });
    expect(caps.host).toBe('desktop');
    expect(caps.platform).toBe('linux');
    expect(caps.architecture).toBe('x64');
    expect(caps.version).toBe('0.6.0-alpha');
    expect(caps.lanHttpsUrls).toEqual(['https://192.168.1.23:6443']);
  });

  it('falls back to safe defaults on garbage input', () => {
    const caps = parseCapabilities({});
    expect(caps.host).toBe('server');
    expect(caps.platform).toBe('unknown');
    expect(caps.architecture).toBe('unknown');
    expect(caps.version).toBe('unknown');
    expect(caps.lanHttpsUrls).toEqual([]);
  });

  it('filters malformed LAN HTTPS URL entries', () => {
    const caps = parseCapabilities({
      lanHttpsUrls: ['https://192.168.1.23:6443', 42, null, 'https://10.0.0.5:6443'],
    });
    expect(caps.lanHttpsUrls).toEqual([
      'https://192.168.1.23:6443',
      'https://10.0.0.5:6443',
    ]);
  });

  it('clamps unknown host strings to "server"', () => {
    const caps = parseCapabilities({ host: 'something-else' });
    expect(caps.host).toBe('server');
  });

  it('clamps unknown platforms to "unknown"', () => {
    const caps = parseCapabilities({ platform: 'plan9' });
    expect(caps.platform).toBe('unknown');
  });
});

describe('isLoopbackHost', () => {
  it('treats explicit loopback URLs as local', () => {
    expect(isLoopbackHost('http://localhost:6060')).toBe(true);
    expect(isLoopbackHost('http://127.0.0.1:6060')).toBe(true);
    expect(isLoopbackHost('http://[::1]:6060')).toBe(true);
  });

  it('treats LAN IPs as remote', () => {
    expect(isLoopbackHost('http://192.168.1.23:6060')).toBe(false);
    expect(isLoopbackHost('https://radio.lan:6060')).toBe(false);
  });

  it('falls back to window.location when base is empty', () => {
    // Vitest's jsdom default is http://localhost — so the empty-base
    // path resolves to a loopback hostname.
    expect(isLoopbackHost('')).toBe(true);
  });
});
