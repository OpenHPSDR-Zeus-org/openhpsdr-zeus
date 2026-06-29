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
/// <see cref="SpotListFrame"/> to all WS clients whenever the spot list changes.
///
/// <para>Broadcasts are <b>coalesced</b>: a change only marks the snapshot dirty;
/// a periodic flush (<see cref="FlushInterval"/>) serialises and broadcasts once
/// per tick. This keeps the cost off the producer thread (the DX-cluster
/// socket-read loop) and collapses a firehose of per-spot changes into one
/// full-snapshot rebroadcast per interval, instead of O(n) serialise-and-send
/// work on every single spot. A modest TCI trickle is unaffected — it just sees
/// at most one flush-interval of added latency.</para>
/// </summary>
public sealed class SpotBroadcastService : BackgroundService
{
    // Coalescing window. Long enough to collapse a contest-cluster firehose into
    // one rebroadcast per tick, short enough to feel immediate for a logger's
    // trickle. Spot display has no realtime requirement.
    internal static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(300);

    private readonly SpotManager _spots;
    private readonly StreamingHub _hub;

    // 0 = clean, 1 = a change is pending a flush. Set on the producer thread,
    // cleared on the flush thread; Interlocked makes the handoff race-free.
    private int _dirty;

    public SpotBroadcastService(SpotManager spots, StreamingHub hub)
    {
        _spots = spots;
        _hub = hub;
        _spots.SpotsChanged += OnSpotsChanged;
    }

    // Producer-thread handler: just flag dirty. No serialise/broadcast here.
    private void OnSpotsChanged() => Interlocked.Exchange(ref _dirty, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(FlushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                if (Interlocked.Exchange(ref _dirty, 0) == 1)
                    Flush();
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested.
        }

        // Final flush so the last change isn't stranded if it landed between the
        // last tick and cancellation.
        if (Interlocked.Exchange(ref _dirty, 0) == 1)
            Flush();
    }

    private void Flush()
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
