// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.

import { describe, it, expect, vi, beforeEach } from 'vitest';
import type { CatSerialPortConfig, CatSerialStatus } from '../api/catSerial';

vi.mock('../api/catSerial', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api/catSerial')>();
  return {
    ...actual, // keep the real constants/defaultPortConfig
    getCatSerialStatus: vi.fn(),
    putCatSerialConfig: vi.fn(),
    testCatSerialPort: vi.fn(),
  };
});

import {
  getCatSerialStatus,
  putCatSerialConfig,
  testCatSerialPort,
  defaultPortConfig,
} from '../api/catSerial';
import { useCatSerialStore } from './cat-serial-store';

const mockGet = vi.mocked(getCatSerialStatus);
const mockPut = vi.mocked(putCatSerialConfig);
const mockTest = vi.mocked(testCatSerialPort);

function statusFrom(ports: CatSerialPortConfig[], availablePorts: string[] = []): CatSerialStatus {
  return {
    ports: ports.map((p, index) => ({ ...p, index, open: p.enabled, clientActivity: 0, error: null })),
    availablePorts,
  };
}

function fourDefaults(): CatSerialPortConfig[] {
  return [0, 1, 2, 3].map(defaultPortConfig);
}

describe('cat-serial-store', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    useCatSerialStore.setState({
      config: fourDefaults(),
      status: null,
      testingIndex: null,
      lastTest: null,
    });
  });

  it('adopts server config when it differs from the in-hand config', async () => {
    const serverPorts = fourDefaults();
    serverPorts[0] = { enabled: true, portName: '/dev/ttys9', baudRate: 9600, parity: 'Even', dataBits: 7, stopBits: 'Two' };
    mockGet.mockResolvedValueOnce(statusFrom(serverPorts));

    await useCatSerialStore.getState().refreshStatus();

    const cfg = useCatSerialStore.getState().config;
    expect(cfg[0]).toMatchObject({ enabled: true, portName: '/dev/ttys9', baudRate: 9600, parity: 'Even', dataBits: 7, stopBits: 'Two' });
    expect(useCatSerialStore.getState().status?.availablePorts).toEqual([]);
  });

  it('keeps the in-hand config by reference when the server matches (no clobber of edits)', async () => {
    const current = useCatSerialStore.getState().config;
    mockGet.mockResolvedValueOnce(statusFrom(current, ['/dev/cu.usbserial-1']));

    await useCatSerialStore.getState().refreshStatus();

    // Same reference → the panel's useEffect([config]) won't refire and reset the form.
    expect(useCatSerialStore.getState().config).toBe(current);
    // Live status (availablePorts) still updates.
    expect(useCatSerialStore.getState().status?.availablePorts).toEqual(['/dev/cu.usbserial-1']);
  });

  it('saveConfig adopts the posted config and the returned status', async () => {
    const ports = fourDefaults();
    ports[1] = { enabled: true, portName: 'COM3', baudRate: 115200, parity: 'None', dataBits: 8, stopBits: 'One' };
    mockPut.mockResolvedValueOnce(statusFrom(ports));

    await useCatSerialStore.getState().saveConfig(ports);

    expect(mockPut).toHaveBeenCalledWith(ports);
    expect(useCatSerialStore.getState().config).toBe(ports);
    expect(useCatSerialStore.getState().status?.ports[1]?.portName).toBe('COM3');
  });

  it('test() records the per-index result and clears the in-flight flag', async () => {
    mockTest.mockResolvedValueOnce({ ok: false, error: 'Port is in use' });

    const result = await useCatSerialStore.getState().test(2, defaultPortConfig());

    expect(result).toEqual({ ok: false, error: 'Port is in use' });
    expect(useCatSerialStore.getState().testingIndex).toBeNull();
    expect(useCatSerialStore.getState().lastTest).toEqual({ index: 2, result: { ok: false, error: 'Port is in use' } });
  });

  it('test() surfaces a thrown error as a failed result instead of rejecting', async () => {
    mockTest.mockRejectedValueOnce(new Error('network down'));

    const result = await useCatSerialStore.getState().test(0, defaultPortConfig());

    expect(result.ok).toBe(false);
    expect(result.error).toBe('network down');
    expect(useCatSerialStore.getState().testingIndex).toBeNull();
  });
});
