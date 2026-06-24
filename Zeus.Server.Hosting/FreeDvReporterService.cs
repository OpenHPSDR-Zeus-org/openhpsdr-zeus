// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// FreeDvReporterService — keeps a live mirror of the FreeDV Reporter stations
// network (qso.freedv.org) for the Stations panel (GET /api/freedv/stations).
// It holds a persistent Socket.IO connection (see SocketIoReporterClient) as a
// read-only "view" observer and folds the event stream into an in-memory
// station table keyed by the reporter's per-connection session id (sid).
//
// Same shape as ActivationSpotsService — a self-contained BackgroundService
// registered as a singleton + hosted service — but driven by a streaming
// Socket.IO link instead of HTTP polling. It touches NOTHING on the radio /
// DSP / TX path: outbound TLS WebSocket in, station snapshot out.

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zeus.Contracts;
using Zeus.Server.FreeDvReporter;

namespace Zeus.Server;

/// <summary>
/// Background service that mirrors the FreeDV Reporter stations feed and exposes
/// the current snapshot via <see cref="GetSnapshot"/>. Registered as a singleton
/// + hosted service in <c>ZeusHost</c>.
/// </summary>
public sealed class FreeDvReporterService : BackgroundService
{
    private const int ReconnectDelaySeconds = 5;
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);

    private readonly ILogger<FreeDvReporterService> _log;
    private readonly ConcurrentDictionary<string, Station> _stations = new();
    private readonly SocketIoReporterClient _client;

    public FreeDvReporterService(ILogger<FreeDvReporterService> log)
    {
        _log = log;
        _client = new SocketIoReporterClient(HandleEvent, log);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        bool reconnect = false;
        while (!ct.IsCancellationRequested)
        {
            // Drop any stale list so a fresh (re)connect starts clean — the
            // reporter resends the full roster via bulk_update on connect.
            _stations.Clear();
            try
            {
                await _client.RunLoopAsync(ct, reconnect).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "FreeDV Reporter link failed; reconnecting in {Delay}s", ReconnectDelaySeconds);
            }

            reconnect = true;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(ReconnectDelaySeconds), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Current stations snapshot: connection state, enabled flag, and the live
    /// station list sorted by frequency ascending (stations that never reported
    /// a frequency are omitted).
    /// </summary>
    public FreeDvStationsResponseDto GetSnapshot()
    {
        var cutoff = DateTime.UtcNow - StaleAfter;
        var list = new List<FreeDvStationDto>(_stations.Count);
        foreach (var s in _stations.Values)
        {
            // Defensive stale-prune — the server normally sends remove_connection,
            // but a dropped disconnect shouldn't leave a ghost forever.
            if (s.LastUpdate < cutoff)
            {
                _stations.TryRemove(s.Sid, out _);
                continue;
            }
            if (s.FreqHz <= 0) continue; // never reported a usable frequency

            list.Add(new FreeDvStationDto(
                Sid: s.Sid,
                Callsign: s.Callsign ?? "",
                GridSquare: s.GridSquare,
                FreqHz: s.FreqHz,
                Mode: s.Mode ?? "",
                Transmitting: s.Transmitting,
                RxOnly: s.RxOnly,
                Message: s.Message,
                Version: s.Version,
                LastRxSnr: s.LastRxSnr is double snr && !double.IsNaN(snr) ? snr : null,
                LastRxCallsign: s.LastRxCallsign,
                LastRxMode: s.LastRxMode,
                LastUpdate: s.LastUpdate.ToString("o", CultureInfo.InvariantCulture),
                ConnectTime: s.ConnectTime?.ToString("o", CultureInfo.InvariantCulture)));
        }

        list.Sort((a, b) => a.FreqHz.CompareTo(b.FreqHz));
        return new FreeDvStationsResponseDto(_client.ConnectionState, Enabled: true, list);
    }

    // ----- Socket.IO event handling -----

    // The single Action handed to SocketIoReporterClient. Dispatches by event
    // name; bulk_update recurses each contained [name, payload] pair through here.
    private void HandleEvent(string name, JsonElement payload)
    {
        switch (name)
        {
            case "bulk_update":
                // Initial snapshot: a JSON array of [eventName, payload] pairs.
                if (payload.ValueKind == JsonValueKind.Array)
                {
                    foreach (var pair in payload.EnumerateArray())
                    {
                        if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() == 0)
                            continue;
                        string? innerName = pair[0].ValueKind == JsonValueKind.String ? pair[0].GetString() : null;
                        if (string.IsNullOrEmpty(innerName)) continue;
                        JsonElement innerPayload = pair.GetArrayLength() > 1 ? pair[1] : default;
                        HandleEvent(innerName!, innerPayload);
                    }
                }
                break;

            case "new_connection":
            case "freq_change":
            case "tx_report":
            case "message_update":
                UpsertStation(payload, isRxReport: false);
                break;

            case "rx_report":
                UpsertStation(payload, isRxReport: true);
                break;

            case "remove_connection":
                {
                    string? sid = GetString(payload, "sid");
                    if (!string.IsNullOrEmpty(sid))
                        _stations.TryRemove(sid!, out _);
                }
                break;

            case "connection_successful":
                _log.LogDebug("FreeDV Reporter: connection_successful");
                break;

            default:
                // qsy_request and any other events are not consumed here.
                break;
        }
    }

    private void UpsertStation(JsonElement p, bool isRxReport)
    {
        if (p.ValueKind != JsonValueKind.Object) return;
        string? sid = GetString(p, "sid");
        if (string.IsNullOrEmpty(sid)) return;

        var s = _stations.GetOrAdd(sid!, k => new Station { Sid = k });

        if (GetString(p, "callsign") is string call)
        {
            if (isRxReport) s.LastRxCallsign = call;
            else s.Callsign = call;
        }
        if (GetString(p, "grid_square") is string grid)
            s.GridSquare = grid.Length > 6 ? grid[..6] : grid;
        if (GetString(p, "version") is string ver)
            s.Version = ver;
        if (GetBool(p, "rx_only") is bool rxOnly)
            s.RxOnly = rxOnly;
        if (GetInt64(p, "freq") is long freq)
            s.FreqHz = freq;
        if (GetString(p, "mode") is string mode)
        {
            if (isRxReport) s.LastRxMode = mode;
            else s.Mode = mode;
        }
        if (GetBool(p, "transmitting") is bool tx)
            s.Transmitting = tx;
        if (GetString(p, "message") is string msg)
            s.Message = msg;
        if (isRxReport && GetDouble(p, "snr") is double snr)
            s.LastRxSnr = snr;
        if (GetDateTime(p, "connect_time") is DateTime connect)
            s.ConnectTime = connect;

        // Always re-stamp LastUpdate from the report (or now if absent), so the
        // stale-prune and any "last heard" UI reflect this event.
        s.LastUpdate = GetDateTime(p, "last_update") ?? DateTime.UtcNow;
    }

    // ----- tolerant JSON field readers (missing / null / string-or-number) -----

    private static string? GetString(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var el))
            return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.GetRawText(),
            _ => null,
        };
    }

    private static bool? GetBool(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var el))
            return null;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(el.GetString(), out var b) => b,
            JsonValueKind.Number when el.TryGetInt64(out var n) => n != 0,
            _ => null,
        };
    }

    private static long? GetInt64(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var el))
            return null;
        return el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetInt64(out var n) => n,
            JsonValueKind.Number when el.TryGetDouble(out var d) => (long)Math.Round(d),
            JsonValueKind.String when long.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sn) => sn,
            JsonValueKind.String when double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sd) => (long)Math.Round(sd),
            _ => null,
        };
    }

    private static double? GetDouble(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var el))
            return null;
        return el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetDouble(out var d) => d,
            JsonValueKind.String when double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sd) => sd,
            _ => null,
        };
    }

    private static DateTime? GetDateTime(JsonElement obj, string name)
    {
        var s = GetString(obj, name);
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
            return dt;
        return null;
    }

    // Internal mutable station record folded from the event stream. Mutated only
    // on the single Socket.IO receive loop; read (and stale-pruned) under the
    // ConcurrentDictionary in GetSnapshot.
    private sealed class Station
    {
        public string Sid = "";
        public string? Callsign;
        public string? GridSquare;
        public long FreqHz;
        public string? Mode;
        public bool Transmitting;
        public bool RxOnly;
        public string? Message;
        public string? Version;
        public double? LastRxSnr;
        public string? LastRxCallsign;
        public string? LastRxMode;
        public DateTime LastUpdate = DateTime.UtcNow;
        public DateTime? ConnectTime;
    }
}
