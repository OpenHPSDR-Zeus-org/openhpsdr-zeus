// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

namespace Zeus.Server;

public sealed class FrontendDspSceneDiagnosticsService
{
    private const int MaxText = 180;
    private const long FreshSceneMs = 15_000;
    private const long StaleSceneMs = 45_000;
    private readonly object _sync = new();
    private FrontendDspSceneSnapshot? _latest;

    public FrontendDspSceneSnapshot Update(FrontendDspSceneDiagnosticsRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new FrontendDspSceneSnapshot(
            SchemaVersion: 1,
            AtUtc: now,
            SourceClientId: Clean(request.SourceClientId, 64),
            Mode: Clean(request.Mode, 16),
            SignalProfile: Clean(request.SignalProfile, 48),
            SignalReason: Clean(request.SignalReason, MaxText),
            SmartNrProfile: Clean(request.SmartNrProfile, 48),
            SmartNrReason: Clean(request.SmartNrReason, MaxText),
            SmartNrRecommendation: Clean(request.SmartNrRecommendation, MaxText),
            SmartNrHeldByRxChain: request.SmartNrHeldByRxChain,
            SmartNrRxChainLabel: Clean(request.SmartNrRxChainLabel, 80),
            MaxSnrDb: Finite(request.MaxSnrDb),
            CoherentMaxSnrDb: Finite(request.CoherentMaxSnrDb),
            OccupiedPct: Percent(request.OccupiedPct),
            CoherentOccupiedPct: Percent(request.CoherentOccupiedPct),
            ImpulsivePct: Percent(request.ImpulsivePct),
            PeakCount: NonNegative(request.PeakCount),
            CoherentPeakCount: NonNegative(request.CoherentPeakCount),
            CoherentSubthresholdSignal: request.CoherentSubthresholdSignal);

        lock (_sync)
        {
            _latest = snapshot;
            return snapshot;
        }
    }

    public object Snapshot()
    {
        lock (_sync)
        {
            if (_latest is null)
            {
                var health = Health(null, null);
                return new
                {
                    schemaVersion = 1,
                    available = false,
                    ageMs = (int?)null,
                    status = health.Status,
                    fresh = false,
                    stale = false,
                    diagnosticRecommendation = health.Recommendation,
                };
            }

            var ageMs = Math.Max(0L, (long)(DateTimeOffset.UtcNow - _latest.AtUtc).TotalMilliseconds);
            var sceneHealth = Health(ageMs, _latest);
            return new
            {
                schemaVersion = _latest.SchemaVersion,
                available = true,
                ageMs,
                status = sceneHealth.Status,
                fresh = sceneHealth.Fresh,
                stale = sceneHealth.Stale,
                diagnosticRecommendation = sceneHealth.Recommendation,
                atUtc = _latest.AtUtc,
                sourceClientId = _latest.SourceClientId,
                mode = _latest.Mode,
                signalProfile = _latest.SignalProfile,
                signalReason = _latest.SignalReason,
                smartNrProfile = _latest.SmartNrProfile,
                smartNrReason = _latest.SmartNrReason,
                smartNrRecommendation = _latest.SmartNrRecommendation,
                smartNrHeldByRxChain = _latest.SmartNrHeldByRxChain,
                smartNrRxChainLabel = _latest.SmartNrRxChainLabel,
                maxSnrDb = _latest.MaxSnrDb,
                coherentMaxSnrDb = _latest.CoherentMaxSnrDb,
                occupiedPct = _latest.OccupiedPct,
                coherentOccupiedPct = _latest.CoherentOccupiedPct,
                impulsivePct = _latest.ImpulsivePct,
                peakCount = _latest.PeakCount,
                coherentPeakCount = _latest.CoherentPeakCount,
                coherentSubthresholdSignal = _latest.CoherentSubthresholdSignal,
            };
        }
    }

    private static string? Clean(string? raw, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var value = string.Join(" ", raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static double? Finite(double? value) =>
        value is { } v && double.IsFinite(v) ? Math.Round(v, 1) : null;

    private static double? Percent(double? value)
    {
        if (value is not { } v || !double.IsFinite(v)) return null;
        return Math.Round(Math.Clamp(v, 0, 100), 1);
    }

    private static int? NonNegative(int? value) =>
        value is { } v && v >= 0 ? v : null;

    private static FrontendDspSceneHealth Health(long? ageMs, FrontendDspSceneSnapshot? latest)
    {
        if (ageMs is null)
        {
            return new(
                "missing",
                Fresh: false,
                Stale: false,
                "No frontend DSP scene has been published yet; open a Zeus client with Signal Intelligence or Smart NR active to audit weak-signal automation.");
        }

        if (ageMs > StaleSceneMs)
        {
            return new(
                "stale",
                Fresh: false,
                Stale: true,
                "Frontend DSP scene telemetry is stale; verify a client is open and publishing Signal Intelligence and Smart NR evidence.");
        }

        if (ageMs > FreshSceneMs)
        {
            return new(
                "aging",
                Fresh: false,
                Stale: false,
                "Frontend DSP scene telemetry is aging; wait for the next refresh or check client connectivity if this persists.");
        }

        if (latest?.SmartNrHeldByRxChain == true)
        {
            return new(
                "fresh",
                Fresh: true,
                Stale: false,
                "Frontend DSP scene telemetry is fresh; Smart NR is currently constrained by RX-chain health.");
        }

        return new(
            "fresh",
            Fresh: true,
            Stale: false,
            "Frontend DSP scene telemetry is fresh and ready for remote diagnostics.");

    }

    private sealed record FrontendDspSceneHealth(
        string Status,
        bool Fresh,
        bool Stale,
        string Recommendation);
}

public sealed record FrontendDspSceneDiagnosticsRequest(
    string? SourceClientId,
    string? Mode,
    string? SignalProfile,
    string? SignalReason,
    string? SmartNrProfile,
    string? SmartNrReason,
    string? SmartNrRecommendation,
    bool? SmartNrHeldByRxChain,
    string? SmartNrRxChainLabel,
    double? MaxSnrDb,
    double? CoherentMaxSnrDb,
    double? OccupiedPct,
    double? CoherentOccupiedPct,
    double? ImpulsivePct,
    int? PeakCount,
    int? CoherentPeakCount,
    bool? CoherentSubthresholdSignal);

public sealed record FrontendDspSceneSnapshot(
    int SchemaVersion,
    DateTimeOffset AtUtc,
    string? SourceClientId,
    string? Mode,
    string? SignalProfile,
    string? SignalReason,
    string? SmartNrProfile,
    string? SmartNrReason,
    string? SmartNrRecommendation,
    bool? SmartNrHeldByRxChain,
    string? SmartNrRxChainLabel,
    double? MaxSnrDb,
    double? CoherentMaxSnrDb,
    double? OccupiedPct,
    double? CoherentOccupiedPct,
    double? ImpulsivePct,
    int? PeakCount,
    int? CoherentPeakCount,
    bool? CoherentSubthresholdSignal);
