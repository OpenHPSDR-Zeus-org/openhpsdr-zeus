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

    public Ft8BroadcastService(Ft8Service ft8, StreamingHub hub)
    {
        _ft8 = ft8;
        _hub = hub;
        _ft8.DecodesReady += OnDecodes;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;

    private void OnDecodes(Ft8DecodeBatch batch)
    {
        var decodes = new List<Ft8DecodeDto>(batch.Decodes.Count);
        foreach (var d in batch.Decodes)
            decodes.Add(new Ft8DecodeDto(d.SnrDb, d.DtSec, d.FreqHz, d.Score, d.Text));

        var dto = new Ft8DecodeBatchDto(
            batch.Receiver,
            new DateTimeOffset(batch.SlotStartUtc, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            batch.Protocol == Ft8Protocol.Ft4 ? "FT4" : "FT8",
            decodes);

        _hub.BroadcastFt8Decode(Ft8DecodeFrame.Encode(dto));
    }

    public override void Dispose()
    {
        _ft8.DecodesReady -= OnDecodes;
        base.Dispose();
    }
}
