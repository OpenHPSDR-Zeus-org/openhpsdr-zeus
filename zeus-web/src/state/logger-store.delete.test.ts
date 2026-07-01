// SPDX-License-Identifier: GPL-2.0-or-later

/** @vitest-environment jsdom */

import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { LogEntry } from '../api/log';

function makeEntry(id: string, callsign: string): LogEntry {
  return {
    id,
    qsoDateTimeUtc: '2026-06-28T12:00:00.000Z',
    callsign,
    name: null,
    frequencyMhz: 14.074,
    band: '20M',
    mode: 'FT8',
    rstSent: '-12',
    rstRcvd: '-09',
    grid: null,
    country: null,
    dxcc: null,
    cqZone: null,
    ituZone: null,
    state: null,
    comment: null,
    createdUtc: '2026-06-28T12:00:00.000Z',
    qrzLogId: null,
    qrzUploadedUtc: null,
  } as unknown as LogEntry;
}

// Shared mock state, declared via vi.hoisted so it exists before the hoisted
// vi.mock factory runs. `serverEntries` is the list the mocked API reads from;
// delete mutates it so the store's post-delete getLogEntries reload sees the
// change.
const h = vi.hoisted(() => {
  const state = { serverEntries: [] as LogEntry[] };
  const deleteLogEntries = vi.fn(async ({ logEntryIds }: { logEntryIds: string[] }) => {
    const before = state.serverEntries.length;
    state.serverEntries = state.serverEntries.filter((e) => !logEntryIds.includes(e.id));
    return { deletedCount: before - state.serverEntries.length };
  });
  return { state, deleteLogEntries };
});

vi.mock('../api/log', () => ({
  getLogEntries: vi.fn(async () => ({ entries: h.state.serverEntries, totalCount: h.state.serverEntries.length })),
  getWorkedCallsignSummary: vi.fn(),
  createLogEntry: vi.fn(),
  exportAdifToDirectory: vi.fn(),
  exportToAdif: vi.fn(),
  importAdif: vi.fn(),
  publishToQrz: vi.fn(),
  deleteLogEntries: h.deleteLogEntries,
}));

import { useLoggerStore } from './logger-store';

const deleteLogEntries = h.deleteLogEntries;

describe('logger-store deleteSelected', () => {
  beforeEach(() => {
    h.state.serverEntries = [makeEntry('a', 'K2ABC'), makeEntry('b', 'N9WAR'), makeEntry('c', 'EI6LF')];
    deleteLogEntries.mockClear();
    useLoggerStore.setState({
      entries: [...h.state.serverEntries],
      totalCount: h.state.serverEntries.length,
      selectedIds: new Set(['a', 'c']),
      deleteInFlight: false,
      deleteError: null,
      lastDeleteResult: null,
    });
  });

  it('deletes the selected ids, clears selection, and reloads the table', async () => {
    await useLoggerStore.getState().deleteSelected(['a', 'c']);

    expect(deleteLogEntries).toHaveBeenCalledWith({ logEntryIds: ['a', 'c'] });
    const s = useLoggerStore.getState();
    expect(s.lastDeleteResult).toEqual({ deletedCount: 2 });
    expect(s.selectedIds.size).toBe(0);
    expect(s.deleteInFlight).toBe(false);
    // Reloaded from the (now-reduced) server list.
    expect(s.entries.map((e) => e.id)).toEqual(['b']);
  });

  it('is a no-op when nothing is selected', async () => {
    await useLoggerStore.getState().deleteSelected([]);
    expect(deleteLogEntries).not.toHaveBeenCalled();
    expect(useLoggerStore.getState().entries).toHaveLength(3);
  });

  it('surfaces an error and keeps the selection when the API fails', async () => {
    deleteLogEntries.mockRejectedValueOnce(new Error('boom'));
    await useLoggerStore.getState().deleteSelected(['a']);

    const s = useLoggerStore.getState();
    expect(s.deleteError).toBe('boom');
    expect(s.deleteInFlight).toBe(false);
    // Selection preserved so the operator can retry.
    expect(Array.from(s.selectedIds)).toEqual(['a', 'c']);
  });
});
