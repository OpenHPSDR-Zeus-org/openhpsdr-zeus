// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using LiteDB;

namespace Zeus.Server.CloudLog;

// Non-secret config for the per-QSO HTTP cloud-log uploaders (Wavelog/Cloudlog
// and Club Log realtime). These records live in Zeus.Server.Hosting (NOT
// Zeus.Contracts) on purpose — they never cross the SignalR hub / StateDto wire,
// only the /api/log/cloud/* REST endpoints and the frontend store consume them
// as JSON. Keeping them out of Contracts avoids a red-light wire-format change.
//
// SECRETS DO NOT LIVE HERE. The Wavelog API key, Club Log application password
// and Club Log API key go in the plaintext CredentialStore (service names
// "wavelog-apikey" / "clublog-password" / "clublog-apikey"), exactly like the
// QRZ key. This store holds only the non-secret, persistable settings.

public sealed record CloudLogConfig(
    WavelogConfig Wavelog,
    ClubLogConfig ClubLog)
{
    public CloudLogConfig() : this(new WavelogConfig(), new ClubLogConfig()) { }
}

// Enabled defaults to false — new network egress is opt-in.
public sealed record WavelogConfig(
    bool Enabled = false,
    string BaseUrl = "",
    string StationProfileId = "");

public sealed record ClubLogConfig(
    bool Enabled = false,
    string Email = "",
    string Callsign = "");

// Single-row LiteDB collection. Mirrors WsjtxConfigStore — same Connection=shared
// pattern + Directory guard (no new exposure to the Linux LiteDB shared-mode
// caveat, GH #682). Returns null when nothing is persisted so the caller falls
// back to the default (egress OFF) config.
public sealed class CloudLogConfigStore : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<CloudLogConfigEntry> _entries;
    private readonly ILogger<CloudLogConfigStore> _log;
    private readonly object _sync = new();

    public CloudLogConfigStore(ILogger<CloudLogConfigStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _entries = _db.GetCollection<CloudLogConfigEntry>("cloudlog_config");

        _log.LogInformation("CloudLogConfigStore initialized at {Path}", dbPath);
    }

    public CloudLogConfig? Get()
    {
        lock (_sync)
        {
            var e = _entries.FindAll().FirstOrDefault();
            if (e is null) return null;
            return new CloudLogConfig(
                new WavelogConfig(
                    Enabled: e.WavelogEnabled,
                    BaseUrl: e.WavelogBaseUrl ?? "",
                    StationProfileId: e.WavelogStationProfileId ?? ""),
                new ClubLogConfig(
                    Enabled: e.ClubLogEnabled,
                    Email: e.ClubLogEmail ?? "",
                    Callsign: e.ClubLogCallsign ?? ""));
        }
    }

    public void Set(CloudLogConfig config)
    {
        lock (_sync)
        {
            var existing = _entries.FindAll().FirstOrDefault() ?? new CloudLogConfigEntry();
            existing.WavelogEnabled = config.Wavelog.Enabled;
            existing.WavelogBaseUrl = config.Wavelog.BaseUrl;
            existing.WavelogStationProfileId = config.Wavelog.StationProfileId;
            existing.ClubLogEnabled = config.ClubLog.Enabled;
            existing.ClubLogEmail = config.ClubLog.Email;
            existing.ClubLogCallsign = config.ClubLog.Callsign;
            existing.UpdatedUtc = DateTime.UtcNow;
            if (existing.Id == 0) _entries.Insert(existing);
            else _entries.Update(existing);
        }
    }

    public void Dispose() => _db.Dispose();
}

public sealed class CloudLogConfigEntry
{
    public int Id { get; set; }
    public bool WavelogEnabled { get; set; }
    public string? WavelogBaseUrl { get; set; }
    public string? WavelogStationProfileId { get; set; }
    public bool ClubLogEnabled { get; set; }
    public string? ClubLogEmail { get; set; }
    public string? ClubLogCallsign { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
