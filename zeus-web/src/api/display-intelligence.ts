// SPDX-License-Identifier: GPL-2.0-or-later
//
// Client for /api/dsp/display-intelligence, the server-side persistence surface
// for Signal Intelligence weak-signal display policy.

export type DisplayIntelligenceSettings = {
  profileId: 'balanced' | 'dx' | 'cw' | 'digital' | 'voice' | 'contest' | 'custom';
  popEnabled: boolean;
  snapEnabled: boolean;
  autoNotchEnabled: boolean;
  autoProfileEnabled: boolean;
  visualAgcEnabled: boolean;
  impulseRejectEnabled: boolean;
  popFloorDb: number;
  popSpanDb: number;
  popGamma: number;
  popRenderIntensity: number;
  coherenceHoldGate: number;
  coherenceBoostDb: number;
  ridgeBoost: number;
  ridgeMaxBoostDb: number;
  visualAgcStrength: number;
  impulseRejectDb: number;
  snapRadiusHz: number;
  snapMinSnrDb: number;
  peakMinSnrDb: number;
};

export const DISPLAY_INTELLIGENCE_DEFAULTS: DisplayIntelligenceSettings = {
  profileId: 'balanced',
  popEnabled: false,
  snapEnabled: false,
  autoNotchEnabled: false,
  autoProfileEnabled: false,
  visualAgcEnabled: true,
  impulseRejectEnabled: true,
  popFloorDb: 3,
  popSpanDb: 30,
  popGamma: 0.5,
  popRenderIntensity: 72,
  coherenceHoldGate: 0.45,
  coherenceBoostDb: 4,
  ridgeBoost: 0.35,
  ridgeMaxBoostDb: 8,
  visualAgcStrength: 45,
  impulseRejectDb: 18,
  snapRadiusHz: 4000,
  snapMinSnrDb: 6,
  peakMinSnrDb: 8,
};

function clampFinite(value: unknown, min: number, max: number, fallback: number): number {
  return typeof value === 'number' && Number.isFinite(value)
    ? Math.max(min, Math.min(max, value))
    : fallback;
}

function clampInt(value: unknown, min: number, max: number, fallback: number): number {
  return Math.round(clampFinite(value, min, max, fallback));
}

function profileId(value: unknown): DisplayIntelligenceSettings['profileId'] {
  if (
    value === 'balanced' ||
    value === 'dx' ||
    value === 'cw' ||
    value === 'digital' ||
    value === 'voice' ||
    value === 'contest' ||
    value === 'custom'
  ) {
    return value;
  }
  return DISPLAY_INTELLIGENCE_DEFAULTS.profileId;
}

export function normalizeDisplayIntelligenceSettings(raw: unknown): DisplayIntelligenceSettings {
  const r = raw && typeof raw === 'object' ? raw as Record<string, unknown> : {};
  const d = DISPLAY_INTELLIGENCE_DEFAULTS;
  return {
    profileId: profileId(typeof r.profileId === 'string' ? r.profileId.trim().toLowerCase() : r.profileId),
    popEnabled: typeof r.popEnabled === 'boolean' ? r.popEnabled : d.popEnabled,
    snapEnabled: typeof r.snapEnabled === 'boolean' ? r.snapEnabled : d.snapEnabled,
    autoNotchEnabled: typeof r.autoNotchEnabled === 'boolean' ? r.autoNotchEnabled : d.autoNotchEnabled,
    autoProfileEnabled: typeof r.autoProfileEnabled === 'boolean' ? r.autoProfileEnabled : d.autoProfileEnabled,
    visualAgcEnabled: typeof r.visualAgcEnabled === 'boolean' ? r.visualAgcEnabled : d.visualAgcEnabled,
    impulseRejectEnabled: typeof r.impulseRejectEnabled === 'boolean' ? r.impulseRejectEnabled : d.impulseRejectEnabled,
    popFloorDb: clampFinite(r.popFloorDb, 0, 12, d.popFloorDb),
    popSpanDb: clampFinite(r.popSpanDb, 12, 60, d.popSpanDb),
    popGamma: clampFinite(r.popGamma, 0.3, 1.2, d.popGamma),
    popRenderIntensity: clampInt(r.popRenderIntensity, 0, 100, d.popRenderIntensity),
    coherenceHoldGate: clampFinite(r.coherenceHoldGate, 0.2, 0.8, d.coherenceHoldGate),
    coherenceBoostDb: clampFinite(r.coherenceBoostDb, 0, 8, d.coherenceBoostDb),
    ridgeBoost: clampFinite(r.ridgeBoost, 0, 0.8, d.ridgeBoost),
    ridgeMaxBoostDb: clampFinite(r.ridgeMaxBoostDb, 0, 12, d.ridgeMaxBoostDb),
    visualAgcStrength: clampInt(r.visualAgcStrength, 0, 100, d.visualAgcStrength),
    impulseRejectDb: clampInt(r.impulseRejectDb, 8, 32, d.impulseRejectDb),
    snapRadiusHz: clampInt(r.snapRadiusHz, 500, 12_000, d.snapRadiusHz),
    snapMinSnrDb: clampFinite(r.snapMinSnrDb, 3, 16, d.snapMinSnrDb),
    peakMinSnrDb: clampFinite(r.peakMinSnrDb, 4, 20, d.peakMinSnrDb),
  };
}

export async function fetchDisplayIntelligenceSettings(
  signal?: AbortSignal,
): Promise<DisplayIntelligenceSettings> {
  const res = await fetch('/api/dsp/display-intelligence', { signal });
  if (!res.ok) throw new Error(`GET /api/dsp/display-intelligence -> ${res.status}`);
  return normalizeDisplayIntelligenceSettings(await res.json());
}

export async function saveDisplayIntelligenceSettings(
  settings: DisplayIntelligenceSettings,
  signal?: AbortSignal,
): Promise<DisplayIntelligenceSettings> {
  const res = await fetch('/api/dsp/display-intelligence', {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(settings),
    signal,
  });
  if (!res.ok) throw new Error(`PUT /api/dsp/display-intelligence -> ${res.status}`);
  return normalizeDisplayIntelligenceSettings(await res.json());
}
