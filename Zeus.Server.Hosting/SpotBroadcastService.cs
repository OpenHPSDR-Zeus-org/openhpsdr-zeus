// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.

using System.Buffers;
using Microsoft.Extensions.Hosting;
using Zeus.Contracts;
using Zeus.Server.Tci;

namespace Zeus.Server;

/// <summary>
/// Watches <see cref="SpotManager.SpotsChanged"/> and broadcasts a fresh
/// <see cref="SpotListFrame"/> to all WS clients whenever the TCI spot list
/// changes. No background loop — entirely event-driven.
/// </summary>
public sealed class SpotBroadcastService : BackgroundService
{
    private readonly SpotManager _spots;
    private readonly StreamingHub _hub;

    public SpotBroadcastService(SpotManager spots, StreamingHub hub)
    {
        _spots = spots;
        _hub = hub;
        _spots.SpotsChanged += OnSpotsChanged;
    }

    // ExecuteAsync is a no-op; all work happens in the SpotsChanged handler.
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;

    private void OnSpotsChanged()
    {
        var all = _spots.GetAll();
        var entries = all.Select(s => new SpotListFrame.SpotEntry(
            s.FreqHz, s.Argb, s.Callsign, s.Mode, s.Comment)).ToList();
        var frame = new SpotListFrame(entries);

        // Estimate payload size: header(3) + per-spot fixed(16) + ~30 bytes avg strings.
        var buf = new ArrayBufferWriter<byte>(3 + all.Length * 48);
        frame.Serialize(buf);
        _hub.BroadcastSpots(buf.WrittenMemory.ToArray());
    }

    public override void Dispose()
    {
        _spots.SpotsChanged -= OnSpotsChanged;
        base.Dispose();
    }
}
