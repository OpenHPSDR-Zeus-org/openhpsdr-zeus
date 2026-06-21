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

// "Report a problem" self-diagnostic state. The footer button opens the modal
// (open() lazy-loads the symptom catalogue on first open); the operator picks a
// symptom and/or types a description, then run() asks the backend to assemble a
// Markdown report + a pre-filled bug-report page URL.

import { create } from 'zustand';
import {
  fetchSymptoms,
  runReport,
  type ReportResult,
  type Symptom,
} from '../api/diagnostics';
import { ApiError } from '../api/client';

export type ReportProblemState = {
  isOpen: boolean;
  symptoms: Symptom[];
  symptomsLoaded: boolean;
  selectedSymptomId: string | null;
  freeText: string;
  loading: boolean;
  error: string | null;
  result: ReportResult | null;

  /** Open the modal; lazy-loads the symptom catalogue on first open. */
  open: () => void;
  close: () => void;
  setSymptom: (id: string | null) => void;
  setFreeText: (s: string) => void;
  /** Run the diagnostic and populate result/error/loading. */
  run: () => Promise<void>;
  /** Clear the form + result back to a pristine state (keeps cached symptoms). */
  reset: () => void;
};

export const useReportProblemStore = create<ReportProblemState>((set, get) => ({
  isOpen: false,
  symptoms: [],
  symptomsLoaded: false,
  selectedSymptomId: null,
  freeText: '',
  loading: false,
  error: null,
  result: null,

  open: () => {
    set({ isOpen: true });
    if (!get().symptomsLoaded) {
      void (async () => {
        try {
          const symptoms = await fetchSymptoms();
          set({ symptoms, symptomsLoaded: true });
        } catch {
          // The catalogue is best-effort; the operator can still type a
          // free-text description and run the diagnostic without it.
          set({ symptomsLoaded: true });
        }
      })();
    }
  },

  close: () => set({ isOpen: false }),

  setSymptom: (id) => set({ selectedSymptomId: id }),

  setFreeText: (s) => set({ freeText: s }),

  run: async () => {
    const { selectedSymptomId, freeText } = get();
    set({ loading: true, error: null, result: null });
    try {
      const trimmed = freeText.trim();
      const result = await runReport({
        symptomId: selectedSymptomId,
        freeText: trimmed.length > 0 ? trimmed : null,
      });
      set({ result, loading: false });
    } catch (err) {
      const msg = err instanceof ApiError ? err.message : String(err);
      set({ error: msg, loading: false });
    }
  },

  reset: () =>
    set({
      selectedSymptomId: null,
      freeText: '',
      loading: false,
      error: null,
      result: null,
    }),
}));
