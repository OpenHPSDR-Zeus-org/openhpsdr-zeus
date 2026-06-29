// SPDX-License-Identifier: GPL-2.0-or-later
using LiteDB;

namespace Zeus.Server;

/// <summary>
/// Persists the operator's ZeusChat opt-in across restarts. Chat is OFF by
/// default; the operator must explicitly enable it (it relays presence and
/// callsign to a public Cloudflare service, so it stays opt-in). A dedicated
/// single-row collection ("chat_enabled") sharing zeus-prefs.db, mirroring
/// <see cref="AudioProcessingModeStore"/>.
///
/// <para>First-run semantics: <see cref="GetEnabled"/> returns <c>false</c>
/// when no row exists, so a brand-new operator stays off the relay until they
/// opt in.</para>
/// </summary>
public sealed class ChatEnabledStore : IDisposable
{
    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<ChatEnabledEntry> _state;
    private readonly ILogger<ChatEnabledStore> _log;
    private readonly object _sync = new();

    public ChatEnabledStore(ILogger<ChatEnabledStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _state = _db.GetCollection<ChatEnabledEntry>("chat_enabled");

        _log.LogInformation("ChatEnabledStore initialized at {Path}", dbPath);
    }

    /// <summary>Persisted opt-in, defaulting to false on first run (no row yet).</summary>
    public bool GetEnabled()
    {
        lock (_sync)
        {
            var entry = _state.FindAll().FirstOrDefault();
            return entry?.Enabled ?? false;
        }
    }

    public void SetEnabled(bool enabled)
    {
        lock (_sync)
        {
            var existing = _state.FindAll().FirstOrDefault();
            var nowUtc = DateTime.UtcNow;
            if (existing is null)
            {
                _state.Insert(new ChatEnabledEntry { Enabled = enabled, UpdatedUtc = nowUtc });
            }
            else
            {
                existing.Enabled = enabled;
                existing.UpdatedUtc = nowUtc;
                _state.Update(existing);
            }
        }
    }

    /// <summary>Whether the operator's frequency may be shared (eye toggle).
    /// Defaults to true (visible to friends) when unset.</summary>
    public bool GetFreqPublic()
    {
        lock (_sync)
        {
            var entry = _state.FindAll().FirstOrDefault();
            return entry?.FreqPublic ?? true;
        }
    }

    public void SetFreqPublic(bool freqPublic)
    {
        lock (_sync)
        {
            var existing = _state.FindAll().FirstOrDefault();
            var nowUtc = DateTime.UtcNow;
            if (existing is null)
            {
                _state.Insert(new ChatEnabledEntry
                {
                    Enabled = false,
                    FreqPublic = freqPublic,
                    UpdatedUtc = nowUtc,
                });
            }
            else
            {
                existing.FreqPublic = freqPublic;
                existing.UpdatedUtc = nowUtc;
                _state.Update(existing);
            }
        }
    }

    public void Dispose() => _dbLease.Dispose();
}

public sealed class ChatEnabledEntry
{
    public int Id { get; set; }
    public bool Enabled { get; set; }
    // Nullable so rows written before this field default to "visible" (null → true)
    // rather than LiteDB's bool default of false, which would silently hide freq.
    public bool? FreqPublic { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
