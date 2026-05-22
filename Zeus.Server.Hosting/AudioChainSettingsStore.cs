// SPDX-License-Identifier: GPL-2.0-or-later
using LiteDB;

namespace Zeus.Server;

/// <summary>
/// Persists chain-level Audio Suite settings across server restarts.
/// Currently one field: the operator's master-bypass state. Single-row
/// collection ("audio_chain_settings") sharing zeus-prefs.db with the
/// other preference stores. Mirrors the ChainOrderStore pattern.
///
/// <para>Why a dedicated store rather than a field on
/// <see cref="ChainOrderStore"/>: chain order and chain master bypass
/// are different concerns. Order is "which plugins in what sequence";
/// master bypass is "should the entire chain run". Folding them into
/// one row creates an unnecessary tight coupling and makes diff /
/// migration harder if either grows new fields.</para>
///
/// <para>First-run semantics: <see cref="GetMasterBypassed"/> returns
/// <c>null</c> on first run (no row yet). The caller
/// (<c>AudioChainMasterBypassService</c>) interprets null as
/// "default to true (bypassed)" so a brand-new operator's chain is
/// inert until they engage it.</para>
/// </summary>
public sealed class AudioChainSettingsStore : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<AudioChainSettingsEntry> _state;
    private readonly ILogger<AudioChainSettingsStore> _log;
    private readonly object _sync = new();

    public AudioChainSettingsStore(ILogger<AudioChainSettingsStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _state = _db.GetCollection<AudioChainSettingsEntry>("audio_chain_settings");

        _log.LogInformation("AudioChainSettingsStore initialized at {Path}", dbPath);
    }

    /// <summary>
    /// Returns the persisted master-bypass state, or null on first run
    /// (no row yet). Null is the "use default" signal — the caller
    /// (<c>AudioChainMasterBypassService</c>) substitutes <c>true</c>
    /// so first-time operators get an inert chain.
    /// </summary>
    public bool? GetMasterBypassed()
    {
        lock (_sync)
        {
            var entry = _state.FindAll().FirstOrDefault();
            return entry?.MasterBypassed;
        }
    }

    public void SetMasterBypassed(bool bypassed)
    {
        lock (_sync)
        {
            var existing = _state.FindAll().FirstOrDefault();
            var nowUtc = DateTime.UtcNow;
            if (existing is null)
            {
                _state.Insert(new AudioChainSettingsEntry
                {
                    MasterBypassed = bypassed,
                    UpdatedUtc = nowUtc,
                });
            }
            else
            {
                existing.MasterBypassed = bypassed;
                existing.UpdatedUtc = nowUtc;
                _state.Update(existing);
            }
        }
    }

    public void Dispose() => _db.Dispose();
}

public sealed class AudioChainSettingsEntry
{
    public int Id { get; set; }
    public bool MasterBypassed { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
