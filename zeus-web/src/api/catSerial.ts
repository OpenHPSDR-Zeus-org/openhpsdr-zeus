// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

// REST client for the serial CAT ports (Thetis CAT1–4). Sibling of api/cat.ts;
// the same Kenwood handler, just over host serial devices instead of TCP.

import { ApiError } from './client';

export const CAT_SERIAL_PORT_COUNT = 4;
export const CAT_SERIAL_DEFAULT_BAUD = 115200;
export const CAT_SERIAL_BAUD_RATES = [300, 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200];
export const CAT_SERIAL_PARITIES = ['None', 'Odd', 'Even', 'Mark', 'Space'];
export const CAT_SERIAL_DATA_BITS = [8, 7, 6];
// Value = System.IO.Ports.StopBits name; label = the human "1 / 1.5 / 2".
export const CAT_SERIAL_STOP_BITS: { value: string; label: string }[] = [
  { value: 'One', label: '1' },
  { value: 'OnePointFive', label: '1.5' },
  { value: 'Two', label: '2' },
];

export type CatSerialPortConfig = {
  enabled: boolean;
  portName: string;
  baudRate: number;
  parity: string;
  dataBits: number;
  stopBits: string;
};

export type CatSerialPortStatus = CatSerialPortConfig & {
  index: number;
  open: boolean;
  clientActivity: number;
  error: string | null;
};

export type CatSerialStatus = {
  ports: CatSerialPortStatus[];
  availablePorts: string[];
};

export type CatSerialTestResult = { ok: boolean; error: string | null };

export function defaultPortConfig(): CatSerialPortConfig {
  return {
    enabled: false,
    portName: '',
    baudRate: CAT_SERIAL_DEFAULT_BAUD,
    parity: 'None',
    dataBits: 8,
    stopBits: 'One',
  };
}

function normalizePort(raw: unknown, index: number): CatSerialPortStatus {
  const r = (raw ?? {}) as Record<string, unknown>;
  return {
    index: typeof r.index === 'number' ? r.index : index,
    enabled: Boolean(r.enabled),
    portName: typeof r.portName === 'string' ? r.portName : '',
    baudRate: typeof r.baudRate === 'number' ? r.baudRate : CAT_SERIAL_DEFAULT_BAUD,
    parity: typeof r.parity === 'string' ? r.parity : 'None',
    dataBits: typeof r.dataBits === 'number' ? r.dataBits : 8,
    stopBits: typeof r.stopBits === 'string' ? r.stopBits : 'One',
    open: Boolean(r.open),
    clientActivity: typeof r.clientActivity === 'number' ? r.clientActivity : 0,
    error: typeof r.error === 'string' && r.error.length > 0 ? r.error : null,
  };
}

function normalizeStatus(raw: unknown): CatSerialStatus {
  const r = (raw ?? {}) as Record<string, unknown>;
  const portsRaw = Array.isArray(r.ports) ? r.ports : [];
  const ports: CatSerialPortStatus[] = [];
  for (let i = 0; i < CAT_SERIAL_PORT_COUNT; i++) ports.push(normalizePort(portsRaw[i], i));
  const availablePorts = Array.isArray(r.availablePorts)
    ? r.availablePorts.filter((p): p is string => typeof p === 'string')
    : [];
  return { ports, availablePorts };
}

async function jsonFetch<T>(
  input: RequestInfo,
  init: RequestInit | undefined,
  parse: (raw: unknown) => T,
): Promise<T> {
  const res = await fetch(input, init);
  if (!res.ok) {
    let message = `${res.status} ${res.statusText}`;
    try {
      const body = (await res.json()) as unknown;
      if (body && typeof body === 'object' && 'error' in body && typeof (body as { error: unknown }).error === 'string') {
        message = (body as { error: string }).error;
      }
    } catch {
      /* non-JSON */
    }
    throw new ApiError(res.status, message);
  }
  return parse((await res.json()) as unknown);
}

export function getCatSerialStatus(signal?: AbortSignal): Promise<CatSerialStatus> {
  return jsonFetch('/api/cat/serial/status', { signal }, normalizeStatus);
}

export function putCatSerialConfig(ports: CatSerialPortConfig[], signal?: AbortSignal): Promise<CatSerialStatus> {
  return jsonFetch(
    '/api/cat/serial/config',
    {
      method: 'PUT',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ ports }),
      signal,
    },
    normalizeStatus,
  );
}

export function testCatSerialPort(port: CatSerialPortConfig, signal?: AbortSignal): Promise<CatSerialTestResult> {
  return jsonFetch(
    '/api/cat/serial/test',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({
        portName: port.portName,
        baudRate: port.baudRate,
        parity: port.parity,
        dataBits: port.dataBits,
        stopBits: port.stopBits,
      }),
      signal,
    },
    (raw) => {
      const r = (raw ?? {}) as Record<string, unknown>;
      return { ok: Boolean(r.ok), error: typeof r.error === 'string' && r.error ? r.error : null };
    },
  );
}
