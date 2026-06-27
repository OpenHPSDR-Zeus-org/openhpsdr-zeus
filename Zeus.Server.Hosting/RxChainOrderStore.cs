// SPDX-License-Identifier: GPL-2.0-or-later
using LiteDB;

namespace Zeus.Server;

/// <summary>
/// Persists the operator's RX audio plugin insert order and parked state.
/// Separate from <see cref="ChainOrderStore"/> so RX VSTs never pollute the
/// TX Audio Suite order/profile contract.
/// </summary>
public sealed class RxChainOrderStore : IDisposable
{
    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<RxChainOrderEntry> _state;
    private readonly ILogger<RxChainOrderStore> _log;
    private readonly object _sync = new();

    public RxChainOrderStore(ILogger<RxChainOrderStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _state = _db.GetCollection<RxChainOrderEntry>("rx_audio_chain_order");

        _log.LogInformation("RxChainOrderStore initialized at {Path}", dbPath);
    }

    public IReadOnlyList<string>? GetOrder()
    {
        lock (_sync)
        {
            var entry = _state.FindAll().FirstOrDefault();
            return entry?.PluginIds;
        }
    }

    public IReadOnlyList<string> GetParked()
    {
        lock (_sync)
        {
            var entry = _state.FindAll().FirstOrDefault();
            return entry?.ParkedPluginIds ?? new List<string>();
        }
    }

    public void SetState(IReadOnlyList<string> pluginIds, IReadOnlyList<string> parkedIds)
    {
        lock (_sync)
        {
            var existing = _state.FindAll().FirstOrDefault();
            var nowUtc = DateTime.UtcNow;
            if (existing is null)
            {
                _state.Insert(new RxChainOrderEntry
                {
                    PluginIds = pluginIds.ToList(),
                    ParkedPluginIds = parkedIds.ToList(),
                    UpdatedUtc = nowUtc,
                });
            }
            else
            {
                existing.PluginIds = pluginIds.ToList();
                existing.ParkedPluginIds = parkedIds.ToList();
                existing.UpdatedUtc = nowUtc;
                _state.Update(existing);
            }
        }
    }

    public void Dispose() => _dbLease.Dispose();
}

public sealed class RxChainOrderEntry
{
    public int Id { get; set; }
    public List<string> PluginIds { get; set; } = new();
    public List<string> ParkedPluginIds { get; set; } = new();
    public DateTime UpdatedUtc { get; set; }
}
