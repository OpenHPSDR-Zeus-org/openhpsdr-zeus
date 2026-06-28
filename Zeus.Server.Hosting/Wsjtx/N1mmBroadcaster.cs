// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Net.Sockets;
using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server.Wsjtx;

/// <summary>
/// Sends the N1MM Logger+ "contactinfo" datagram (see
/// <see cref="N1mmContactInfoEncoder"/>) to a logger that listens for the N1MM
/// external-broadcast format — HRD Logbook "QSO Forwarding" or DXKeeper via the
/// N1MM→DXKeeper Gateway. SEPARATE from the WSJT-X type-12 broadcaster: a
/// different wire format on its own configurable port (default 2333).
///
/// SEND-ONLY, no listener; no-op when disabled (the default). Pure
/// <see cref="UdpClient"/>, cross-platform, no native deps. Mirrors
/// <see cref="WsjtxUdpBroadcaster"/>: one cached send socket, sends serialised
/// through a gate, never throws to the caller (a send failure must not break the
/// log POST).
/// </summary>
public sealed class N1mmBroadcaster : IDisposable
{
    private readonly ILogger<N1mmBroadcaster> _log;
    private readonly N1mmConfigStore _store;
    private readonly SpottingManagementService? _operator;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly object _sync = new();
    private N1mmConfig _config;
    private UdpClient? _udp;
    private bool _disposed;

    public N1mmBroadcaster(
        ILogger<N1mmBroadcaster> log,
        N1mmConfigStore store,
        SpottingManagementService? operatorIdentity = null)
    {
        _log = log;
        _store = store;
        _operator = operatorIdentity;
        _config = _store.Get() ?? new N1mmConfig();
    }

    public N1mmConfig GetConfig()
    {
        lock (_sync) return _config;
    }

    public N1mmConfig SetConfig(N1mmConfig config)
    {
        var normalized = new N1mmConfig(
            Enabled: config.Enabled,
            Host: string.IsNullOrWhiteSpace(config.Host) ? "127.0.0.1" : config.Host.Trim(),
            Port: config.Port is > 0 and < 65536 ? config.Port : 2333);

        lock (_sync) _config = normalized;
        try { _store.Set(normalized); }
        catch (Exception ex) { _log.LogWarning(ex, "n1mm.config.persist failed"); }

        _log.LogInformation(
            "n1mm.config.updated enabled={Enabled} host={Host} port={Port}",
            normalized.Enabled, normalized.Host, normalized.Port);
        return normalized;
    }

    /// <summary>Broadcast one logged QSO as an N1MM contactinfo datagram. No-op
    /// when disabled; never throws.</summary>
    public async Task BroadcastLoggedQsoAsync(LogEntry entry, CancellationToken ct = default)
    {
        var cfg = GetConfig();
        if (!cfg.Enabled) return;

        try
        {
            var (myCall, _) = _operator?.ResolveOperator() ?? ("", "");
            var datagram = N1mmContactInfoEncoder.Encode(entry, myCall);
            await SendAsync(cfg, datagram, ct).ConfigureAwait(false);
            _log.LogInformation(
                "n1mm.broadcast call={Call} -> {Host}:{Port} bytes={Bytes}",
                entry.Callsign, cfg.Host, cfg.Port, datagram.Length);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "n1mm.broadcast failed call={Call}", entry.Callsign);
        }
    }

    private async Task SendAsync(N1mmConfig cfg, byte[] datagram, CancellationToken ct)
    {
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var udp = _udp ??= new UdpClient();
            await udp.SendAsync(datagram, datagram.Length, cfg.Host, cfg.Port)
                .WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _udp?.Dispose();
        _sendGate.Dispose();
    }
}

// Server-side config (NOT Zeus.Contracts — never crosses the hub wire).
public sealed record N1mmConfig(
    bool Enabled = false,
    string Host = "127.0.0.1",
    int Port = 2333);

// Single-row LiteDB collection. Shares the single LiteDatabase via
// SharedLiteDatabase.Acquire + Directory guard as CatConfigStore does (one engine
// per prefs file avoids the Windows exclusive-lock IOException a second handle
// hits, and the Linux LiteDB shared-mode caveat, GH #682).
public sealed class N1mmConfigStore : IDisposable
{
    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<N1mmConfigEntry> _entries;
    private readonly object _sync = new();

    public N1mmConfigStore(ILogger<N1mmConfigStore> log, string? dbPathOverride = null)
    {
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _entries = _db.GetCollection<N1mmConfigEntry>("n1mm_config");
        log.LogInformation("N1mmConfigStore initialized at {Path}", dbPath);
    }

    public N1mmConfig? Get()
    {
        lock (_sync)
        {
            var e = _entries.FindAll().FirstOrDefault();
            if (e is null) return null;
            return new N1mmConfig(
                Enabled: e.Enabled,
                Host: string.IsNullOrWhiteSpace(e.Host) ? "127.0.0.1" : e.Host,
                Port: e.Port is > 0 and < 65536 ? e.Port : 2333);
        }
    }

    public void Set(N1mmConfig config)
    {
        lock (_sync)
        {
            var existing = _entries.FindAll().FirstOrDefault() ?? new N1mmConfigEntry();
            existing.Enabled = config.Enabled;
            existing.Host = config.Host;
            existing.Port = config.Port;
            existing.UpdatedUtc = DateTime.UtcNow;
            if (existing.Id == 0) _entries.Insert(existing);
            else _entries.Update(existing);
        }
    }

    public void Dispose() => _dbLease.Dispose();
}

public sealed class N1mmConfigEntry
{
    public int Id { get; set; }
    public bool Enabled { get; set; }
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 2333;
    public DateTime UpdatedUtc { get; set; }
}
