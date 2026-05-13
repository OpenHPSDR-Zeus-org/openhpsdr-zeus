// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.

using LiteDB;

namespace Zeus.Server;

/// <summary>
/// Persists band-plan operator preferences: active region and TX guard override.
/// Shares zeus-prefs.db with other stores.
/// </summary>
public sealed class BandPrefsStore : IDisposable
{
    private const string DefaultRegionId = "IARU_R1";
    private const string PrefsKey = "default";

    private readonly LiteDatabase _db;
    private readonly ILiteCollection<BandPrefsEntry> _coll;

    public BandPrefsStore()
    {
        var dbPath = PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _coll = _db.GetCollection<BandPrefsEntry>("band_plan_prefs");
        _coll.EnsureIndex(x => x.Key, unique: true);
    }

    public string GetRegionId()
    {
        var e = _coll.FindOne(x => x.Key == PrefsKey);
        return e?.RegionId ?? DefaultRegionId;
    }

    public void SetRegionId(string regionId)
    {
        var e = _coll.FindOne(x => x.Key == PrefsKey);
        if (e is null)
            _coll.Insert(new BandPrefsEntry { Key = PrefsKey, RegionId = regionId, TxGuardIgnore = false });
        else
        {
            e.RegionId = regionId;
            _coll.Update(e);
        }
    }

    public bool GetTxGuardIgnore()
    {
        var e = _coll.FindOne(x => x.Key == PrefsKey);
        return e?.TxGuardIgnore ?? false;
    }

    public void SetTxGuardIgnore(bool ignore)
    {
        var e = _coll.FindOne(x => x.Key == PrefsKey);
        if (e is null)
            _coll.Insert(new BandPrefsEntry { Key = PrefsKey, RegionId = DefaultRegionId, TxGuardIgnore = ignore });
        else
        {
            e.TxGuardIgnore = ignore;
            _coll.Update(e);
        }
    }

    public void Dispose() => _db.Dispose();

}

public sealed class BandPrefsEntry
{
    public int Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string RegionId { get; set; } = "IARU_R1";
    public bool TxGuardIgnore { get; set; }
}
