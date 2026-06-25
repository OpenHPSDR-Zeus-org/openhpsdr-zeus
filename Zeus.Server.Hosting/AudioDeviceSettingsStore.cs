// SPDX-License-Identifier: GPL-2.0-or-later

using LiteDB;

namespace Zeus.Server;

public sealed record AudioDeviceSettings(string? InputDeviceId, string? OutputDeviceId);

public sealed class AudioDeviceSettingsStore : IDisposable
{
    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<AudioDeviceSettingsEntry> _docs;
    private readonly ILogger<AudioDeviceSettingsStore> _log;
    private readonly object _sync = new();

    public AudioDeviceSettingsStore(
        ILogger<AudioDeviceSettingsStore> log,
        string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _docs = _db.GetCollection<AudioDeviceSettingsEntry>("audio_device_settings");

        _log.LogInformation("AudioDeviceSettingsStore initialized at {Path}", dbPath);
    }

    public AudioDeviceSettings Get()
    {
        lock (_sync)
        {
            var e = _docs.FindAll().FirstOrDefault();
            return e is null
                ? new AudioDeviceSettings(InputDeviceId: null, OutputDeviceId: null)
                : new AudioDeviceSettings(
                    InputDeviceId: Normalize(e.InputDeviceId),
                    OutputDeviceId: Normalize(e.OutputDeviceId));
        }
    }

    public void SetInputDeviceId(string? inputDeviceId)
    {
        lock (_sync)
        {
            var e = GetOrCreateEntry();
            e.InputDeviceId = Normalize(inputDeviceId);
            SaveEntry(e);
        }
    }

    public void SetOutputDeviceId(string? outputDeviceId)
    {
        lock (_sync)
        {
            var e = GetOrCreateEntry();
            e.OutputDeviceId = Normalize(outputDeviceId);
            SaveEntry(e);
        }
    }

    public void Set(string? inputDeviceId, string? outputDeviceId)
    {
        lock (_sync)
        {
            var e = GetOrCreateEntry();
            e.InputDeviceId = Normalize(inputDeviceId);
            e.OutputDeviceId = Normalize(outputDeviceId);
            SaveEntry(e);
        }
    }

    public void Dispose() => _dbLease.Dispose();

    private AudioDeviceSettingsEntry GetOrCreateEntry() =>
        _docs.FindAll().FirstOrDefault() ?? new AudioDeviceSettingsEntry();

    private void SaveEntry(AudioDeviceSettingsEntry e)
    {
        e.UpdatedUtc = DateTime.UtcNow;
        if (e.Id == 0) _docs.Insert(e);
        else _docs.Update(e);
    }

    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}

public sealed class AudioDeviceSettingsEntry
{
    public int Id { get; set; }
    public string? InputDeviceId { get; set; }
    public string? OutputDeviceId { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
