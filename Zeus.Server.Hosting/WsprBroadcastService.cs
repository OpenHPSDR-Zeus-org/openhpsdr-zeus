// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// WsprBroadcastService — bridges WsprService.SpotsReady to the WebSocket hub.
// On every completed 120 s slot it maps the spot batch to the wire DTO and
// pushes a 0x39 WsprSpot frame to all connected clients (the WSPR workspace
// renders it). Event-driven, no background loop — mirrors Ft8BroadcastService.

using Microsoft.Extensions.Hosting;
using Zeus.Contracts;

namespace Zeus.Server;

public sealed class WsprBroadcastService : BackgroundService
{
    private readonly WsprService _wspr;
    private readonly StreamingHub _hub;

    public WsprBroadcastService(WsprService wspr, StreamingHub hub)
    {
        _wspr = wspr;
        _hub = hub;
        _wspr.SpotsReady += OnSpots;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;

    private void OnSpots(WsprSpotBatch batch)
    {
        var spots = new List<WsprSpotDto>(batch.Spots.Count);
        foreach (var s in batch.Spots)
            spots.Add(new WsprSpotDto(s.SnrDb, s.DtSec, s.FreqMhz, s.DriftHz, s.Message));

        var dto = new WsprSpotBatchDto(
            batch.Receiver,
            new DateTimeOffset(batch.SlotStartUtc, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            batch.DialFreqMhz,
            spots);

        _hub.BroadcastWsprSpot(WsprSpotFrame.Encode(dto));
    }

    public override void Dispose()
    {
        _wspr.SpotsReady -= OnSpots;
        base.Dispose();
    }
}
