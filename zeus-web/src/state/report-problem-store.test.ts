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
import type { ReportResult, Symptom } from '../api/diagnostics';

vi.mock('../api/diagnostics', () => ({
  fetchSymptoms: vi.fn(),
  runReport: vi.fn(),
}));

import { fetchSymptoms, runReport } from '../api/diagnostics';
import { ApiError } from '../api/client';
import { useReportProblemStore } from './report-problem-store';

const SYMPTOMS: Symptom[] = [
  { id: 'no-audio', label: 'No receive audio', group: 'Audio' },
  { id: 'tx-crackle', label: 'Crackling on transmit', group: 'Transmit' },
];

const RESULT: ReportResult = {
  report: { foo: 'bar' },
  markdown: '# Zeus problem report\n\nNo receive audio.\n',
  githubIssueUrl: 'https://github.com/OpenHPSDR-Zeus-org/openhpsdr-zeus/issues/new?body=...',
};

const RESET = {
  isOpen: false,
  symptoms: [],
  symptomsLoaded: false,
  selectedSymptomId: null,
  freeText: '',
  loading: false,
  error: null,
  result: null,
};

const mockFetchSymptoms = vi.mocked(fetchSymptoms);
const mockRunReport = vi.mocked(runReport);

describe('report-problem-store', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    useReportProblemStore.setState(RESET);
  });

  it('open() opens the modal and lazy-loads symptoms', async () => {
    mockFetchSymptoms.mockResolvedValue(SYMPTOMS);

    useReportProblemStore.getState().open();
    expect(useReportProblemStore.getState().isOpen).toBe(true);
    expect(mockFetchSymptoms).toHaveBeenCalledTimes(1);

    // Let the lazy load settle.
    await vi.waitFor(() => {
      expect(useReportProblemStore.getState().symptomsLoaded).toBe(true);
    });
    expect(useReportProblemStore.getState().symptoms).toEqual(SYMPTOMS);
  });

  it('open() does not re-fetch symptoms once loaded', async () => {
    mockFetchSymptoms.mockResolvedValue(SYMPTOMS);

    useReportProblemStore.getState().open();
    await vi.waitFor(() => {
      expect(useReportProblemStore.getState().symptomsLoaded).toBe(true);
    });
    useReportProblemStore.getState().close();
    useReportProblemStore.getState().open();

    expect(mockFetchSymptoms).toHaveBeenCalledTimes(1);
  });

  it('open() still marks symptoms loaded when the fetch fails', async () => {
    mockFetchSymptoms.mockRejectedValue(new Error('network down'));

    useReportProblemStore.getState().open();
    await vi.waitFor(() => {
      expect(useReportProblemStore.getState().symptomsLoaded).toBe(true);
    });
    expect(useReportProblemStore.getState().symptoms).toEqual([]);
  });

  it('setSymptom() and setFreeText() update the form fields', () => {
    useReportProblemStore.getState().setSymptom('no-audio');
    useReportProblemStore.getState().setFreeText('it is broken');
    const s = useReportProblemStore.getState();
    expect(s.selectedSymptomId).toBe('no-audio');
    expect(s.freeText).toBe('it is broken');
  });

  it('run() populates result and passes the selected symptom + trimmed text', async () => {
    mockRunReport.mockResolvedValue(RESULT);
    useReportProblemStore.getState().setSymptom('no-audio');
    useReportProblemStore.getState().setFreeText('  no sound at all  ');

    await useReportProblemStore.getState().run();

    expect(mockRunReport).toHaveBeenCalledWith({
      symptomId: 'no-audio',
      freeText: 'no sound at all',
    });
    const s = useReportProblemStore.getState();
    expect(s.result).toEqual(RESULT);
    expect(s.loading).toBe(false);
    expect(s.error).toBeNull();
  });

  it('run() sends null freeText when the box is empty/whitespace', async () => {
    mockRunReport.mockResolvedValue(RESULT);
    useReportProblemStore.getState().setFreeText('   ');

    await useReportProblemStore.getState().run();

    expect(mockRunReport).toHaveBeenCalledWith({
      symptomId: null,
      freeText: null,
    });
  });

  it('run() records the ApiError message and clears loading on failure', async () => {
    mockRunReport.mockRejectedValue(new ApiError(500, 'server exploded'));

    await useReportProblemStore.getState().run();

    const s = useReportProblemStore.getState();
    expect(s.result).toBeNull();
    expect(s.loading).toBe(false);
    expect(s.error).toBe('server exploded');
  });

  it('close() leaves the modal closed but keeps cached symptoms', async () => {
    mockFetchSymptoms.mockResolvedValue(SYMPTOMS);
    useReportProblemStore.getState().open();
    await vi.waitFor(() => {
      expect(useReportProblemStore.getState().symptomsLoaded).toBe(true);
    });

    useReportProblemStore.getState().close();
    const s = useReportProblemStore.getState();
    expect(s.isOpen).toBe(false);
    expect(s.symptoms).toEqual(SYMPTOMS);
  });

  it('reset() clears the form + result but keeps cached symptoms', async () => {
    mockFetchSymptoms.mockResolvedValue(SYMPTOMS);
    mockRunReport.mockResolvedValue(RESULT);
    useReportProblemStore.getState().open();
    await vi.waitFor(() => {
      expect(useReportProblemStore.getState().symptomsLoaded).toBe(true);
    });
    useReportProblemStore.getState().setSymptom('no-audio');
    useReportProblemStore.getState().setFreeText('broken');
    await useReportProblemStore.getState().run();

    useReportProblemStore.getState().reset();
    const s = useReportProblemStore.getState();
    expect(s.selectedSymptomId).toBeNull();
    expect(s.freeText).toBe('');
    expect(s.result).toBeNull();
    expect(s.error).toBeNull();
    expect(s.loading).toBe(false);
    // Cached catalogue survives a reset.
    expect(s.symptoms).toEqual(SYMPTOMS);
  });
});
