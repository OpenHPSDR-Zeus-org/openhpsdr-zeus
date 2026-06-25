// SPDX-License-Identifier: GPL-2.0-or-later
//
// Persists the active TX fidelity target used by the station-profile panel and
// diagnostics. The station-profile catalog remains the source of detailed TX
// settings; this store only records which target is currently authoritative.

using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

public sealed class TxFidelityPolicyStore : IDisposable
{
    public const string DefaultProfileId = "studio-ssb";
    public const int DefaultTargetSpectralDensity = 55;

    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<TxFidelityPolicyEntry> _policy;
    private readonly ILogger<TxFidelityPolicyStore> _log;
    private readonly object _sync = new();

    public TxFidelityPolicyStore(ILogger<TxFidelityPolicyStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _policy = _db.GetCollection<TxFidelityPolicyEntry>("tx_fidelity_policy");

        _log.LogInformation("TxFidelityPolicyStore initialized at {Path}", dbPath);
    }

    public TxFidelityPolicyDto Get()
    {
        lock (_sync)
        {
            var entry = _policy.FindAll().FirstOrDefault();
            return entry is null
                ? DefaultPolicy()
                : new TxFidelityPolicyDto(
                    NormalizeProfileId(entry.ProfileId),
                    ClampDensity(entry.TargetSpectralDensity));
        }
    }

    public TxFidelityPolicyDto Set(TxFidelityPolicyDto policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var normalized = new TxFidelityPolicyDto(
            NormalizeProfileId(policy.ProfileId),
            ClampDensity(policy.TargetSpectralDensity));

        lock (_sync)
        {
            var entry = _policy.FindAll().FirstOrDefault() ?? new TxFidelityPolicyEntry();
            entry.ProfileId = normalized.ProfileId;
            entry.TargetSpectralDensity = normalized.TargetSpectralDensity;
            entry.UpdatedUtc = DateTime.UtcNow;
            if (entry.Id == 0) _policy.Insert(entry);
            else _policy.Update(entry);
        }

        return normalized;
    }

    private static TxFidelityPolicyDto DefaultPolicy() =>
        new(DefaultProfileId, DefaultTargetSpectralDensity);

    private static string NormalizeProfileId(string? profileId) =>
        string.IsNullOrWhiteSpace(profileId)
            ? DefaultProfileId
            : profileId.Trim().ToLowerInvariant();

    private static int ClampDensity(int density) =>
        Math.Clamp(density, 0, 100);

    public void Dispose() => _dbLease.Dispose();
}

public sealed class TxFidelityPolicyEntry
{
    public int Id { get; set; }
    public string ProfileId { get; set; } = TxFidelityPolicyStore.DefaultProfileId;
    public int TargetSpectralDensity { get; set; } = TxFidelityPolicyStore.DefaultTargetSpectralDensity;
    public DateTime UpdatedUtc { get; set; }
}
