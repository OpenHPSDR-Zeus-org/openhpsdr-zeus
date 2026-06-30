// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Ft8BroadcastService — bridges Ft8Service.DecodesReady to the WebSocket hub.
// On every completed slot it maps the decode batch to the wire DTO and pushes
// a 0x38 Ft8Decode frame to all connected clients (the FT8 workspace renders
// it). Event-driven, no background loop — mirrors SpotBroadcastService.

using Microsoft.Extensions.Hosting;
using Zeus.Contracts;
using Zeus.Dsp.Ft8;

namespace Zeus.Server;

public sealed class Ft8BroadcastService : BackgroundService
{
    private readonly Ft8Service _ft8;
    private readonly StreamingHub _hub;
    private readonly LogService _log;

    // Cached set of callsigns worked before on FT8/FT4 (the worked-before
    // highlight source). Refreshed lazily on a short TTL rather than per decode:
    // a logbook scan per slot (~10-30 decodes / 7.5-15 s) would be wasteful, and
    // a TTL — unlike hooking a single create event — also picks up bulk ADIF
    // imports uniformly. The staleness window (≤ TTL) is immaterial for a
    // cosmetic highlight. Guarded by a lock because DecodesReady fires on the
    // decoder thread.
    private static readonly TimeSpan WorkedCacheTtl = TimeSpan.FromSeconds(30);
    private readonly object _workedLock = new();
    private IReadOnlySet<string> _workedCalls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private DateTime _workedLoadedUtc = DateTime.MinValue;

    public Ft8BroadcastService(Ft8Service ft8, StreamingHub hub, LogService log)
    {
        _ft8 = ft8;
        _hub = hub;
        _log = log;
        _ft8.DecodesReady += OnDecodes;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;

    private void OnDecodes(Ft8DecodeBatch batch)
    {
        var worked = GetWorkedCalls();

        var decodes = new List<Ft8DecodeDto>(batch.Decodes.Count);
        foreach (var d in batch.Decodes)
        {
            bool workedBefore = false;
            string? country = null;
            if (Ft8MessageParse.TryParseSender(d.Text, out var sender, out _))
            {
                workedBefore = worked.Contains(sender);
                country = CallsignCountryResolver.Resolve(sender);
            }
            decodes.Add(new Ft8DecodeDto(
                d.SnrDb, d.DtSec, d.FreqHz, d.Score, d.Text, workedBefore, country));
        }

        var dto = new Ft8DecodeBatchDto(
            batch.Receiver,
            new DateTimeOffset(batch.SlotStartUtc, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            batch.Protocol == Ft8Protocol.Ft4 ? "FT4" : "FT8",
            decodes);

        _hub.BroadcastFt8Decode(Ft8DecodeFrame.Encode(dto));
    }

    /// <summary>
    /// Return the cached worked-before set, refreshing it from the logbook when
    /// the TTL has expired. A failed refresh keeps the last good set (a logbook
    /// hiccup must never break the decode broadcast) and is swallowed — the
    /// highlight just goes stale, never throws into the decode pipeline.
    /// </summary>
    private IReadOnlySet<string> GetWorkedCalls()
    {
        lock (_workedLock)
        {
            if (DateTime.UtcNow - _workedLoadedUtc >= WorkedCacheTtl)
            {
                try
                {
                    _workedCalls = _log.GetDigitalWorkedCallsignsAsync().GetAwaiter().GetResult();
                }
                catch
                {
                    // keep the previous set; just push the next retry out by the TTL
                }
                _workedLoadedUtc = DateTime.UtcNow;
            }
            return _workedCalls;
        }
    }

    public override void Dispose()
    {
        _ft8.DecodesReady -= OnDecodes;
        base.Dispose();
    }
}
