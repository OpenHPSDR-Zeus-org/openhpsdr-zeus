// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// RF2K-S amplifier integration. Polls the amp's REST API on TCP/8080 when
// enabled, exposes a snapshot via GetStatus() and async commands for
// standby/operate, antenna selection, operational-interface mode, fault
// reset, and (via Rf2kVncClient) Tune/Bypass click injection.
//
// Shape mirrors RotctldService — singleton BackgroundService, in-memory
// status, persisted config via Rf2kSettingsStore.

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Zeus.Contracts;

namespace Zeus.Server;

public sealed class Rf2kService : BackgroundService
{
    private static readonly TimeSpan ReconnectBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    // Allow this many consecutive poll-cycle failures before we flip Connected
    // to false. Each cycle hits 8 sequential HTTP endpoints; if any one of them
    // blips (a 3 s timeout, transient RST, momentary 5xx from the amp's embedded
    // web server, etc.) the entire cycle fails. Flipping Connected on a single
    // blip causes a panel-visible flap every 5 s (the ReconnectBackoff window)
    // even though the next cycle reconnects cleanly. A small tolerance lets us
    // ride out transient blips while still detecting a real disconnect after
    // ~3 × PollingIntervalMs of sustained failure.
    private const int ConsecutiveFailuresBeforeDisconnect = 3;

    /// <summary>Match the firmware's snake_case field naming.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogger<Rf2kService> _log;
    private readonly Rf2kSettingsStore _store;
    private readonly Rf2kVncClient _vnc;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly SemaphoreSlim _configChanged = new(0, 1);

    private volatile Rf2kConfig _config = new();

    // Snapshot fields: written under _state, read lock-free volatile when possible.
    private readonly object _state = new();
    private bool _connected;
    private int _consecutiveFailures;
    private string? _error;
    private DateTimeOffset? _lastSampleUtc;
    private Rf2kInfo? _info;
    private Rf2kData? _data;
    private Rf2kPower? _power;
    private Rf2kTuner? _tuner;
    private string? _operateMode;
    private string? _operationalInterface;
    private string? _operationalInterfaceError;
    private Rf2kActiveAntenna? _activeAntenna;
    private IReadOnlyList<Rf2kAntenna>? _antennas;

    public Rf2kService(ILogger<Rf2kService> log, Rf2kSettingsStore store, Rf2kVncClient vnc)
    {
        _log = log;
        _store = store;
        _vnc = vnc;
        _http = new HttpClient { Timeout = RequestTimeout };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // Hydrate config from persistence (or fall back to record defaults).
        var persisted = _store.Get();
        if (persisted is not null) _config = persisted;
    }

    // ------------------------------------------------------------------------
    //  Public API used by ZeusEndpoints
    // ------------------------------------------------------------------------

    public Rf2kStatus GetStatus()
    {
        var cfg = _config;
        lock (_state)
        {
            return new Rf2kStatus(
                Enabled: cfg.Enabled,
                Connected: _connected,
                Host: cfg.Host,
                Port: cfg.Port,
                Info: _info,
                Data: _data,
                Power: _power,
                Tuner: _tuner,
                OperateMode: _operateMode,
                OperationalInterface: _operationalInterface,
                OperationalInterfaceError: _operationalInterfaceError,
                ActiveAntenna: _activeAntenna,
                Antennas: _antennas,
                Error: _error,
                LastSampleUtc: _lastSampleUtc);
        }
    }

    public Rf2kConfig GetConfig() => _config;

    public async Task<Rf2kStatus> SetConfigAsync(Rf2kConfig next, CancellationToken ct)
    {
        await _io.WaitAsync(ct);
        try
        {
            var sanitized = next with
            {
                Host = string.IsNullOrWhiteSpace(next.Host) ? "10.70.120.41" : next.Host.Trim(),
                Port = next.Port is > 0 and < 65536 ? next.Port : 8080,
                VncPort = next.VncPort is > 0 and < 65536 ? next.VncPort : 5900,
                VncPassword = next.VncPassword ?? string.Empty,
                PollingIntervalMs = Math.Clamp(next.PollingIntervalMs, 250, 10_000),
                TuneClickX = next.TuneClickX is >= 0 and <= 65535 ? next.TuneClickX : 0,
                TuneClickY = next.TuneClickY is >= 0 and <= 65535 ? next.TuneClickY : 0,
                BypassClickX = next.BypassClickX is >= 0 and <= 65535 ? next.BypassClickX : 0,
                BypassClickY = next.BypassClickY is >= 0 and <= 65535 ? next.BypassClickY : 0,
            };
            _config = sanitized;
            _store.Set(sanitized);

            // Clear stale snapshot on host change so the panel doesn't show
            // last-known-good values from a different amp.
            ClearSnapshotLocked();

            if (_configChanged.CurrentCount == 0) _configChanged.Release();
        }
        finally
        {
            _io.Release();
        }
        return GetStatus();
    }

    public async Task<Rf2kStatus> SetOperateModeAsync(string mode, CancellationToken ct)
    {
        var normalized = string.Equals(mode, "OPERATE", StringComparison.OrdinalIgnoreCase) ? "OPERATE" : "STANDBY";
        var body = new { operate_mode = normalized };
        var ok = await PutJsonAsync("/operate-mode", body, ct);
        if (ok) await PollOnceAsync(ct);
        return GetStatus();
    }

    public async Task<Rf2kStatus> SetOperationalInterfaceAsync(string iface, CancellationToken ct)
    {
        var normalized = iface?.Trim().ToUpperInvariant() switch
        {
            "UNIV" or "CAT" or "UDP" or "TCI" => iface!.Trim().ToUpperInvariant(),
            _ => "UNIV"
        };
        var body = new { operational_interface = normalized };
        var ok = await PutJsonAsync("/operational-interface", body, ct);
        if (ok) await PollOnceAsync(ct);
        return GetStatus();
    }

    public async Task<Rf2kStatus> SetActiveAntennaAsync(string type, int? number, CancellationToken ct)
    {
        var normalizedType = string.Equals(type, "EXTERNAL", StringComparison.OrdinalIgnoreCase) ? "EXTERNAL" : "INTERNAL";
        object body = normalizedType == "EXTERNAL"
            ? new { type = "EXTERNAL" }
            : new { type = "INTERNAL", number = number ?? 1 };
        var ok = await PutJsonAsync("/antennas/active", body, ct);
        if (ok) await PollOnceAsync(ct);
        return GetStatus();
    }

    public async Task<Rf2kStatus> ResetErrorAsync(CancellationToken ct)
    {
        var ok = await PostNoBodyAsync("/error/reset", ct);
        if (ok) await PollOnceAsync(ct);
        return GetStatus();
    }

    public async Task<Rf2kTestResult> TestAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var client = new HttpClient { Timeout = RequestTimeout };
            var url = $"http://{host}:{port}/info";
            using var resp = await client.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                return new Rf2kTestResult(false, $"HTTP {(int)resp.StatusCode}");
            var info = await resp.Content.ReadFromJsonAsync<Rf2kInfo>(Json, ct);
            if (info?.Device is null) return new Rf2kTestResult(false, "Bad payload");
            return new Rf2kTestResult(true, null);
        }
        catch (Exception ex)
        {
            return new Rf2kTestResult(false, ex.Message);
        }
    }

    public async Task<Rf2kTestResult> SendTuneClickAsync(CancellationToken ct)
    {
        var cfg = _config;
        if (cfg.TuneClickX <= 0 && cfg.TuneClickY <= 0)
            return new Rf2kTestResult(false, "Tune click coordinates not configured. Calibrate from the panel settings.");
        var err = await _vnc.SendClickAsync(cfg.Host, cfg.VncPort, (ushort)cfg.TuneClickX, (ushort)cfg.TuneClickY, cfg.VncPassword, ct);
        return new Rf2kTestResult(err is null, err);
    }

    public async Task<Rf2kTestResult> SendBypassClickAsync(CancellationToken ct)
    {
        var cfg = _config;
        if (cfg.BypassClickX <= 0 && cfg.BypassClickY <= 0)
            return new Rf2kTestResult(false, "Bypass click coordinates not configured. Calibrate from the panel settings.");
        var err = await _vnc.SendClickAsync(cfg.Host, cfg.VncPort, (ushort)cfg.BypassClickX, (ushort)cfg.BypassClickY, cfg.VncPassword, ct);
        return new Rf2kTestResult(err is null, err);
    }

    /// <summary>Send a click at arbitrary X/Y. Used by the panel's calibration workflow.</summary>
    public async Task<Rf2kTestResult> SendTestClickAsync(int x, int y, CancellationToken ct)
    {
        if (x is < 0 or > 65535 || y is < 0 or > 65535)
            return new Rf2kTestResult(false, "Coordinates must be in 0..65535");
        var cfg = _config;
        var err = await _vnc.SendClickAsync(cfg.Host, cfg.VncPort, (ushort)x, (ushort)y, cfg.VncPassword, ct);
        return new Rf2kTestResult(err is null, err);
    }

    // ------------------------------------------------------------------------
    //  Background poll loop
    // ------------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var cfg = _config;
            if (!cfg.Enabled)
            {
                lock (_state)
                {
                    if (_connected)
                    {
                        _connected = false;
                        ClearSnapshotLocked();
                    }
                }
                try { await _configChanged.WaitAsync(stoppingToken); } catch (OperationCanceledException) { return; }
                continue;
            }

            var ok = await PollOnceAsync(stoppingToken);
            if (!ok)
            {
                // Back off on failure, but wake early on config change.
                var delayTask = Task.Delay(ReconnectBackoff, stoppingToken);
                var configTask = _configChanged.WaitAsync(stoppingToken);
                await Task.WhenAny(delayTask, configTask);
                continue;
            }

            try { await Task.Delay(cfg.PollingIntervalMs, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task<bool> PollOnceAsync(CancellationToken ct)
    {
        var cfg = _config;
        if (!cfg.Enabled) return false;
        try
        {
            var info = await GetJsonAsync<Rf2kInfo>("/info", ct);
            var data = await GetJsonAsync<Rf2kData>("/data", ct);
            var power = await GetJsonAsync<Rf2kPower>("/power", ct);
            var tuner = await GetJsonAsync<Rf2kTuner>("/tuner", ct);
            var operate = await GetJsonAsync<Rf2kOperateMode>("/operate-mode", ct);
            var iface = await GetJsonAsync<Rf2kOperationalInterface>("/operational-interface", ct);
            var antennas = await GetJsonAsync<Rf2kAntennaList>("/antennas", ct);
            var active = await GetJsonAsync<Rf2kActiveAntenna>("/antennas/active", ct);

            int priorFailures;
            bool wasDisconnected;
            lock (_state)
            {
                _info = info;
                _data = data;
                _power = power;
                _tuner = tuner;
                _operateMode = operate?.OperateMode;
                _operationalInterface = iface?.OperationalInterface;
                _operationalInterfaceError = iface?.Error;
                _antennas = antennas?.Antennas;
                _activeAntenna = active;
                priorFailures = _consecutiveFailures;
                wasDisconnected = !_connected;
                _connected = true;
                _consecutiveFailures = 0;
                _error = null;
                _lastSampleUtc = DateTimeOffset.UtcNow;
            }
            // Log a recovery line if we were silently retrying through transient
            // blips — useful for distinguishing "amp flaky" from "amp solid".
            if (priorFailures > 0)
            {
                _log.LogInformation(
                    "rf2k.poll.recovered after={Failures} failed polls host={Host} wasDisconnected={Was}",
                    priorFailures, cfg.Host, wasDisconnected);
            }
            return true;
        }
        catch (Exception ex)
        {
            int failures;
            bool flippedDisconnected;
            lock (_state)
            {
                _consecutiveFailures++;
                failures = _consecutiveFailures;
                _error = ex.Message;
                // Only flip Connected after the threshold to absorb transient
                // single-poll blips. Snapshot fields are NOT cleared here so the
                // UI keeps showing last-known values during the suppress window
                // rather than blanking on every blip.
                if (_connected && failures >= ConsecutiveFailuresBeforeDisconnect)
                {
                    _connected = false;
                    flippedDisconnected = true;
                }
                else
                {
                    flippedDisconnected = false;
                }
            }
            // Log every failure so the flapping endpoint surfaces in the log.
            // Warning level (not Error) — most blips are recoverable; the
            // flippedDisconnected=true case is the one worth attention.
            _log.LogWarning(ex,
                "rf2k.poll.failed consecutive={Count}/{Threshold} host={Host} flippedDisconnected={Flipped}",
                failures, ConsecutiveFailuresBeforeDisconnect, cfg.Host, flippedDisconnected);
            return false;
        }
    }

    private async Task<T?> GetJsonAsync<T>(string path, CancellationToken ct) where T : class
    {
        var cfg = _config;
        var url = $"http://{cfg.Host}:{cfg.Port}{path}";
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<T>(Json, ct);
    }

    private async Task<bool> PutJsonAsync(string path, object body, CancellationToken ct)
    {
        var cfg = _config;
        var url = $"http://{cfg.Host}:{cfg.Port}{path}";
        try
        {
            using var resp = await _http.PutAsJsonAsync(url, body, Json, ct);
            if (!resp.IsSuccessStatusCode)
            {
                lock (_state) { _error = $"PUT {path} → HTTP {(int)resp.StatusCode}"; }
                return false;
            }
            lock (_state) { _error = null; }
            return true;
        }
        catch (Exception ex)
        {
            lock (_state) { _error = $"PUT {path}: {ex.Message}"; }
            return false;
        }
    }

    private async Task<bool> PostNoBodyAsync(string path, CancellationToken ct)
    {
        var cfg = _config;
        var url = $"http://{cfg.Host}:{cfg.Port}{path}";
        try
        {
            using var resp = await _http.PostAsync(url, content: null, ct);
            if (!resp.IsSuccessStatusCode)
            {
                lock (_state) { _error = $"POST {path} → HTTP {(int)resp.StatusCode}"; }
                return false;
            }
            lock (_state) { _error = null; }
            return true;
        }
        catch (Exception ex)
        {
            lock (_state) { _error = $"POST {path}: {ex.Message}"; }
            return false;
        }
    }

    private void ClearSnapshotLocked()
    {
        _info = null; _data = null; _power = null; _tuner = null;
        _operateMode = null; _operationalInterface = null; _operationalInterfaceError = null;
        _antennas = null; _activeAntenna = null;
        _lastSampleUtc = null;
        // Reset failure tolerance — count from the previous host should not
        // carry into a new host/port configuration.
        _consecutiveFailures = 0;
    }

    public override void Dispose()
    {
        _http.Dispose();
        _io.Dispose();
        _configChanged.Dispose();
        base.Dispose();
    }
}
