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
using System.Reflection;
using Microsoft.Extensions.Hosting;
using Zeus.Dsp.Ft8;

namespace Zeus.Server.Spotting;

/// <summary>
/// Subscribes to <see cref="Ft8Service.DecodesReady"/>, buffers reportable
/// decodes, and periodically uploads them to PSK Reporter as one (or more) IPFIX
/// datagrams. Same leaf-subscriber seam as Ft8BroadcastService / FreeDvReporter:
/// the decode handler only ENQUEUES (never calls back into the radio/DSP/TX) and
/// swallows its own errors, so a reporter fault can never disturb decode or TX.
/// Flushing happens off the decode thread on a 5-minute timer (or sooner if the
/// buffer fills). DISABLED by default and additionally no-ops when operator
/// identity (callsign + grid) is unresolved.
/// </summary>
public sealed class PskReporterReporter : BackgroundService
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMinutes(5);

    // Bound memory: at most this many distinct callsigns buffered between flushes.
    private const int MaxBufferedCalls = 512;

    // Keep each datagram comfortably under a typical UDP MTU (~1500 B). Each
    // sender record is ~30 B; 40 records + headers/descriptors stays < ~1.3 KB.
    private const int MaxRecordsPerDatagram = 40;

    private static readonly string DecodingSoftware = BuildSoftwareString();

    private readonly ILogger<PskReporterReporter> _log;
    private readonly Ft8Service? _ft8;
    private readonly RadioService? _radio;
    private readonly SpottingManagementService _spotting;

    // Dedup buffer keyed by callsign — keeps the best-SNR sighting in the window
    // (PSK Reporter only wants "heard X"). Guarded by _sync.
    private readonly Dictionary<string, PskReporterEncoder.SenderRecord> _pending = new();
    private readonly object _sync = new();

    private readonly SemaphoreSlim _flushNow = new(0, 1);
    private readonly uint _observationDomainId =
        (uint)Random.Shared.Next(1, int.MaxValue);
    private uint _sequenceNumber;

    public PskReporterReporter(
        ILogger<PskReporterReporter> log,
        Ft8Service ft8,
        RadioService radio,
        SpottingManagementService spotting)
    {
        _log = log;
        _ft8 = ft8;
        _radio = radio;
        _spotting = spotting;
        _ft8.DecodesReady += OnDecodes;
    }

    // Test seam: no Ft8Service/RadioService wiring (so no DSP/radio graph needed).
    // Exercise the gate via OnDecodes, the dedup/cap via EnqueueDecodes, and the
    // flush gate via FlushAsync directly.
    internal PskReporterReporter(
        ILogger<PskReporterReporter> log,
        SpottingManagementService spotting)
    {
        _log = log;
        _ft8 = null;
        _radio = null;
        _spotting = spotting;
    }

    public override void Dispose()
    {
        if (_ft8 is not null) _ft8.DecodesReady -= OnDecodes;
        _flushNow.Dispose();
        base.Dispose();
    }

    // Test-only observability of the dedup buffer.
    internal int PendingCount { get { lock (_sync) return _pending.Count; } }

    internal bool TryGetPending(string call, out PskReporterEncoder.SenderRecord rec)
    {
        lock (_sync) return _pending.TryGetValue(call, out rec);
    }

    // Drive the flush gate directly (no timer) for tests.
    internal Task FlushAsyncForTests() => FlushAsync(CancellationToken.None);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Wake on the timer OR a fill-triggered signal, whichever first.
                await _flushNow.WaitAsync(FlushInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await FlushAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "psk-reporter flush failed");
            }
        }
    }

    // Decode-thread handler — gate then enqueue only, never throws.
    private void OnDecodes(Ft8DecodeBatch batch)
    {
        try
        {
            HandleDecodes(batch, _radio?.Snapshot().VfoHz ?? 0);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "psk-reporter enqueue failed");
        }
    }

    // Full decode-handler gate (enable + identity + RX0 + valid dial) then enqueue.
    // Internal + dial-injected so the whole gate is unit-testable without a live
    // radio: disabled => nothing buffered, no-identity => nothing buffered,
    // non-RX0 => nothing buffered, enabled+identity => buffered.
    internal void HandleDecodes(Ft8DecodeBatch batch, long dialHz)
    {
        if (!_spotting.GetConfig().PskReporterEnabled) return;

        var (call, grid) = _spotting.ResolveOperator();
        if (string.IsNullOrWhiteSpace(call) || string.IsNullOrWhiteSpace(grid))
            return; // identity not set — nothing to report from

        // FT8 native decodes on RX0 only today; the absolute RF frequency is
        // RX0 dial + audio offset, so a batch from any other receiver would be
        // reported on the wrong dial. Guard explicitly until multi-RX FT8 lands
        // (then resolve the dial from batch.Receiver instead of the RX0 dial).
        if (batch.Receiver != 0) return;

        if (dialHz <= 0) return;

        EnqueueDecodes(batch, dialHz);
    }

    // Dedup/cap a decoded batch into the pending buffer. Reached only after the
    // gate in HandleDecodes. The dedup ("keep the strongest sighting") and the
    // MaxBufferedCalls cap are exercised here. Signals an early flush on overflow.
    private void EnqueueDecodes(Ft8DecodeBatch batch, long dialHz)
    {
        string mode = batch.Protocol == Ft8Protocol.Ft4 ? "FT4" : "FT8";
        uint flowStart = ToFlowStartSeconds(batch.SlotStartUtc);

        bool overflowed = false;
        lock (_sync)
        {
            foreach (var d in batch.Decodes)
            {
                if (!Ft8MessageParse.TryParseSender(d.Text, out var senderCall, out var senderGrid))
                    continue;

                long absHz = dialHz + (long)Math.Round(d.FreqHz);
                if (absHz <= 0 || absHz > uint.MaxValue) continue;

                sbyte snr = (sbyte)Math.Clamp((int)Math.Round(d.SnrDb), -128, 127);

                var rec = new PskReporterEncoder.SenderRecord(
                    Callsign: senderCall,
                    Grid: senderGrid,
                    FrequencyHz: (uint)absHz,
                    SnrDb: snr,
                    Mode: mode,
                    FlowStartSeconds: flowStart);

                if (_pending.TryGetValue(senderCall, out var existing))
                {
                    // Keep the strongest sighting in the window.
                    if (rec.SnrDb > existing.SnrDb)
                        _pending[senderCall] = rec;
                }
                else
                {
                    if (_pending.Count >= MaxBufferedCalls)
                    {
                        overflowed = true;
                        continue;
                    }
                    _pending[senderCall] = rec;
                }
            }

            if (_pending.Count >= MaxBufferedCalls) overflowed = true;
        }

        if (overflowed && _flushNow.CurrentCount == 0)
        {
            try { _flushNow.Release(); } catch (SemaphoreFullException) { /* already signalled */ }
        }
    }

    // PSK Reporter flowStartSeconds is a UNIX timestamp. SlotStartUtc is built in
    // UTC today, but new DateTimeOffset(dt, TimeSpan.Zero) THROWS if Kind==Local,
    // and that throw would be swallowed by OnDecodes' catch and silently kill ALL
    // reporting. Normalize to UTC first so a future slot-time refactor can't.
    private static uint ToFlowStartSeconds(DateTime slotStart)
    {
        var utc = slotStart.Kind switch
        {
            DateTimeKind.Utc => slotStart,
            DateTimeKind.Local => slotStart.ToUniversalTime(),
            _ => DateTime.SpecifyKind(slotStart, DateTimeKind.Utc),
        };
        return (uint)new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeSeconds();
    }

    // Split deduped records into datagram-sized chunks. Pure + internal so the
    // off-by-one chunking can be unit-tested without sending UDP.
    internal static List<List<PskReporterEncoder.SenderRecord>> Chunk(
        IReadOnlyList<PskReporterEncoder.SenderRecord> records, int maxPerChunk)
    {
        var chunks = new List<List<PskReporterEncoder.SenderRecord>>();
        for (int i = 0; i < records.Count; i += maxPerChunk)
        {
            int n = Math.Min(maxPerChunk, records.Count - i);
            var chunk = new List<PskReporterEncoder.SenderRecord>(n);
            for (int j = 0; j < n; j++) chunk.Add(records[i + j]);
            chunks.Add(chunk);
        }
        return chunks;
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        // Re-check the enable flag at flush time — the operator may have disabled
        // it since the records were buffered.
        if (!_spotting.GetConfig().PskReporterEnabled)
        {
            lock (_sync) _pending.Clear();
            return;
        }

        var (call, grid) = _spotting.ResolveOperator();
        if (string.IsNullOrWhiteSpace(call) || string.IsNullOrWhiteSpace(grid))
        {
            lock (_sync) _pending.Clear();
            return;
        }

        List<PskReporterEncoder.SenderRecord> snapshot;
        lock (_sync)
        {
            if (_pending.Count == 0) return;
            snapshot = new List<PskReporterEncoder.SenderRecord>(_pending.Values);
            _pending.Clear();
        }

        var rx = new PskReporterEncoder.ReceiverInfo(call, grid, DecodingSoftware);
        uint exportTime = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        try
        {
            using var udp = new UdpClient();
            foreach (var chunk in Chunk(snapshot, MaxRecordsPerDatagram))
            {
                var datagram = PskReporterEncoder.Encode(
                    rx, chunk, exportTime, unchecked(_sequenceNumber++), _observationDomainId);
                await udp.SendAsync(datagram, datagram.Length, PskReporterEncoder.Host, PskReporterEncoder.Port)
                    .WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            }
            _log.LogInformation("psk-reporter uploaded {Count} spot(s)", snapshot.Count);
        }
        catch (Exception ex)
        {
            // Tolerate all network failure — never crash the decode path. The
            // dropped batch is acceptable; the next window re-reports live stations.
            _log.LogWarning(ex, "psk-reporter upload failed ({Count} spot(s) dropped)", snapshot.Count);
        }
    }

    private static string BuildSoftwareString()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        var ver = v is null ? "" : $" {v.Major}.{v.Minor}.{v.Build}";
        return $"Zeus{ver}";
    }
}
