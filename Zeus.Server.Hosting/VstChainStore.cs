// SPDX-License-Identifier: GPL-2.0-or-later
//
// VstChainStore — LiteDB-backed persistence for the VST plugin chain.
//
// Single-document collection ("vst_chain", _id=1) holds the master enable,
// per-slot plugin path / bypass / parameter cache, and operator-supplied
// custom search paths. Shape mirrors `Zeus.PluginHost.Chain.ChainSlot`
// closely enough that the hosted service can replay the chain into
// PluginHostManager on startup with a 1:1 mapping.
//
// We do not depend on Zeus.PluginHost from Zeus.Server.Hosting (the host
// project lives there) — instead the store exposes a plain DTO and the
// hosted service in Zeus.Server.Hosting translates between the DTO and
// PluginHostManager calls. Keeps Zeus.PluginHost free of LiteDB.

using System.Collections.Generic;
using LiteDB;

namespace Zeus.Server;

/// <summary>One persisted slot. Plugin path null = empty slot.</summary>
public sealed class VstChainSlotEntry
{
    public int Index { get; set; }
    public string? PluginPath { get; set; }
    public bool Bypass { get; set; }
    /// <summary>
    /// Parameter cache keyed by stringified VST3 parameter id. Stored as
    /// strings because BSON can't key dictionaries on uint32. The value is
    /// the latest normalized [0,1] value the plugin acknowledged via
    /// SlotSetParamResult.ActualValue.
    /// </summary>
    public Dictionary<string, double> Parameters { get; set; } = new();
}

/// <summary>
/// Top-level persisted document for the VST chain. <see cref="SchemaVersion"/>
/// is bumped on a non-backwards-compatible change so the store can migrate
/// or reset. Today everything is v1.
/// </summary>
public sealed class VstChainEntry
{
    public int Id { get; set; } = 1;
    public int SchemaVersion { get; set; } = 1;
    public bool MasterEnabled { get; set; }
    public List<VstChainSlotEntry> Slots { get; set; } = new();
    public List<string> CustomSearchPaths { get; set; } = new();
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>
/// Persistence boundary for the VST chain. Wave 6a ships LiteDB-backed; the
/// interface lets unit tests sub a fake without spinning up a real DB. The
/// hosted service auto-saves on every meaningful state change (load,
/// unload, bypass, master toggle, parameter set, custom path edits).
/// </summary>
public interface IVstChainPersistence
{
    /// <summary>Load the persisted chain document, or a fresh one with 8
    /// empty slots if nothing has ever been written.</summary>
    VstChainEntry Load();

    /// <summary>Replace the persisted document atomically.</summary>
    void Save(VstChainEntry entry);
}

/// <summary>
/// LiteDB-backed concrete persistence. Shares <c>zeus-prefs.db</c> with
/// the other settings stores; the chain config isn't sensitive.
/// </summary>
public sealed class LiteDbVstChainPersistence : IVstChainPersistence, IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<VstChainEntry> _entries;
    private readonly ILogger<LiteDbVstChainPersistence> _log;

    public LiteDbVstChainPersistence(
        ILogger<LiteDbVstChainPersistence> log,
        string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _entries = _db.GetCollection<VstChainEntry>("vst_chain");

        _log.LogInformation("VstChainStore initialized at {Path}", dbPath);
    }

    public VstChainEntry Load()
    {
        var existing = _entries.FindById(1);
        if (existing != null)
        {
            // Backfill missing slots if a future schema version writes
            // fewer; today we always write 8.
            while (existing.Slots.Count < 8)
            {
                existing.Slots.Add(new VstChainSlotEntry { Index = existing.Slots.Count });
            }
            return existing;
        }

        var fresh = new VstChainEntry
        {
            Id = 1,
            SchemaVersion = 1,
            MasterEnabled = false,
            Slots = new List<VstChainSlotEntry>(8),
            CustomSearchPaths = new List<string>(),
            UpdatedUtc = DateTime.UtcNow,
        };
        for (int i = 0; i < 8; i++)
        {
            fresh.Slots.Add(new VstChainSlotEntry { Index = i });
        }
        return fresh;
    }

    public void Save(VstChainEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        entry.Id = 1; // single-document collection
        entry.UpdatedUtc = DateTime.UtcNow;
        _entries.Upsert(entry);
    }

    public void Dispose() => _db.Dispose();

}
