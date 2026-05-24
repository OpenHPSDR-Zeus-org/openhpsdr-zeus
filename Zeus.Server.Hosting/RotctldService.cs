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
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Globalization;
using System.Net.Sockets;
using System.Text;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Persistent TCP client for hamlib's rotctld. Holds a single socket,
/// sends commands, polls position at <see cref="RotctldConfig.PollingIntervalMs"/>,
/// and reconnects with 5-second backoff on failure. Shape mirrors Log4YM's
/// RotatorService. Config (host / port / enabled flag / polling interval) is
/// persisted by <see cref="RotctldConfigStore"/> in zeus-prefs.db and hydrated
/// at construction so a clean exit with Enabled=true reconnects automatically
/// on next startup. Live socket state (current azimuth, target, error) is
/// in-memory only.
/// </summary>
public sealed class RotctldService : BackgroundService
{
    private const int MovingEpsilonDeg = 1;
    private static readonly TimeSpan ReconnectBackoff = TimeSpan.FromSeconds(5);

    private readonly ILogger<RotctldService> _log;
    private readonly RotctldConfigStore _store;
    private readonly SemaphoreSlim _io = new(1, 1);

    // Serialised by _io for connection state, lock-free volatile reads for status snapshot.
    private TcpClient? _client;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    private volatile RotctldConfig _config = new();
    private volatile bool _connected;
    private volatile string? _lastError;

    // Position/target fields need atomic writes. double? via object lock.
    private readonly object _state = new();
    private double? _currentAz;
    private double? _targetAz;
    private DateTime _lastCommandUtc;

    // Signal the loop to reconnect after a config change.
    private readonly SemaphoreSlim _configChanged = new(0, 1);

    public RotctldService(ILogger<RotctldService> log, RotctldConfigStore store)
    {
        _log = log;
        _store = store;
        // Hydrate config from disk at construction time so GetStatus() returns
        // the operator's saved host/port even before the loop has run, and
        // ExecuteAsync's first tick will pick up Enabled=true and reconnect.
        _config = _store.Get();
    }

    /// <summary>Returns the live config snapshot (host / port / enabled / polling interval).
    /// Hydrated from <see cref="RotctldConfigStore"/> at construction; updated by <see cref="SetConfigAsync"/>.</summary>
    public RotctldConfig GetConfig() => _config;

    public RotctldStatus GetStatus()
    {
        double? cur, tgt;
        lock (_state) { cur = _currentAz; tgt = _targetAz; }
        var moving = tgt != null && cur != null && Math.Abs(NormDelta(tgt.Value - cur.Value)) > MovingEpsilonDeg;
        return new RotctldStatus(
            Enabled: _config.Enabled,
            Connected: _connected,
            Host: _config.Host,
            Port: _config.Port,
            CurrentAz: cur,
            TargetAz: tgt,
            Moving: moving,
            Error: _lastError);
    }

    public async Task<RotctldStatus> SetConfigAsync(RotctldConfig next, CancellationToken ct)
    {
        await _io.WaitAsync(ct);
        try
        {
            _config = next with
            {
                Host = string.IsNullOrWhiteSpace(next.Host) ? "127.0.0.1" : next.Host.Trim(),
                Port = next.Port is > 0 and < 65536 ? next.Port : 4533,
                PollingIntervalMs = Math.Clamp(next.PollingIntervalMs, 100, 10_000),
            };
            // Persist server-side so other clients (phone, second browser,
            // post-restart sessions) see the same connected state without
            // needing their own localStorage copy.
            _store.Set(_config);
            DisconnectLocked();
            lock (_state) { _currentAz = null; _targetAz = null; }
            _lastError = null;
            // Kick the loop so it picks up the new config immediately.
            if (_configChanged.CurrentCount == 0) _configChanged.Release();
        }
        finally
        {
            _io.Release();
        }
        return GetStatus();
    }

    public async Task<RotctldStatus> SetAzAsync(double az, CancellationToken ct)
    {
        var normalized = ((az % 360) + 360) % 360;
        await _io.WaitAsync(ct);
        try
        {
            if (!_connected || _writer == null || _reader == null)
            {
                _lastError = "rotctld not connected";
                return GetStatus();
            }
            try
            {
                // Short-form: P <az> <el>. Zero elevation — we don't model a dual-axis rotator yet.
                await _writer.WriteAsync($"P {normalized.ToString("F2", CultureInfo.InvariantCulture)} 0\n");
                await _writer.FlushAsync(ct);
                var reply = await _reader.ReadLineAsync(ct);
                if (reply == null) throw new IOException("rotctld closed connection");
                // rotctld answers "RPRT 0" on success, "RPRT -<n>" otherwise.
                if (!reply.StartsWith("RPRT 0", StringComparison.Ordinal))
                {
                    _lastError = $"rotctld P command: {reply}";
                }
                else
                {
                    _lastError = null;
                    lock (_state) { _targetAz = normalized; _lastCommandUtc = DateTime.UtcNow; }
                }
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                DisconnectLocked();
            }
        }
        finally
        {
            _io.Release();
        }
        return GetStatus();
    }

    public async Task<RotctldStatus> StopRotatorAsync(CancellationToken ct)
    {
        await _io.WaitAsync(ct);
        try
        {
            if (!_connected || _writer == null || _reader == null) return GetStatus();
            try
            {
                await _writer.WriteAsync("S\n");
                await _writer.FlushAsync(ct);
                _ = await _reader.ReadLineAsync(ct);
                lock (_state) { _targetAz = null; }
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                DisconnectLocked();
            }
        }
        finally
        {
            _io.Release();
        }
        return GetStatus();
    }

    /// <summary>One-shot probe against an arbitrary host:port without disturbing the running connection.</summary>
    public async Task<RotctldTestResult> TestAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var tc = new TcpClient();
            using var dialCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            dialCts.CancelAfter(TimeSpan.FromSeconds(3));
            await tc.ConnectAsync(host, port, dialCts.Token);
            using var stream = tc.GetStream();
            using var sr = new StreamReader(stream, Encoding.ASCII);
            await using var sw = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\n" };
            await sw.WriteAsync("p\n");
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(TimeSpan.FromSeconds(2));
            var az = await sr.ReadLineAsync(readCts.Token);
            if (az == null) return new RotctldTestResult(false, "rotctld closed connection before reply");
            // Accept either numeric az line or "RPRT -n" error — connection proves rotctld is there.
            return new RotctldTestResult(true, null);
        }
        catch (Exception ex)
        {
            return new RotctldTestResult(false, ex.Message);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var cfg = _config;
            if (!cfg.Enabled)
            {
                // Wait for enable or cancellation.
                try { await _configChanged.WaitAsync(stoppingToken); } catch (OperationCanceledException) { return; }
                continue;
            }

            if (!_connected)
            {
                await _io.WaitAsync(stoppingToken);
                try
                {
                    await ConnectLockedAsync(cfg, stoppingToken);
                }
                finally
                {
                    _io.Release();
                }

                if (!_connected)
                {
                    // Back off; wake early on config change.
                    var delayTask = Task.Delay(ReconnectBackoff, stoppingToken);
                    var configTask = _configChanged.WaitAsync(stoppingToken);
                    await Task.WhenAny(delayTask, configTask);
                    continue;
                }
            }

            // Poll a position sample.
            await _io.WaitAsync(stoppingToken);
            try
            {
                if (_connected && _writer != null && _reader != null)
                {
                    try
                    {
                        await _writer.WriteAsync("p\n");
                        await _writer.FlushAsync(stoppingToken);
                        var az = await _reader.ReadLineAsync(stoppingToken);
                        var el = await _reader.ReadLineAsync(stoppingToken);
                        if (az == null || el == null) throw new IOException("rotctld closed connection");
                        if (double.TryParse(az.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var azd))
                        {
                            lock (_state) { _currentAz = azd; }
                            _lastError = null;
                        }
                    }
                    catch (Exception ex)
                    {
                        _lastError = ex.Message;
                        DisconnectLocked();
                    }
                }
            }
            finally
            {
                _io.Release();
            }

            try { await Task.Delay(cfg.PollingIntervalMs, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task ConnectLockedAsync(RotctldConfig cfg, CancellationToken ct)
    {
        try
        {
            var tc = new TcpClient();
            using var dialCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            dialCts.CancelAfter(TimeSpan.FromSeconds(3));
            await tc.ConnectAsync(cfg.Host, cfg.Port, dialCts.Token);
            var stream = tc.GetStream();
            _client = tc;
            _reader = new StreamReader(stream, Encoding.ASCII);
            _writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = false, NewLine = "\n" };
            _connected = true;
            _lastError = null;
            _log.LogInformation("rotctld connected {Host}:{Port}", cfg.Host, cfg.Port);
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _connected = false;
            DisposeConnectionLocked();
        }
    }

    private void DisconnectLocked()
    {
        if (_connected) _log.LogInformation("rotctld disconnect");
        _connected = false;
        DisposeConnectionLocked();
    }

    private void DisposeConnectionLocked()
    {
        try { _writer?.Dispose(); } catch { /* ignore */ }
        try { _reader?.Dispose(); } catch { /* ignore */ }
        try { _client?.Dispose(); } catch { /* ignore */ }
        _writer = null;
        _reader = null;
        _client = null;
    }

    public override void Dispose()
    {
        DisposeConnectionLocked();
        _io.Dispose();
        _configChanged.Dispose();
        base.Dispose();
    }

    // Shortest signed delta in degrees across the 0/360 wrap.
    private static double NormDelta(double d)
    {
        d = ((d % 360) + 360) % 360;
        return d > 180 ? d - 360 : d;
    }
}
