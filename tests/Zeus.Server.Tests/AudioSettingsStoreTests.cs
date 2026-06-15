// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// External-ports plan, §2/§8 — global TX-audio SOURCE persistence round-trip +
// legacy four-bool row migration to Host.

using LiteDB;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class AudioSettingsStoreTests : IDisposable
{
    private readonly string _dbPath;

    public AudioSettingsStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zeus-prefs-audio-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private AudioSettingsStore NewStore() =>
        new AudioSettingsStore(NullLogger<AudioSettingsStore>.Instance, _dbPath);

    [Fact]
    public void Unset_Defaults_To_Host_NoBias()
    {
        using var store = NewStore();
        var sel = store.Get();
        Assert.Equal(TxAudioSource.Host, sel.Source);
        Assert.False(sel.MicBoost);
        Assert.False(sel.MicBias); // mic_bias defaults OFF
        Assert.Equal(0, sel.LineInGain);
    }

    [Fact]
    public void Set_RoundTrips_AcrossReopen()
    {
        using (var store = NewStore())
        {
            store.Set(new AudioSourceSelection(
                Source: TxAudioSource.RadioLineIn, MicBoost: true, MicBias: true, LineInGain: 21));
        }

        using var reopened = NewStore();
        var sel = reopened.Get();
        Assert.Equal(TxAudioSource.RadioLineIn, sel.Source);
        Assert.True(sel.MicBoost);
        Assert.True(sel.MicBias);
        Assert.Equal(21, sel.LineInGain);
    }

    [Fact]
    public void Set_Twice_Updates_NotDuplicates_DeleteManyInsert()
    {
        // The DeleteMany+Insert upsert must replace the single global row, not
        // accumulate rows (the LiteDB Id=0-always-inserts bug, PR #387).
        using var store = NewStore();
        store.Set(new AudioSourceSelection(TxAudioSource.RadioMic, false, false, 5));
        store.Set(new AudioSourceSelection(TxAudioSource.RadioBalancedXlr, true, true, 12));

        var sel = store.Get();
        Assert.Equal(TxAudioSource.RadioBalancedXlr, sel.Source);
        Assert.True(sel.MicBoost);
        Assert.True(sel.MicBias);
        Assert.Equal(12, sel.LineInGain);
    }

    [Fact]
    public void LineInGain_ClampedTo31()
    {
        using var store = NewStore();
        store.Set(new AudioSourceSelection(TxAudioSource.RadioLineIn, false, false, 99));
        Assert.Equal(31, store.Get().LineInGain);
    }

    [Fact]
    public void Changed_Fires_On_Set()
    {
        using var store = NewStore();
        int fired = 0;
        store.Changed += () => fired++;
        store.Set(AudioSourceSelection.Default with { MicBoost = true });
        Assert.Equal(1, fired);
    }

    [Fact]
    public void LegacyFourBoolRow_MigratesTo_Host()
    {
        // Write a pre-rework row directly into the audio_frontend collection with
        // the OLD four-bool schema (LineIn / MicBoost / MicBias / BalancedInput /
        // LineInGain) and NO `Source` field. The new store must hydrate Source
        // to Host (the schema-less LiteDB default 0) — the §8 migration contract.
        using (var db = new LiteDatabase($"Filename={_dbPath};Connection=shared"))
        {
            var col = db.GetCollection("audio_frontend");
            col.Insert(new BsonDocument
            {
                ["_id"] = 1,
                ["LineIn"] = true,
                ["MicBoost"] = true,
                ["MicBias"] = true,
                ["BalancedInput"] = true,
                ["LineInGain"] = 17,
                ["UpdatedUtc"] = DateTime.UtcNow,
            });
        }

        using var store = NewStore();
        var sel = store.Get();
        // No `Source` field on the legacy row → Host (the clean fallback).
        Assert.Equal(TxAudioSource.Host, sel.Source);
        // The carried-over params survive as raw values (clamped at push time),
        // but the SOURCE — the thing that actually drives the wire — is Host.
        Assert.True(sel.MicBoost);
        Assert.True(sel.MicBias);
        Assert.Equal(17, sel.LineInGain);
    }

    [Fact]
    public void CorruptSourceByte_FallsBackToHost()
    {
        using (var db = new LiteDatabase($"Filename={_dbPath};Connection=shared"))
        {
            var col = db.GetCollection("audio_frontend");
            col.Insert(new BsonDocument
            {
                ["_id"] = 1,
                ["Source"] = 99, // out of TxAudioSource range
                ["MicBoost"] = false,
                ["MicBias"] = false,
                ["LineInGain"] = 0,
            });
        }

        using var store = NewStore();
        Assert.Equal(TxAudioSource.Host, store.Get().Source);
    }
}
