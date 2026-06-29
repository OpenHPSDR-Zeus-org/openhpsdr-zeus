// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.

using System.Globalization;
using System.Net.Sockets;
using System.Text;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Persistent TCP client for hamlib's rotctld. Multi-slot capable (issue
/// #917): the operator configures up to 4 named rotator slots, each with a
/// host / port and a list of bands it covers. At any one time exactly one
/// slot is *active* — the service holds a single live TCP connection to that
/// slot's rotctld endpoint, polls its position at the configured interval,
/// and reconnects with 5-second backoff on failure. Switching the active
/// slot (manually via the panel selector or automatically when the TX VFO
/// crosses into a band assigned to another slot) closes the current
/// connection and opens a new one. Inactive slots are static config; they
/// don't hold sockets.
///
/// Legacy single-slot endpoints (/api/rotator/status, /api/rotator/config,
/// /api/rotator/set, /api/rotator/stop) project the active slot into the
/// pre-#917 shape so the existing Compass and Dial panels keep working
/// unchanged.
/// </summary>
public sealed class RotctldService : BackgroundService
{
    private const int MovingEpsilonDeg = 1;
    private static readonly TimeSpan ReconnectBackoff = TimeSpan.FromSeconds(5);

    private readonly ILogger<RotctldService> _log;
    private readonly RotctldConfigStore _store;
    private readonly RadioService? _radio;
    private readonly SemaphoreSlim _io = new(1, 1);

    // Serialised by _io for connection state, lock-free volatile reads for status snapshot.
    private TcpClient? _client;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    private volatile RotctldMultiConfig _config;
    private volatile bool _connected;
    private volatile string? _lastError;

    // Position/target fields need atomic writes. double? via object lock.
    private readonly object _state = new();
    private double? _currentAz;
    private double? _targetAz;
    private DateTime _lastCommandUtc;

    // Signal the loop to reconnect after a config change.
    private readonly SemaphoreSlim _configChanged = new(0, 1);

    // Last band we routed for, so we don't fire a slot switch on every
    // StateChanged tick — only when the TX VFO actually crosses a band edge.
    private string? _lastRoutedBand;

    // Set first in Dispose() so the radio's StateChanged callback stops
    // spawning auto-route work while we're tearing down.
    private volatile bool _disposed;

    public RotctldService(ILogger<RotctldService> log, RotctldConfigStore store, RadioService? radio = null)
    {
        _log = log;
        _store = store;
        _radio = radio;
        // Hydrate config from disk at construction time so GetStatus() returns
        // the operator's saved slots even before the loop has run, and
        // ExecuteAsync's first tick will pick up an enabled active slot and
        // reconnect.
        _config = _store.Get();

        // Band-driven auto-routing: when the TX VFO crosses a band edge and
        // AutoRoute is on, switch the active slot to the one that owns the
        // new band. RadioService is nullable so existing unit-test
        // constructions of RotctldService without a wired radio still build.
        if (_radio != null)
        {
            _radio.StateChanged += OnRadioStateChanged;
        }
    }

    /// <summary>Legacy single-slot config view — host/port/enabled/polling of
    /// the active slot. Compass and Dial panels still hit this so they don't
    /// need to learn about slots. Multi-slot config lives in
    /// <see cref="GetMultiConfig"/>.</summary>
    public RotctldConfig GetConfig()
    {
        var cfg = _config;
        var active = ActiveSlotOrFirst(cfg);
        return new RotctldConfig(
            Enabled: active.Enabled,
            Host: active.Host,
            Port: active.Port,
            PollingIntervalMs: active.PollingIntervalMs);
    }

    public RotctldMultiConfig GetMultiConfig() => _config;

    public RotctldStatus GetStatus()
    {
        double? cur, tgt;
        lock (_state) { cur = _currentAz; tgt = _targetAz; }
        var moving = tgt != null && cur != null && Math.Abs(NormDelta(tgt.Value - cur.Value)) > MovingEpsilonDeg;
        var cfg = _config;
        var active = ActiveSlotOrFirst(cfg);
        return new RotctldStatus(
            Enabled: active.Enabled,
            Connected: _connected,
            Host: active.Host,
            Port: active.Port,
            CurrentAz: cur,
            TargetAz: tgt,
            Moving: moving,
            Error: _lastError,
            ActiveSlotId: active.Id,
            SlotCount: cfg.Slots.Count);
    }

    /// <summary>Legacy single-slot config setter — writes host/port/enabled
    /// into the active slot only, leaves other slots unchanged. Multi-slot
    /// edits go through <see cref="SetMultiConfigAsync"/>.</summary>
    public async Task<RotctldStatus> SetConfigAsync(RotctldConfig next, CancellationToken ct)
    {
        await _io.WaitAsync(ct);
        try
        {
            var host = string.IsNullOrWhiteSpace(next.Host) ? "127.0.0.1" : next.Host.Trim();
            var port = next.Port is > 0 and < 65536 ? next.Port : 4533;
            var poll = Math.Clamp(next.PollingIntervalMs, 100, 10_000);
            var cfg = _config;
            var slots = cfg.Slots.ToList();
            var idx = slots.FindIndex(s => s.Id == cfg.ActiveSlotId);
            if (idx < 0)
            {
                // No active slot in the list (should not happen post-migration,
                // but be defensive): seed one.
                slots.Insert(0, new RotctldSlot(1, "Rotator 1", next.Enabled, host, port,
                    BandUtils.HfBands.ToArray(), poll));
                cfg = cfg with { ActiveSlotId = 1, Slots = slots };
            }
            else
            {
                slots[idx] = slots[idx] with
                {
                    Enabled = next.Enabled,
                    Host = host,
                    Port = port,
                    PollingIntervalMs = poll,
                };
                cfg = cfg with { Slots = slots };
            }
            ApplyConfigLocked(cfg, resetState: true);
        }
        finally
        {
            _io.Release();
        }
        return GetStatus();
    }

    public async Task<RotctldMultiConfig> SetMultiConfigAsync(RotctldMultiConfig next, CancellationToken ct)
    {
        await _io.WaitAsync(ct);
        try
        {
            var sanitized = Sanitize(next);
            // If active slot moved or its connection params changed, drop and
            // reconnect on the loop's next tick.
            var prev = _config;
            var prevActive = ActiveSlotOrFirst(prev);
            var newActive = ActiveSlotOrFirst(sanitized);
            bool resetState =
                prevActive.Id != newActive.Id
                || prevActive.Host != newActive.Host
                || prevActive.Port != newActive.Port
                || prevActive.Enabled != newActive.Enabled
                || prevActive.PollingIntervalMs != newActive.PollingIntervalMs;
            ApplyConfigLocked(sanitized, resetState);
            return _config;
        }
        finally
        {
            _io.Release();
        }
    }

    public async Task<RotctldStatus> SetActiveSlotAsync(int slotId, CancellationToken ct)
    {
        await _io.WaitAsync(ct);
        try
        {
            var cfg = _config;
            if (cfg.Slots.All(s => s.Id != slotId))
            {
                _lastError = $"unknown slot id {slotId}";
            }
            else if (cfg.ActiveSlotId != slotId)
            {
                ApplyConfigLocked(cfg with { ActiveSlotId = slotId }, resetState: true);
            }
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
                // Bound the reply read — a rotctld that accepts the command but
                // never answers must not hang here holding _io. On timeout the
                // catch below drops the connection and the loop reconnects.
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readCts.CancelAfter(TimeSpan.FromSeconds(3));
                var reply = await _reader.ReadLineAsync(readCts.Token);
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
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readCts.CancelAfter(TimeSpan.FromSeconds(3));
                var reply = await _reader.ReadLineAsync(readCts.Token);
                if (reply == null) throw new IOException("rotctld closed connection");
                // "Stop the tower" must not silently report success on failure:
                // rotctld answers "RPRT 0" on success, "RPRT -<n>" otherwise.
                if (!reply.StartsWith("RPRT 0", StringComparison.Ordinal))
                {
                    _lastError = $"rotctld S command: {reply}";
                }
                else
                {
                    _lastError = null;
                    lock (_state) { _targetAz = null; }
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
            var active = ActiveSlotOrFirst(cfg);
            if (!active.Enabled)
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
                    await ConnectLockedAsync(active, stoppingToken);
                }
                finally
                {
                    _io.Release();
                }

                if (!_connected)
                {
                    // Back off; wake early on config change. Link both waits to
                    // one CTS and cancel it after WhenAny so the losing wait is
                    // de-queued rather than left as a live FIFO waiter that
                    // could steal the next config-change permit. Observe both
                    // tasks (read .Exception) so a cancelled loser doesn't
                    // surface as an unobserved-exception.
                    using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    var delayTask = Task.Delay(ReconnectBackoff, waitCts.Token);
                    var configTask = _configChanged.WaitAsync(waitCts.Token);
                    await Task.WhenAny(delayTask, configTask);
                    waitCts.Cancel();
                    _ = delayTask.ContinueWith(static t => t.Exception, TaskScheduler.Default);
                    _ = configTask.ContinueWith(static t => t.Exception, TaskScheduler.Default);
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
                        // Bound the read: a TCP-connected-but-silent rotctld
                        // (wedged hamlib backend, half-open socket, controller
                        // that stopped answering) must not hang here — we hold
                        // _io across this read, so a stall would freeze every
                        // config / point / stop / slot-switch call queued
                        // behind it. On timeout the catch drops the connection
                        // and the outer loop reconnects.
                        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        readCts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(2000, active.PollingIntervalMs * 2)));
                        var az = await _reader.ReadLineAsync(readCts.Token);
                        if (az == null) throw new IOException("rotctld closed connection");
                        // rotctld answers 'p' with TWO lines (az\nel) on success
                        // but a SINGLE "RPRT -n" line on error. Reading a second
                        // line unconditionally would block until the next reply
                        // and desync the stream — so branch on the first line.
                        if (az.StartsWith("RPRT", StringComparison.Ordinal))
                        {
                            _lastError = $"rotctld p command: {az}";
                        }
                        else
                        {
                            var el = await _reader.ReadLineAsync(readCts.Token);
                            if (el == null) throw new IOException("rotctld closed connection");
                            if (double.TryParse(az.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var azd))
                            {
                                lock (_state) { _currentAz = azd; }
                                _lastError = null;
                            }
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
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

            try { await Task.Delay(active.PollingIntervalMs, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task ConnectLockedAsync(RotctldSlot slot, CancellationToken ct)
    {
        try
        {
            var tc = new TcpClient();
            using var dialCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            dialCts.CancelAfter(TimeSpan.FromSeconds(3));
            await tc.ConnectAsync(slot.Host, slot.Port, dialCts.Token);
            var stream = tc.GetStream();
            _client = tc;
            _reader = new StreamReader(stream, Encoding.ASCII);
            _writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = false, NewLine = "\n" };
            _connected = true;
            _lastError = null;
            _log.LogInformation("rotctld connected slot={SlotId} ({Label}) {Host}:{Port}",
                slot.Id, slot.Label, slot.Host, slot.Port);
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

    // Caller holds _io.
    private void ApplyConfigLocked(RotctldMultiConfig next, bool resetState)
    {
        _config = next;
        _store.Set(next);
        if (resetState)
        {
            DisconnectLocked();
            lock (_state) { _currentAz = null; _targetAz = null; }
            _lastError = null;
        }
        if (_configChanged.CurrentCount == 0) _configChanged.Release();
    }

    private void OnRadioStateChanged(StateDto state)
    {
        if (_disposed) return;
        var target = RouteForState(state);
        if (target == null) return;
        // Fire-and-forget — we're on the radio's state-change callback and
        // mustn't block it. AutoRouteAsync bounds its own token (so a wedged
        // _io can never strand it forever) and observes faults.
        _ = AutoRouteAsync(target.Value.SlotId, target.Value.Band, target.Value.Label);
    }

    /// <summary>
    /// Pure routing decision: given a radio state, which slot (if any) should
    /// become active? Returns null for a no-op — auto-route off, VFO out of
    /// band, same band as last routed (dedupe), no <em>enabled</em> slot owns
    /// the band, or the owning slot is already active. Mutates
    /// <see cref="_lastRoutedBand"/> exactly as the live handler does so the
    /// dedupe semantics are preserved. Extracted from
    /// <see cref="OnRadioStateChanged"/> so the routing truth table is unit
    /// testable without a radio or a socket.
    /// </summary>
    internal (int SlotId, string Band, string Label)? RouteForState(StateDto state)
    {
        var cfg = _config;
        if (!cfg.AutoRoute) { _lastRoutedBand = null; return null; }
        var txHz = RadioService.TxFrequencyHz(state);
        var band = BandUtils.FreqToBand(txHz);
        if (band == null) return null;
        if (string.Equals(band, _lastRoutedBand, StringComparison.OrdinalIgnoreCase)) return null;
        _lastRoutedBand = band;

        // Only route onto an ENABLED slot. Auto-switching onto a disabled slot
        // would disconnect the working rotator and silently park (ExecuteAsync
        // idles a !Enabled active slot) — a footgun for a blind ship. When no
        // enabled slot owns the new band, leave the currently-active slot
        // alone and surface a one-line note.
        var match = cfg.Slots.FirstOrDefault(s =>
            s.Enabled &&
            s.Bands.Any(b => string.Equals(b, band, StringComparison.OrdinalIgnoreCase)));
        if (match == null)
        {
            _lastError = $"no enabled rotator for {band}";
            return null;
        }
        if (match.Id == cfg.ActiveSlotId) return null;
        return (match.Id, band, match.Label);
    }

    private async Task AutoRouteAsync(int slotId, string band, string label)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await SetActiveSlotAsync(slotId, cts.Token);
            _log.LogInformation("rotctld auto-route band={Band} -> slot {SlotId} ({Label})",
                band, slotId, label);
        }
        catch (ObjectDisposedException) { /* service shutting down */ }
        catch (OperationCanceledException) { /* shutdown or the 5 s safety cap */ }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "rotctld auto-route to slot {SlotId} failed", slotId);
        }
    }

    private static RotctldSlot ActiveSlotOrFirst(RotctldMultiConfig cfg)
    {
        if (cfg.Slots.Count == 0)
        {
            // Defensive fallback — should not happen post-migration but keeps
            // GetStatus from NullReferenceException-ing on a degenerate config.
            return new RotctldSlot(1, "Rotator 1", false, "127.0.0.1", 4533,
                BandUtils.HfBands.ToArray(), 500);
        }
        return cfg.Slots.FirstOrDefault(s => s.Id == cfg.ActiveSlotId) ?? cfg.Slots[0];
    }

    // Drop empty/garbage slots, clamp numeric fields, dedup ids, cap at
    // MaxSlots, ensure the active id is one we actually have.
    internal static RotctldMultiConfig Sanitize(RotctldMultiConfig cfg)
    {
        var slots = new List<RotctldSlot>();
        var seenIds = new HashSet<int>();
        foreach (var s in cfg.Slots ?? (IReadOnlyList<RotctldSlot>)Array.Empty<RotctldSlot>())
        {
            if (slots.Count >= RotctldConfigStore.MaxSlots) break;
            var id = s.Id <= 0 ? NextFreeId(seenIds) : s.Id;
            if (!seenIds.Add(id)) id = NextFreeId(seenIds);
            seenIds.Add(id);
            var label = string.IsNullOrWhiteSpace(s.Label) ? $"Rotator {id}" : s.Label.Trim();
            var host = string.IsNullOrWhiteSpace(s.Host) ? "127.0.0.1" : s.Host.Trim();
            var port = s.Port is > 0 and < 65536 ? s.Port : 4533;
            var poll = Math.Clamp(s.PollingIntervalMs <= 0 ? 500 : s.PollingIntervalMs, 100, 10_000);
            var bands = (s.Bands ?? Array.Empty<string>())
                .Where(b => !string.IsNullOrWhiteSpace(b))
                .Select(b => b.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            slots.Add(new RotctldSlot(id, label, s.Enabled, host, port, bands, poll));
        }
        if (slots.Count == 0)
        {
            slots.Add(new RotctldSlot(1, "Rotator 1", false, "127.0.0.1", 4533,
                BandUtils.HfBands.ToArray(), 500));
        }
        var activeId = slots.Any(s => s.Id == cfg.ActiveSlotId) ? cfg.ActiveSlotId : slots[0].Id;
        return new RotctldMultiConfig(activeId, cfg.AutoRoute, slots);
    }

    private static int NextFreeId(HashSet<int> seen)
    {
        for (int i = 1; i <= RotctldConfigStore.MaxSlots + 1; i++)
        {
            if (!seen.Contains(i)) return i;
        }
        return RotctldConfigStore.MaxSlots + 1;
    }

    public override void Dispose()
    {
        _disposed = true;
        if (_radio != null) _radio.StateChanged -= OnRadioStateChanged;
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
