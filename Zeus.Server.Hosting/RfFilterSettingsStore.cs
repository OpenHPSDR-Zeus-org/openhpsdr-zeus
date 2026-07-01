// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Thetis-style Alex RF filter matrix. Operators edit frequency windows and
// bypass policy; Protocol2Client still owns the final Alex bit encoding.

using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

public sealed class RfFilterSettingsStore : IDisposable
{
    private const int SingletonId = 1;
    private const long MinHz = 0;
    private const long MaxHz = 65_000_000;
    private const string AnanProfileKey = "anan-7000";
    private const string ClassicProfileKey = "classic-alex";

    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<RfFilterSettingsEntry> _state;
    private readonly ILogger<RfFilterSettingsStore> _log;
    private readonly object _sync = new();

    public event Action? Changed;

    public RfFilterSettingsStore(ILogger<RfFilterSettingsStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _state = _db.GetCollection<RfFilterSettingsEntry>("rf_filter_settings");
        _state.EnsureIndex(x => x.Id, unique: true);

        _log.LogInformation("RfFilterSettingsStore initialized at {Path}", dbPath);
    }

    public RfFilterSettingsDto GetDto(HpsdrBoardKind board, StateDto state, bool txActive, bool psEnabled)
    {
        var settings = GetSettings();
        string activeKey = ProfileKeyFor(board);
        var profile = settings.Profiles.First(p => p.Key == activeKey);
        long rx1Hz = ClampHz(state.VfoHz);
        long rx2Hz = ClampHz(state.Rx2Enabled ? state.Rx2().VfoHz : state.VfoHz);
        long txHz = ClampHz(RadioService.TxFrequencyHz(state));
        bool bypassed = settings.RxBypassAll
            || (txActive && settings.RxBypassOnTx)
            || (txActive && psEnabled && settings.RxBypassOnPureSignal);
        string reason = BypassReason(settings, txActive, psEnabled);

        var rx1 = ResolveActive(profile.RxFilters, rx1Hz, bypassed);
        var rx2 = ResolveActive(profile.RxFilters, rx2Hz, bypassed);
        var tx = ResolveActive(profile.TxFilters, txHz, bypassed: false);

        return new RfFilterSettingsDto(
            Supported: SupportsRfFilters(board),
            BoardFamily: BoardFamilyLabel(board),
            ActiveProfileKey: activeKey,
            CustomMatrixEnabled: settings.CustomMatrixEnabled,
            RxBypassAll: settings.RxBypassAll,
            RxBypassOnTx: settings.RxBypassOnTx,
            RxBypassOnPureSignal: settings.RxBypassOnPureSignal,
            Profiles: settings.Profiles,
            Active: new RfFilterActiveDto(
                ProfileKey: profile.Key,
                ProfileLabel: profile.Label,
                Rx1Hz: rx1Hz,
                Rx2Hz: rx2Hz,
                TxHz: txHz,
                TxActive: txActive,
                Rx1Key: rx1.Key,
                Rx1Label: rx1.Label,
                Rx2Key: rx2.Key,
                Rx2Label: rx2.Label,
                TxKey: tx.Key,
                TxLabel: tx.Label,
                Reason: reason),
            Warnings: Validate(settings.Profiles));
    }

    public RfFilterRuntimeSettings GetRuntime(HpsdrBoardKind board)
    {
        var settings = GetSettings();
        var anan = settings.Profiles.First(p => p.Key == AnanProfileKey);
        var classic = settings.Profiles.First(p => p.Key == ClassicProfileKey);
        var active = settings.Profiles.First(p => p.Key == ProfileKeyFor(board));
        return new RfFilterRuntimeSettings(
            CustomMatrixEnabled: settings.CustomMatrixEnabled,
            RxBypassAll: settings.RxBypassAll,
            RxBypassOnTx: settings.RxBypassOnTx,
            RxBypassOnPureSignal: settings.RxBypassOnPureSignal,
            Anan7000RxFilters: anan.RxFilters,
            ClassicAlexRxFilters: classic.RxFilters,
            TxFilters: active.TxFilters);
    }

    public RfFilterSettingsDto Set(RfFilterSettingsSetRequest req, HpsdrBoardKind board, StateDto state, bool txActive, bool psEnabled)
    {
        ArgumentNullException.ThrowIfNull(req);
        var normalized = Normalize(req);
        lock (_sync)
        {
            _state.Upsert(ToEntry(normalized));
        }
        Changed?.Invoke();
        return GetDto(board, state, txActive, psEnabled);
    }

    public RfFilterSettingsDto Reset(HpsdrBoardKind board, StateDto state, bool txActive, bool psEnabled)
    {
        lock (_sync)
        {
            _state.Delete(SingletonId);
        }
        Changed?.Invoke();
        return GetDto(board, state, txActive, psEnabled);
    }

    private RfFilterSettingsSetRequest GetSettings()
    {
        lock (_sync)
        {
            var e = _state.FindById(SingletonId);
            return e is null ? DefaultSettings() : Normalize(FromEntry(e));
        }
    }

    private static RfFilterSettingsSetRequest Normalize(RfFilterSettingsSetRequest req)
    {
        var incoming = req.Profiles ?? Array.Empty<RfFilterProfileDto>();
        return new RfFilterSettingsSetRequest(
            CustomMatrixEnabled: req.CustomMatrixEnabled,
            RxBypassAll: req.RxBypassAll,
            RxBypassOnTx: req.RxBypassOnTx,
            RxBypassOnPureSignal: req.RxBypassOnPureSignal,
            Profiles: new[]
            {
                MergeProfile(DefaultAnanProfile(), incoming.FirstOrDefault(p => p.Key == AnanProfileKey)),
                MergeProfile(DefaultClassicProfile(), incoming.FirstOrDefault(p => p.Key == ClassicProfileKey)),
            });
    }

    private static RfFilterProfileDto MergeProfile(RfFilterProfileDto defaults, RfFilterProfileDto? incoming)
    {
        if (incoming is null) return defaults;
        return defaults with
        {
            RxFilters = MergeRanges(defaults.RxFilters, incoming.RxFilters),
            TxFilters = MergeRanges(defaults.TxFilters, incoming.TxFilters),
        };
    }

    private static IReadOnlyList<RfFilterRangeDto> MergeRanges(
        IReadOnlyList<RfFilterRangeDto> defaults,
        IReadOnlyList<RfFilterRangeDto>? incoming)
    {
        var byKey = (incoming ?? Array.Empty<RfFilterRangeDto>())
            .Where(r => !string.IsNullOrWhiteSpace(r.Key))
            .GroupBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        return defaults
            .Select(d => byKey.TryGetValue(d.Key, out var r)
                ? d with
                {
                    StartHz = ClampHz(r.StartHz),
                    EndHz = Math.Max(ClampHz(r.StartHz), ClampHz(r.EndHz)),
                    ForceBypass = r.ForceBypass,
                }
                : d)
            .ToArray();
    }

    private static IReadOnlyList<string> Validate(IReadOnlyList<RfFilterProfileDto> profiles)
    {
        var warnings = new List<string>();
        foreach (var profile in profiles)
        {
            ValidateRows(warnings, $"{profile.Label} RX", profile.RxFilters);
            ValidateRows(warnings, $"{profile.Label} TX", profile.TxFilters);
        }
        return warnings;
    }

    private static void ValidateRows(List<string> warnings, string label, IReadOnlyList<RfFilterRangeDto> rows)
    {
        var ordered = rows.OrderBy(r => r.StartHz).ToArray();
        long? previousEnd = null;
        foreach (var row in ordered)
        {
            if (previousEnd is long end)
            {
                if (row.StartHz <= end)
                    warnings.Add($"{label}: {row.Label} overlaps the previous row.");
                else if (row.StartHz > end + 1)
                    warnings.Add($"{label}: gap before {row.Label}.");
            }
            previousEnd = row.EndHz;
        }
    }

    private static RfFilterRangeDto ResolveActive(IReadOnlyList<RfFilterRangeDto> rows, long hz, bool bypassed)
    {
        if (bypassed)
            return new RfFilterRangeDto("bypass", "Bypass (forced)", hz, hz);
        var row = rows.FirstOrDefault(r => hz >= r.StartHz && hz <= r.EndHz)
            ?? new RfFilterRangeDto("auto", "Auto fallback", hz, hz);
        return row.ForceBypass
            ? row with { Label = $"{row.Label} -> Bypass" }
            : row;
    }

    private static string BypassReason(RfFilterSettingsSetRequest settings, bool txActive, bool psEnabled)
    {
        if (settings.RxBypassAll) return "RX BPF bypass is forced.";
        if (txActive && psEnabled && settings.RxBypassOnPureSignal)
            return "RX BPF bypassed while PureSignal is active.";
        if (txActive && settings.RxBypassOnTx)
            return "RX BPF bypassed while transmitting.";
        return settings.CustomMatrixEnabled ? "Custom matrix active." : "Legacy auto matrix active.";
    }

    private static bool SupportsRfFilters(HpsdrBoardKind board) =>
        board is not HpsdrBoardKind.HermesLite2 and not HpsdrBoardKind.Unknown;

    private static string ProfileKeyFor(HpsdrBoardKind board) =>
        board is HpsdrBoardKind.Hermes or HpsdrBoardKind.HermesII
            ? ClassicProfileKey
            : AnanProfileKey;

    private static string BoardFamilyLabel(HpsdrBoardKind board) =>
        ProfileKeyFor(board) == ClassicProfileKey
            ? "Classic Alex HPF"
            : "ANAN-7000 / Saturn BPF";

    private static long ClampHz(long hz) => Math.Clamp(hz, MinHz, MaxHz);

    private static RfFilterSettingsSetRequest DefaultSettings() => new(
        CustomMatrixEnabled: false,
        RxBypassAll: false,
        RxBypassOnTx: false,
        RxBypassOnPureSignal: false,
        Profiles: new[] { DefaultAnanProfile(), DefaultClassicProfile() });

    private static RfFilterProfileDto DefaultAnanProfile() => new(
        Key: AnanProfileKey,
        Label: "ANAN-7000 / Saturn BPF",
        RxFilters: new[]
        {
            Range("bypass", "Bypass", 0, 1_499_999),
            Range("160", "160 m", 1_500_000, 2_099_999),
            Range("80_60", "80 / 60 m", 2_100_000, 5_499_999),
            Range("40_30", "40 / 30 m", 5_500_000, 10_999_999),
            Range("20_15", "20 / 15 m", 11_000_000, 21_999_999),
            Range("12_10", "12 / 10 m", 22_000_000, 34_999_999),
            Range("6_pre", "6 m / preamp", 35_000_000, MaxHz),
        },
        TxFilters: DefaultTxFilters());

    private static RfFilterProfileDto DefaultClassicProfile() => new(
        Key: ClassicProfileKey,
        Label: "Classic Alex HPF",
        RxFilters: new[]
        {
            Range("bypass", "Bypass HPF", 0, 1_799_999),
            Range("1_5", "1.5 MHz HPF", 1_800_000, 6_499_999),
            Range("6_5", "6.5 MHz HPF", 6_500_000, 9_499_999),
            Range("9_5", "9.5 MHz HPF", 9_500_000, 12_999_999),
            Range("13", "13 MHz HPF", 13_000_000, 19_999_999),
            Range("20", "20 MHz HPF", 20_000_000, 49_999_999),
            Range("6_pre", "6 m preamp", 50_000_000, MaxHz),
        },
        TxFilters: DefaultTxFilters());

    private static IReadOnlyList<RfFilterRangeDto> DefaultTxFilters() => new[]
    {
        Range("160", "160 m LPF", 0, 2_500_000),
        Range("80", "80 m LPF", 2_500_001, 5_000_000),
        Range("60_40", "60 / 40 m LPF", 5_000_001, 8_000_000),
        Range("30_20", "30 / 20 m LPF", 8_000_001, 16_500_000),
        Range("17_15", "17 / 15 m LPF", 16_500_001, 24_000_000),
        Range("12_10", "12 / 10 m LPF", 24_000_001, 35_600_000),
        Range("6_bypass", "6 m / bypass LPF", 35_600_001, MaxHz),
    };

    private static RfFilterRangeDto Range(string key, string label, long startHz, long endHz, bool bypass = false) =>
        new(key, label, startHz, endHz, bypass);

    private static RfFilterSettingsEntry ToEntry(RfFilterSettingsSetRequest s) => new()
    {
        Id = SingletonId,
        CustomMatrixEnabled = s.CustomMatrixEnabled,
        RxBypassAll = s.RxBypassAll,
        RxBypassOnTx = s.RxBypassOnTx,
        RxBypassOnPureSignal = s.RxBypassOnPureSignal,
        Profiles = s.Profiles.Select(ToProfileEntry).ToList(),
        UpdatedUtc = DateTime.UtcNow,
    };

    private static RfFilterProfileEntry ToProfileEntry(RfFilterProfileDto p) => new()
    {
        Key = p.Key,
        Label = p.Label,
        RxFilters = p.RxFilters.Select(ToRangeEntry).ToList(),
        TxFilters = p.TxFilters.Select(ToRangeEntry).ToList(),
    };

    private static RfFilterRangeEntry ToRangeEntry(RfFilterRangeDto r) => new()
    {
        Key = r.Key,
        Label = r.Label,
        StartHz = r.StartHz,
        EndHz = r.EndHz,
        ForceBypass = r.ForceBypass,
    };

    private static RfFilterSettingsSetRequest FromEntry(RfFilterSettingsEntry e) => new(
        CustomMatrixEnabled: e.CustomMatrixEnabled,
        RxBypassAll: e.RxBypassAll,
        RxBypassOnTx: e.RxBypassOnTx,
        RxBypassOnPureSignal: e.RxBypassOnPureSignal,
        Profiles: (e.Profiles ?? new()).Select(FromProfileEntry).ToArray());

    private static RfFilterProfileDto FromProfileEntry(RfFilterProfileEntry p) => new(
        Key: p.Key,
        Label: p.Label,
        RxFilters: (p.RxFilters ?? new()).Select(FromRangeEntry).ToArray(),
        TxFilters: (p.TxFilters ?? new()).Select(FromRangeEntry).ToArray());

    private static RfFilterRangeDto FromRangeEntry(RfFilterRangeEntry r) =>
        new(r.Key, r.Label, r.StartHz, r.EndHz, r.ForceBypass);

    public void Dispose() => _dbLease.Dispose();
}

public sealed class RfFilterSettingsEntry
{
    public int Id { get; set; }
    public bool CustomMatrixEnabled { get; set; }
    public bool RxBypassAll { get; set; }
    public bool RxBypassOnTx { get; set; }
    public bool RxBypassOnPureSignal { get; set; }
    public List<RfFilterProfileEntry> Profiles { get; set; } = new();
    public DateTime UpdatedUtc { get; set; }
}

public sealed class RfFilterProfileEntry
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public List<RfFilterRangeEntry> RxFilters { get; set; } = new();
    public List<RfFilterRangeEntry> TxFilters { get; set; } = new();
}

public sealed class RfFilterRangeEntry
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public long StartHz { get; set; }
    public long EndHz { get; set; }
    public bool ForceBypass { get; set; }
}
