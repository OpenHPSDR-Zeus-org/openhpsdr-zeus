// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see ATTRIBUTIONS.md for provenance.

// Voyeur Mode (zeus-la5) REST client. Voyeur Mode parks the radio on a
// frequency and logs each transmission ("over"); these calls drive the
// start/stop control and the save/delete log management.

export type VoyeurStatus = {
  active: boolean;
  sessionId: string | null;
  freqHz: number;
  mode: string;
  band: string;
  segmentCount: number;
  capturedSeconds: number;
  droppedSamples: number;
  ringFillPct: number;
  degraded: boolean;
  transcriptionAvailable: boolean;
};

export type VoyeurSession = {
  id: string;
  label: string;
  startedUtc: string;
  endedUtc: string | null;
  freqHz: number;
  mode: string;
  band: string;
  segmentCount: number;
  capturedSeconds: number;
  droppedSamples: number;
  pinned: boolean;
  hasAudio: boolean;
};

export type VoyeurSegment = {
  id: string;
  startedUtc: string;
  durationMs: number;
  peakDbfs: number;
  hasAudio: boolean;
  transcript: string | null;
  callsign: string | null;
  // "confirmed" | "tentative" | "unknown" (Phase 2 — null in Phase 1)
  callsignState: string | null;
  // QRZ operator name when confirmed (roster enrichment).
  callsignName: string | null;
};

export type VoyeurSessionDetail = {
  session: VoyeurSession;
  segments: VoyeurSegment[];
};

async function json<T>(input: string, init?: RequestInit): Promise<T> {
  const res = await fetch(input, init);
  if (!res.ok) {
    let message = `${res.status} ${res.statusText}`;
    try {
      const body = (await res.json()) as { error?: string };
      if (body?.error) message = body.error;
    } catch {
      /* ignore */
    }
    throw new Error(message);
  }
  return (await res.json()) as T;
}

export type VoyeurTranscription = { available: boolean; modelDir: string };

export const getVoyeurStatus = (signal?: AbortSignal) =>
  json<VoyeurStatus>('/api/voyeur/status', { signal });

export const getVoyeurTranscription = (signal?: AbortSignal) =>
  json<VoyeurTranscription>('/api/voyeur/transcription', { signal });

export type VoyeurModel = { id: string; label: string };

export type VoyeurInstall = {
  phase: 'Idle' | 'Downloading' | 'Done' | 'Error';
  percent: number;
  message: string;
  item: string | null;
  modelPresent: boolean;
  binaryPresent: boolean;
  rid: string;
};

export const getVoyeurModels = (signal?: AbortSignal) =>
  json<VoyeurModel[]>('/api/voyeur/install/models', { signal });

export const getVoyeurInstallStatus = (signal?: AbortSignal) =>
  json<VoyeurInstall>('/api/voyeur/install/status', { signal });

export const installVoyeurModel = (model: string) =>
  json<VoyeurInstall>('/api/voyeur/install/model', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ model }),
  });

export const cancelVoyeurInstall = () =>
  json<VoyeurInstall>('/api/voyeur/install/cancel', { method: 'POST' });

export const startVoyeur = (keepAudio = true) =>
  json<VoyeurStatus>('/api/voyeur/start', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ keepAudio }),
  });

export const stopVoyeur = () =>
  json<VoyeurStatus>('/api/voyeur/stop', { method: 'POST' });

export const listVoyeurSessions = (signal?: AbortSignal) =>
  json<VoyeurSession[]>('/api/voyeur/sessions', { signal });

export const getVoyeurSession = (id: string, signal?: AbortSignal) =>
  json<VoyeurSessionDetail>(`/api/voyeur/sessions/${encodeURIComponent(id)}`, { signal });

export const updateVoyeurSession = (
  id: string,
  patch: { label?: string; pinned?: boolean },
) =>
  json<VoyeurSession>(`/api/voyeur/sessions/${encodeURIComponent(id)}`, {
    method: 'PATCH',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(patch),
  });

export const deleteVoyeurSession = (id: string) =>
  json<{ deleted: string }>(`/api/voyeur/sessions/${encodeURIComponent(id)}`, {
    method: 'DELETE',
  });
