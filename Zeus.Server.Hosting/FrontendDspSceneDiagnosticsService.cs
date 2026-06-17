// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Zeus.Contracts;

namespace Zeus.Server;

public sealed class FrontendDspSceneDiagnosticsService
{
    private const int MaxText = 180;
    private const long FreshSceneMs = 15_000;
    private const long StaleSceneMs = 45_000;
    private const long FutureSourceToleranceMs = 5_000;
    private readonly object _sync = new();
    private FrontendDspSceneSnapshot? _latest;
    private FrontendDspSceneSnapshot? _latestAdjacentNoise;

    public FrontendDspSceneSnapshot Update(FrontendDspSceneDiagnosticsRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new FrontendDspSceneSnapshot(
            SchemaVersion: 1,
            AtUtc: now,
            SourceAtUtc: request.SourceAtUtc,
            SourceClientId: Clean(request.SourceClientId, 64),
            Mode: Clean(request.Mode, 16),
            SignalProfile: Clean(request.SignalProfile, 48),
            SignalReason: Clean(request.SignalReason, MaxText),
            SmartNrProfile: Clean(request.SmartNrProfile, 48),
            SmartNrReason: Clean(request.SmartNrReason, MaxText),
            SmartNrRecommendation: Clean(request.SmartNrRecommendation, MaxText),
            SmartNrHeldByRxChain: request.SmartNrHeldByRxChain,
            SmartNrRxChainLabel: Clean(request.SmartNrRxChainLabel, 80),
            SmartNrRxChainRecommendation: Clean(request.SmartNrRxChainRecommendation, MaxText),
            SmartNrRxChainTone: CleanRxChainTone(request.SmartNrRxChainTone),
            SmartNrRxChainScore: Score(request.SmartNrRxChainScore),
            MaxSnrDb: Finite(request.MaxSnrDb),
            CoherentMaxSnrDb: Finite(request.CoherentMaxSnrDb),
            OccupiedPct: Percent(request.OccupiedPct),
            CoherentOccupiedPct: Percent(request.CoherentOccupiedPct),
            ImpulsivePct: Percent(request.ImpulsivePct),
            PeakCount: NonNegative(request.PeakCount),
            CoherentPeakCount: NonNegative(request.CoherentPeakCount),
            CoherentSubthresholdSignal: request.CoherentSubthresholdSignal,
            AdjacentNoiseUsable: request.AdjacentNoiseUsable,
            AdjacentNoiseBins: NonNegative(request.AdjacentNoiseBins),
            AdjacentNoiseLeftBins: NonNegative(request.AdjacentNoiseLeftBins),
            AdjacentNoiseRightBins: NonNegative(request.AdjacentNoiseRightBins),
            AdjacentNoiseFloorDb: Finite(request.AdjacentNoiseFloorDb),
            AdjacentNoiseP10Db: Finite(request.AdjacentNoiseP10Db),
            AdjacentNoiseP50Db: Finite(request.AdjacentNoiseP50Db),
            AdjacentNoiseP90Db: Finite(request.AdjacentNoiseP90Db),
            AdjacentNoiseLeftFloorDb: Finite(request.AdjacentNoiseLeftFloorDb),
            AdjacentNoiseRightFloorDb: Finite(request.AdjacentNoiseRightFloorDb),
            AdjacentNoiseSlopeDbPerKhz: Finite(request.AdjacentNoiseSlopeDbPerKhz),
            AdjacentNoiseRejectedPct: Percent(request.AdjacentNoiseRejectedPct));

        lock (_sync)
        {
            _latest = snapshot;
            if (HasUsableAdjacentNoiseProfile(snapshot))
                _latestAdjacentNoise = snapshot;
            return snapshot;
        }
    }

    public object Snapshot()
    {
        lock (_sync)
        {
            if (_latest is null)
            {
                var health = Health(null, null, null, null);
                return new
                {
                    schemaVersion = 1,
                    available = false,
                    ageMs = (int?)null,
                    sourceAtUtc = (DateTimeOffset?)null,
                    sourceAgeMs = (int?)null,
                    sourceClockSkewMs = (int?)null,
                    status = health.Status,
                    fresh = false,
                    stale = false,
                    diagnosticRecommendation = health.Recommendation,
                };
            }

            var timing = Timing(_latest, DateTimeOffset.UtcNow);
            return new
            {
                schemaVersion = _latest.SchemaVersion,
                available = true,
                ageMs = timing.AgeMs,
                sourceAtUtc = _latest.SourceAtUtc,
                sourceAgeMs = timing.SourceAgeMs,
                sourceClockSkewMs = timing.SourceClockSkewMs,
                status = timing.Health.Status,
                fresh = timing.Health.Fresh,
                stale = timing.Health.Stale,
                diagnosticRecommendation = timing.Health.Recommendation,
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
                smartNrRxChainRecommendation = _latest.SmartNrRxChainRecommendation,
                smartNrRxChainTone = _latest.SmartNrRxChainTone,
                smartNrRxChainScore = _latest.SmartNrRxChainScore,
                maxSnrDb = _latest.MaxSnrDb,
                coherentMaxSnrDb = _latest.CoherentMaxSnrDb,
                occupiedPct = _latest.OccupiedPct,
                coherentOccupiedPct = _latest.CoherentOccupiedPct,
                impulsivePct = _latest.ImpulsivePct,
                peakCount = _latest.PeakCount,
                coherentPeakCount = _latest.CoherentPeakCount,
                coherentSubthresholdSignal = _latest.CoherentSubthresholdSignal,
                adjacentNoiseUsable = _latest.AdjacentNoiseUsable,
                adjacentNoiseBins = _latest.AdjacentNoiseBins,
                adjacentNoiseLeftBins = _latest.AdjacentNoiseLeftBins,
                adjacentNoiseRightBins = _latest.AdjacentNoiseRightBins,
                adjacentNoiseFloorDb = _latest.AdjacentNoiseFloorDb,
                adjacentNoiseP10Db = _latest.AdjacentNoiseP10Db,
                adjacentNoiseP50Db = _latest.AdjacentNoiseP50Db,
                adjacentNoiseP90Db = _latest.AdjacentNoiseP90Db,
                adjacentNoiseLeftFloorDb = _latest.AdjacentNoiseLeftFloorDb,
                adjacentNoiseRightFloorDb = _latest.AdjacentNoiseRightFloorDb,
                adjacentNoiseSlopeDbPerKhz = _latest.AdjacentNoiseSlopeDbPerKhz,
                adjacentNoiseRejectedPct = _latest.AdjacentNoiseRejectedPct,
            };
        }
    }

    public FrontendAdjacentNoiseProfileSnapshot? TryGetFreshAdjacentNoiseProfile()
    {
        lock (_sync)
        {
            if (_latestAdjacentNoise is null)
                return null;

            var timing = Timing(_latestAdjacentNoise, DateTimeOffset.UtcNow);
            if (!timing.Health.Fresh ||
                _latestAdjacentNoise.AdjacentNoiseUsable != true ||
                _latestAdjacentNoise.AdjacentNoiseBins is not { } bins ||
                bins < 24 ||
                _latestAdjacentNoise.AdjacentNoiseFloorDb is not { } floorDb)
            {
                return null;
            }

            double p10 = _latestAdjacentNoise.AdjacentNoiseP10Db ?? floorDb;
            double p50 = _latestAdjacentNoise.AdjacentNoiseP50Db ?? floorDb;
            double p90 = _latestAdjacentNoise.AdjacentNoiseP90Db ?? floorDb;
            int leftBins = _latestAdjacentNoise.AdjacentNoiseLeftBins ?? 0;
            int rightBins = _latestAdjacentNoise.AdjacentNoiseRightBins ?? 0;
            double leftFloor = _latestAdjacentNoise.AdjacentNoiseLeftFloorDb ?? floorDb;
            double rightFloor = _latestAdjacentNoise.AdjacentNoiseRightFloorDb ?? floorDb;
            double slope = _latestAdjacentNoise.AdjacentNoiseSlopeDbPerKhz ?? 0.0;
            double rejected = _latestAdjacentNoise.AdjacentNoiseRejectedPct ?? 100.0;

            return new(
                AgeMs: timing.AgeMs,
                SourceAgeMs: timing.SourceAgeMs,
                Bins: bins,
                LeftBins: leftBins,
                RightBins: rightBins,
                FloorDb: floorDb,
                P10Db: p10,
                P50Db: p50,
                P90Db: p90,
                LeftFloorDb: leftFloor,
                RightFloorDb: rightFloor,
                SlopeDbPerKhz: slope,
                RejectedPct: rejected);
        }
    }

    public SmartNrConditionDto SmartNrCondition(
        DspNrRuntimeSnapshot nrRuntime,
        SmartNrRxChainRuntimeDto? rxChain = null)
    {
        var now = DateTimeOffset.UtcNow;
        rxChain ??= SmartNrRxChainRuntimeDto.Unknown;

        lock (_sync)
        {
            if (_latest is null)
            {
                var health = Health(null, null, null, null);
                var alignment = NrRuntimeAlignment(null, nrRuntime);
                return new(
                    SchemaVersion: 1,
                    Available: false,
                    Status: health.Status,
                    Fresh: false,
                    Stale: false,
                    AgeMs: null,
                    AtUtc: null,
                    SourceAtUtc: null,
                    SourceAgeMs: null,
                    SourceClockSkewMs: null,
                    SourceClientId: null,
                    Mode: null,
                    Profile: null,
                    Reason: null,
                    Recommendation: null,
                    HeldByRxChain: null,
                    RxChainLabel: null,
                    RxChainRecommendation: null,
                    RxChainTone: null,
                    RxChainScore: null,
                    MaxSnrDb: null,
                    CoherentMaxSnrDb: null,
                    OccupiedPct: null,
                    CoherentOccupiedPct: null,
                    ImpulsivePct: null,
                    PeakCount: null,
                    CoherentPeakCount: null,
                    CoherentSubthresholdSignal: null,
                    AdjacentNoiseUsable: null,
                    AdjacentNoiseBins: null,
                    AdjacentNoiseLeftBins: null,
                    AdjacentNoiseRightBins: null,
                    AdjacentNoiseFloorDb: null,
                    AdjacentNoiseP10Db: null,
                    AdjacentNoiseP50Db: null,
                    AdjacentNoiseP90Db: null,
                    AdjacentNoiseLeftFloorDb: null,
                    AdjacentNoiseRightFloorDb: null,
                    AdjacentNoiseSlopeDbPerKhz: null,
                    AdjacentNoiseRejectedPct: null,
                    WdspActive: nrRuntime.WdspActive,
                    WdspNativeLoadable: nrRuntime.WdspNativeLoadable,
                    WdspEmnrPost2Available: nrRuntime.WdspEmnrPost2Available,
                    WdspNr4SbnrAvailable: nrRuntime.WdspNr4SbnrAvailable,
                    WdspNr5SpnrAvailable: nrRuntime.WdspNr5SpnrAvailable,
                    Nr4Readiness: nrRuntime.Nr4Readiness,
                    Nr5Readiness: nrRuntime.Nr5Readiness,
                    RequestedNrMode: nrRuntime.RequestedNrMode,
                    EffectiveNrMode: nrRuntime.EffectiveNrMode,
                    Nr5SpnrDiagnostics: nrRuntime.Nr5SpnrDiagnostics,
                    ExpectedNrMode: alignment.ExpectedNrMode,
                    RuntimeAligned: alignment.RuntimeAligned,
                    RuntimeAlignmentStatus: alignment.Status,
                    RuntimeAlignmentRecommendation: alignment.Recommendation,
                    RxChain: rxChain,
                    DiagnosticRecommendation: health.Recommendation,
                    GeneratedUtc: now);
            }

            var timing = Timing(_latest, now);
            var runtimeAlignment = NrRuntimeAlignment(_latest, nrRuntime);
            return new(
                SchemaVersion: _latest.SchemaVersion,
                Available: true,
                Status: timing.Health.Status,
                Fresh: timing.Health.Fresh,
                Stale: timing.Health.Stale,
                AgeMs: timing.AgeMs,
                AtUtc: _latest.AtUtc,
                SourceAtUtc: _latest.SourceAtUtc,
                SourceAgeMs: timing.SourceAgeMs,
                SourceClockSkewMs: timing.SourceClockSkewMs,
                SourceClientId: _latest.SourceClientId,
                Mode: _latest.Mode,
                Profile: _latest.SmartNrProfile,
                Reason: _latest.SmartNrReason,
                Recommendation: _latest.SmartNrRecommendation,
                HeldByRxChain: _latest.SmartNrHeldByRxChain,
                RxChainLabel: _latest.SmartNrRxChainLabel,
                RxChainRecommendation: _latest.SmartNrRxChainRecommendation,
                RxChainTone: _latest.SmartNrRxChainTone,
                RxChainScore: _latest.SmartNrRxChainScore,
                MaxSnrDb: _latest.MaxSnrDb,
                CoherentMaxSnrDb: _latest.CoherentMaxSnrDb,
                OccupiedPct: _latest.OccupiedPct,
                CoherentOccupiedPct: _latest.CoherentOccupiedPct,
                ImpulsivePct: _latest.ImpulsivePct,
                PeakCount: _latest.PeakCount,
                CoherentPeakCount: _latest.CoherentPeakCount,
                CoherentSubthresholdSignal: _latest.CoherentSubthresholdSignal,
                AdjacentNoiseUsable: _latest.AdjacentNoiseUsable,
                AdjacentNoiseBins: _latest.AdjacentNoiseBins,
                AdjacentNoiseLeftBins: _latest.AdjacentNoiseLeftBins,
                AdjacentNoiseRightBins: _latest.AdjacentNoiseRightBins,
                AdjacentNoiseFloorDb: _latest.AdjacentNoiseFloorDb,
                AdjacentNoiseP10Db: _latest.AdjacentNoiseP10Db,
                AdjacentNoiseP50Db: _latest.AdjacentNoiseP50Db,
                AdjacentNoiseP90Db: _latest.AdjacentNoiseP90Db,
                AdjacentNoiseLeftFloorDb: _latest.AdjacentNoiseLeftFloorDb,
                AdjacentNoiseRightFloorDb: _latest.AdjacentNoiseRightFloorDb,
                AdjacentNoiseSlopeDbPerKhz: _latest.AdjacentNoiseSlopeDbPerKhz,
                AdjacentNoiseRejectedPct: _latest.AdjacentNoiseRejectedPct,
                WdspActive: nrRuntime.WdspActive,
                WdspNativeLoadable: nrRuntime.WdspNativeLoadable,
                WdspEmnrPost2Available: nrRuntime.WdspEmnrPost2Available,
                WdspNr4SbnrAvailable: nrRuntime.WdspNr4SbnrAvailable,
                WdspNr5SpnrAvailable: nrRuntime.WdspNr5SpnrAvailable,
                Nr4Readiness: nrRuntime.Nr4Readiness,
                Nr5Readiness: nrRuntime.Nr5Readiness,
                RequestedNrMode: nrRuntime.RequestedNrMode,
                EffectiveNrMode: nrRuntime.EffectiveNrMode,
                Nr5SpnrDiagnostics: nrRuntime.Nr5SpnrDiagnostics,
                ExpectedNrMode: runtimeAlignment.ExpectedNrMode,
                RuntimeAligned: runtimeAlignment.RuntimeAligned,
                RuntimeAlignmentStatus: runtimeAlignment.Status,
                RuntimeAlignmentRecommendation: runtimeAlignment.Recommendation,
                RxChain: rxChain,
                DiagnosticRecommendation: timing.Health.Recommendation,
                GeneratedUtc: now);
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

    private static int? Score(int? value) =>
        value is { } v ? Math.Clamp(v, 0, 100) : null;

    private static string? CleanRxChainTone(string? raw)
    {
        var tone = Clean(raw, 16)?.ToLowerInvariant();
        return tone is "neutral" or "optimize" or "protect" ? tone : null;
    }

    private static FrontendDspSceneTiming Timing(FrontendDspSceneSnapshot latest, DateTimeOffset now)
    {
        var ageMs = Math.Max(0L, (long)(now - latest.AtUtc).TotalMilliseconds);
        long? sourceAgeMs = null;
        long? sourceClockSkewMs = null;
        if (latest.SourceAtUtc is { } sourceAt)
        {
            var sourceDeltaMs = (long)(now - sourceAt).TotalMilliseconds;
            if (sourceDeltaMs >= 0)
            {
                sourceAgeMs = sourceDeltaMs;
            }
            else
            {
                sourceAgeMs = 0;
                sourceClockSkewMs = -sourceDeltaMs;
            }
        }

        return new(ageMs, sourceAgeMs, sourceClockSkewMs, Health(ageMs, sourceAgeMs, sourceClockSkewMs, latest));
    }

    private static FrontendDspSceneHealth Health(
        long? ageMs,
        long? sourceAgeMs,
        long? sourceClockSkewMs,
        FrontendDspSceneSnapshot? latest)
    {
        if (ageMs is null)
        {
            return new(
                "missing",
                Fresh: false,
                Stale: false,
                "No frontend DSP scene has been published yet; open a Zeus client with Signal Intelligence or Smart NR active to audit weak-signal automation.");
        }

        if (ageMs.Value <= StaleSceneMs && sourceClockSkewMs is { } skewMs && skewMs > FutureSourceToleranceMs)
        {
            return new(
                "clock-skew",
                Fresh: false,
                Stale: false,
                $"Frontend DSP scene source timestamp is {(skewMs / 1000.0):0.0}s in the future; verify client and host clocks before trusting weak-signal automation freshness.");
        }

        long effectiveAgeMs = Math.Max(ageMs.Value, sourceAgeMs ?? ageMs.Value);

        if (effectiveAgeMs > StaleSceneMs)
        {
            string staleSubject = sourceAgeMs is { } sourceAge && sourceAge > StaleSceneMs
                ? "source evidence"
                : "telemetry";
            return new(
                "stale",
                Fresh: false,
                Stale: true,
                $"Frontend DSP scene {staleSubject} is stale; verify a client is open and publishing live Signal Intelligence and Smart NR evidence.");
        }

        if (effectiveAgeMs > FreshSceneMs)
        {
            string agingSubject = sourceAgeMs is { } sourceAge && sourceAge > FreshSceneMs
                ? "source evidence"
                : "telemetry";
            return new(
                "aging",
                Fresh: false,
                Stale: false,
                $"Frontend DSP scene {agingSubject} is aging; wait for the next live spectrum refresh or check client connectivity if this persists.");
        }

        if (!HasSceneEvidence(latest))
        {
            return new(
                "fresh",
                Fresh: true,
                Stale: false,
                "Frontend DSP scene heartbeat is fresh; no Signal Intelligence or Smart NR source evidence has been published yet.");
        }

        bool coherentSubthreshold = latest?.CoherentSubthresholdSignal == true;
        if (coherentSubthreshold && latest?.SmartNrHeldByRxChain == true)
        {
            var reason = RxChainStatusReason(latest);
            return new(
                "fresh",
                Fresh: true,
                Stale: false,
                $"Frontend DSP scene telemetry is fresh; a coherent subthreshold weak-signal ridge is present, but Smart NR is constrained by RX-chain health{reason}.");
        }

        if (coherentSubthreshold)
        {
            return new(
                "fresh",
                Fresh: true,
                Stale: false,
                "Frontend DSP scene telemetry is fresh; coherent subthreshold weak-signal evidence is present, so preserve RX headroom and use gentle Smart NR/filtering.");
        }

        if (latest?.SmartNrHeldByRxChain == true)
        {
            var reason = RxChainStatusReason(latest);
            return new(
                "fresh",
                Fresh: true,
                Stale: false,
                $"Frontend DSP scene telemetry is fresh; Smart NR is currently constrained by RX-chain health{reason}.");
        }

        if (latest?.SmartNrRxChainScore is { } score && score < 80)
        {
            var reason = RxChainStatusReason(latest);
            return new(
                "fresh",
                Fresh: true,
                Stale: false,
                $"Frontend DSP scene telemetry is fresh; RX-chain health needs attention{reason}.");
        }

        return new(
            "fresh",
            Fresh: true,
            Stale: false,
            "Frontend DSP scene telemetry is fresh and ready for remote diagnostics.");

    }

    private static bool HasSceneEvidence(FrontendDspSceneSnapshot? latest) =>
        latest is not null
        && (!string.IsNullOrWhiteSpace(latest.SignalProfile)
            || !string.IsNullOrWhiteSpace(latest.SignalReason)
            || !string.IsNullOrWhiteSpace(latest.SmartNrProfile)
            || !string.IsNullOrWhiteSpace(latest.SmartNrReason)
            || !string.IsNullOrWhiteSpace(latest.SmartNrRecommendation)
            || !string.IsNullOrWhiteSpace(latest.SmartNrRxChainLabel)
            || !string.IsNullOrWhiteSpace(latest.SmartNrRxChainRecommendation)
            || !string.IsNullOrWhiteSpace(latest.SmartNrRxChainTone)
            || latest.SmartNrRxChainScore is not null
            || latest.MaxSnrDb is not null
            || latest.CoherentMaxSnrDb is not null
            || latest.OccupiedPct is not null
            || latest.CoherentOccupiedPct is not null
            || latest.ImpulsivePct is not null
            || latest.PeakCount is not null
            || latest.CoherentPeakCount is not null
            || latest.CoherentSubthresholdSignal is not null
            || latest.AdjacentNoiseUsable is not null
            || latest.AdjacentNoiseBins is not null
            || latest.AdjacentNoiseFloorDb is not null
            || latest.AdjacentNoiseRejectedPct is not null);

    private static bool HasUsableAdjacentNoiseProfile(FrontendDspSceneSnapshot latest) =>
        latest.AdjacentNoiseUsable == true &&
        latest.AdjacentNoiseBins is >= 24 &&
        latest.AdjacentNoiseFloorDb is not null;

    private static SmartNrRuntimeAlignment NrRuntimeAlignment(
        FrontendDspSceneSnapshot? latest,
        DspNrRuntimeSnapshot nrRuntime)
    {
        string? profile = Clean(latest?.SmartNrProfile, 48);
        if (string.IsNullOrWhiteSpace(profile))
        {
            return new(
                ExpectedNrMode: null,
                RuntimeAligned: null,
                Status: "no-profile",
                Recommendation: "No Smart NR profile has been published yet, so backend NR runtime alignment cannot be evaluated.");
        }

        string? expected = ExpectedNrMode(profile);
        if (expected is null)
        {
            return new(
                ExpectedNrMode: null,
                RuntimeAligned: null,
                Status: "profile-not-mapped",
                Recommendation: $"Smart NR profile {profile} has no one-to-one WDSP NR mode mapping; use the requested/effective NR mode and notch/blanker diagnostics directly.");
        }

        if (!nrRuntime.WdspActive)
        {
            return new(
                ExpectedNrMode: expected,
                RuntimeAligned: false,
                Status: "runtime-inactive",
                Recommendation: $"Smart NR recommends {profile}, which maps to WDSP {expected}, but WDSP is not active yet.");
        }

        if (string.Equals(nrRuntime.EffectiveNrMode, expected, StringComparison.OrdinalIgnoreCase))
        {
            return new(
                ExpectedNrMode: expected,
                RuntimeAligned: true,
                Status: "aligned",
                Recommendation: $"Smart NR profile {profile} is aligned with the effective WDSP NR mode ({nrRuntime.EffectiveNrMode}).");
        }

        if (string.Equals(nrRuntime.RequestedNrMode, expected, StringComparison.OrdinalIgnoreCase))
        {
            return new(
                ExpectedNrMode: expected,
                RuntimeAligned: false,
                Status: "apply-pending",
                Recommendation: $"Smart NR profile {profile} maps to WDSP {expected}, and that mode is requested, but the effective runtime is still {nrRuntime.EffectiveNrMode}; wait for the DSP apply path before judging audio.");
        }

        return new(
            ExpectedNrMode: expected,
            RuntimeAligned: false,
            Status: "mismatched",
            Recommendation: $"Smart NR profile {profile} maps to WDSP {expected}, but the backend is requested={nrRuntime.RequestedNrMode} effective={nrRuntime.EffectiveNrMode}; reapply Smart NR or inspect the DSP apply path before tuning by ear.");
    }

    private static string? ExpectedNrMode(string profile)
    {
        var normalized = profile.Trim().ToUpperInvariant();
        return normalized switch
        {
            "NR1" => "Anr",
            "NR2" => "Emnr",
            "NR4" => "Sbnr",
            "NR5" => "Nr5",
            "LIGHT" => "Off",
            "NOTCH" => "Off",
            _ => null,
        };
    }

    private sealed record FrontendDspSceneHealth(
        string Status,
        bool Fresh,
        bool Stale,
        string Recommendation);

    private sealed record SmartNrRuntimeAlignment(
        string? ExpectedNrMode,
        bool? RuntimeAligned,
        string Status,
        string Recommendation);

    private sealed record FrontendDspSceneTiming(
        long AgeMs,
        long? SourceAgeMs,
        long? SourceClockSkewMs,
        FrontendDspSceneHealth Health);

    private static string RxChainStatusReason(FrontendDspSceneSnapshot? latest)
    {
        if (latest is null) return "";
        var label = Clean(latest.SmartNrRxChainLabel, 80);
        var recommendation = Clean(latest.SmartNrRxChainRecommendation, MaxText);
        var score = latest.SmartNrRxChainScore is { } s ? $" score {s}/100" : "";
        if (!string.IsNullOrWhiteSpace(label) && !string.IsNullOrWhiteSpace(recommendation))
            return $": {label}{score}; {recommendation}";
        if (!string.IsNullOrWhiteSpace(label))
            return $": {label}{score}";
        if (!string.IsNullOrWhiteSpace(recommendation))
            return $": {recommendation}{score}";
        return score.Length > 0 ? $":{score}" : "";
    }
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
    string? SmartNrRxChainRecommendation,
    string? SmartNrRxChainTone,
    int? SmartNrRxChainScore,
    double? MaxSnrDb,
    double? CoherentMaxSnrDb,
    double? OccupiedPct,
    double? CoherentOccupiedPct,
    double? ImpulsivePct,
    int? PeakCount,
    int? CoherentPeakCount,
    bool? CoherentSubthresholdSignal,
    DateTimeOffset? SourceAtUtc = null,
    bool? AdjacentNoiseUsable = null,
    int? AdjacentNoiseBins = null,
    int? AdjacentNoiseLeftBins = null,
    int? AdjacentNoiseRightBins = null,
    double? AdjacentNoiseFloorDb = null,
    double? AdjacentNoiseP10Db = null,
    double? AdjacentNoiseP50Db = null,
    double? AdjacentNoiseP90Db = null,
    double? AdjacentNoiseLeftFloorDb = null,
    double? AdjacentNoiseRightFloorDb = null,
    double? AdjacentNoiseSlopeDbPerKhz = null,
    double? AdjacentNoiseRejectedPct = null);

public sealed record FrontendDspSceneSnapshot(
    int SchemaVersion,
    DateTimeOffset AtUtc,
    DateTimeOffset? SourceAtUtc,
    string? SourceClientId,
    string? Mode,
    string? SignalProfile,
    string? SignalReason,
    string? SmartNrProfile,
    string? SmartNrReason,
    string? SmartNrRecommendation,
    bool? SmartNrHeldByRxChain,
    string? SmartNrRxChainLabel,
    string? SmartNrRxChainRecommendation,
    string? SmartNrRxChainTone,
    int? SmartNrRxChainScore,
    double? MaxSnrDb,
    double? CoherentMaxSnrDb,
    double? OccupiedPct,
    double? CoherentOccupiedPct,
    double? ImpulsivePct,
    int? PeakCount,
    int? CoherentPeakCount,
    bool? CoherentSubthresholdSignal,
    bool? AdjacentNoiseUsable,
    int? AdjacentNoiseBins,
    int? AdjacentNoiseLeftBins,
    int? AdjacentNoiseRightBins,
    double? AdjacentNoiseFloorDb,
    double? AdjacentNoiseP10Db,
    double? AdjacentNoiseP50Db,
    double? AdjacentNoiseP90Db,
    double? AdjacentNoiseLeftFloorDb,
    double? AdjacentNoiseRightFloorDb,
    double? AdjacentNoiseSlopeDbPerKhz,
    double? AdjacentNoiseRejectedPct);

public sealed record FrontendAdjacentNoiseProfileSnapshot(
    long AgeMs,
    long? SourceAgeMs,
    int Bins,
    int LeftBins,
    int RightBins,
    double FloorDb,
    double P10Db,
    double P50Db,
    double P90Db,
    double LeftFloorDb,
    double RightFloorDb,
    double SlopeDbPerKhz,
    double RejectedPct);
