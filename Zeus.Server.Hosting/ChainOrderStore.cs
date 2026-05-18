// SPDX-License-Identifier: GPL-2.0-or-later
using LiteDB;

namespace Zeus.Server;

/// <summary>
/// Persists the operator's preferred Audio Suite chain order across
/// server restarts. Single-row collection ("audio_chain_order")
/// sharing zeus-prefs.db with the other preference stores. Mirrors
/// the PsSettingsStore / RadioStateStore pattern: a typed POCO,
/// LiteDB upsert, no schema migrations because LiteDB is happy to
/// deserialise rows written by older builds with missing fields.
///
/// <para>Why a dedicated store rather than a field on
/// <see cref="RadioStateStore"/>: chain order is plugin-runtime state,
/// not RadioService state. The RadioStateStore comment explicitly
/// scopes itself to "RadioService state" with sister stores for
/// DSP / PA / PS / filter presets / band memory. Audio chain order
/// is its own concern — a flat list of plugin IDs — so it gets its
/// own store and stays out of the way of the radio state churn.</para>
/// </summary>
public sealed class ChainOrderStore : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<ChainOrderEntry> _state;
    private readonly ILogger<ChainOrderStore> _log;
    private readonly object _sync = new();

    public ChainOrderStore(ILogger<ChainOrderStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _state = _db.GetCollection<ChainOrderEntry>("audio_chain_order");

        _log.LogInformation("ChainOrderStore initialized at {Path}", dbPath);
    }

    /// <summary>
    /// Returns the persisted order, or null on first run (no row yet).
    /// Null is the "use default seed" signal — the caller (ChainOrderService)
    /// substitutes the v2 default order in that case.
    /// </summary>
    public IReadOnlyList<string>? GetOrder()
    {
        lock (_sync)
        {
            var entry = _state.FindAll().FirstOrDefault();
            return entry?.PluginIds;
        }
    }

    public void SetOrder(IReadOnlyList<string> pluginIds)
    {
        lock (_sync)
        {
            var existing = _state.FindAll().FirstOrDefault();
            var nowUtc = DateTime.UtcNow;
            if (existing is null)
            {
                _state.Insert(new ChainOrderEntry
                {
                    PluginIds = pluginIds.ToList(),
                    UpdatedUtc = nowUtc,
                });
            }
            else
            {
                existing.PluginIds = pluginIds.ToList();
                existing.UpdatedUtc = nowUtc;
                _state.Update(existing);
            }
        }
    }

    public void Dispose() => _db.Dispose();
}

public sealed class ChainOrderEntry
{
    public int Id { get; set; }
    public List<string> PluginIds { get; set; } = new();
    public DateTime UpdatedUtc { get; set; }
}
