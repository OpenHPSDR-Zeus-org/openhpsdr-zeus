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
            CoherentPeakCount: NonNegative(request.CoherentPeakCount));

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
                return new
                {
                    schemaVersion = 1,
                    available = false,
                    ageMs = (int?)null,
                };
            }

            var ageMs = Math.Max(0L, (long)(DateTimeOffset.UtcNow - _latest.AtUtc).TotalMilliseconds);
            return new
            {
                schemaVersion = _latest.SchemaVersion,
                available = true,
                ageMs,
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
    int? CoherentPeakCount);

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
    int? CoherentPeakCount);
