// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Pins the egress-safety gate of the two spotting reporters: no upload unless the
// uploader is ENABLED *and* operator identity (callsign + grid) is resolved. This
// is the load-bearing safety promise for a network-egress feature, so it gets
// direct coverage here (disabled => zero, enabled-but-no-identity => zero,
// enabled+identity => exactly the expected uploads), plus the PSK Reporter dedup,
// cap, and datagram-chunk decisions.

using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Dsp.Ft8;
using Zeus.Server;
using Zeus.Server.Spotting;

namespace Zeus.Server.Tests.Spotting;

public sealed class SpottingReporterGateTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"zeus-spotgate-{Guid.NewGuid():N}");

    public SpottingReporterGateTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // A spotting service backed by a temp store and a logged-out QRZ (Home == null,
    // so ResolveOperator never touches the network and falls through to the
    // override only). Optionally pre-configured enabled with an explicit identity.
    private SpottingManagementService NewSpotting(
        bool pskEnabled = false, bool wsprEnabled = false,
        string call = "", string grid = "")
    {
        var store = new SpottingSettingsStore(
            NullLogger<SpottingSettingsStore>.Instance, Path.Combine(_root, $"spot-{Guid.NewGuid():N}.db"));
        var qrz = new QrzService(
            new SingleClientFactory(), NullLogger<QrzService>.Instance,
            new CredentialStore(NullLogger<CredentialStore>.Instance, Path.Combine(_root, $"creds-{Guid.NewGuid():N}.db")));
        var identity = new OperatorIdentityStore(
            NullLogger<OperatorIdentityStore>.Instance, Path.Combine(_root, $"operator-{Guid.NewGuid():N}.db"));
        var svc = new SpottingManagementService(NullLogger<SpottingManagementService>.Instance, store, identity, qrz);
        if (pskEnabled || wsprEnabled || call.Length > 0 || grid.Length > 0)
            svc.SetConfig(new SpottingRuntimeConfig(pskEnabled, wsprEnabled, call, grid));
        return svc;
    }

    // ---- PSK Reporter (FT8/FT4) -------------------------------------------------

    private static Ft8DecodeBatch Ft8Batch(int receiver, params string[] messages)
    {
        var decodes = new List<Ft8DecodeResult>();
        foreach (var m in messages)
            decodes.Add(new Ft8DecodeResult(SnrDb: -10, DtSec: 0.1f, FreqHz: 1500, Score: 1, LdpcErrors: 0, Text: m));
        return new Ft8DecodeBatch(receiver, new DateTime(2026, 6, 26, 14, 0, 0, DateTimeKind.Utc), Ft8Protocol.Ft8, decodes);
    }

    [Fact]
    public void Psk_Disabled_Buffers_Nothing()
    {
        var spotting = NewSpotting(pskEnabled: false, call: "K1ABC", grid: "FN42");
        using var r = new PskReporterReporter(NullLogger<PskReporterReporter>.Instance, spotting);

        r.HandleDecodes(Ft8Batch(0, "CQ W1AW FN31"), dialHz: 14_074_000);

        Assert.Equal(0, r.PendingCount);
    }

    [Fact]
    public void Psk_Enabled_Without_Identity_Buffers_Nothing()
    {
        var spotting = NewSpotting(pskEnabled: true); // no call/grid, logged-out QRZ
        using var r = new PskReporterReporter(NullLogger<PskReporterReporter>.Instance, spotting);

        r.HandleDecodes(Ft8Batch(0, "CQ W1AW FN31"), dialHz: 14_074_000);

        Assert.Equal(0, r.PendingCount);
    }

    [Fact]
    public void Psk_NonRx0_Batch_Buffers_Nothing()
    {
        var spotting = NewSpotting(pskEnabled: true, call: "K1ABC", grid: "FN42");
        using var r = new PskReporterReporter(NullLogger<PskReporterReporter>.Instance, spotting);

        r.HandleDecodes(Ft8Batch(receiver: 1, "CQ W1AW FN31"), dialHz: 14_074_000);

        Assert.Equal(0, r.PendingCount);
    }

    [Fact]
    public void Psk_Enabled_With_Identity_Buffers_Parsed_Senders()
    {
        var spotting = NewSpotting(pskEnabled: true, call: "K1ABC", grid: "FN42");
        using var r = new PskReporterReporter(NullLogger<PskReporterReporter>.Instance, spotting);

        // Two real senders + one free-text line that must be rejected by the parser.
        r.HandleDecodes(Ft8Batch(0, "CQ W1AW FN31", "K1ABC G0XYZ IO91", "TNX FER QSO"), dialHz: 14_074_000);

        Assert.Equal(2, r.PendingCount);
        Assert.True(r.TryGetPending("W1AW", out var w1aw));
        Assert.Equal(14_074_000u + 1500u, w1aw.FrequencyHz);
        Assert.True(r.TryGetPending("G0XYZ", out _));
    }

    [Fact]
    public void Psk_Dedup_Keeps_Strongest_Sighting()
    {
        var spotting = NewSpotting(pskEnabled: true, call: "K1ABC", grid: "FN42");
        using var r = new PskReporterReporter(NullLogger<PskReporterReporter>.Instance, spotting);

        var batch = new Ft8DecodeBatch(0, new DateTime(2026, 6, 26, 14, 0, 0, DateTimeKind.Utc), Ft8Protocol.Ft8,
            new List<Ft8DecodeResult>
            {
                new(SnrDb: -18, DtSec: 0.1f, FreqHz: 1500, Score: 1, LdpcErrors: 0, Text: "CQ W1AW FN31"),
                new(SnrDb: -5,  DtSec: 0.1f, FreqHz: 1500, Score: 1, LdpcErrors: 0, Text: "CQ W1AW FN31"),
            });

        r.HandleDecodes(batch, dialHz: 14_074_000);

        Assert.Equal(1, r.PendingCount);
        Assert.True(r.TryGetPending("W1AW", out var rec));
        Assert.Equal((sbyte)-5, rec.SnrDb); // strongest kept
    }

    [Fact]
    public void Psk_Caps_Buffer_At_MaxBufferedCalls()
    {
        var spotting = NewSpotting(pskEnabled: true, call: "K1ABC", grid: "FN42");
        using var r = new PskReporterReporter(NullLogger<PskReporterReporter>.Instance, spotting);

        // 600 distinct senders; the dedup buffer must cap at 512.
        var decodes = new List<Ft8DecodeResult>();
        for (int i = 0; i < 600; i++)
            decodes.Add(new Ft8DecodeResult(-10, 0.1f, 1500, 1, 0, $"CQ W{i}ABC FN31"));
        var batch = new Ft8DecodeBatch(0, new DateTime(2026, 6, 26, 14, 0, 0, DateTimeKind.Utc), Ft8Protocol.Ft8, decodes);

        r.HandleDecodes(batch, dialHz: 14_074_000);

        Assert.Equal(512, r.PendingCount);
    }

    [Fact]
    public async Task Psk_Flush_Clears_Buffer_When_Disabled_Without_Sending()
    {
        // Buffer while enabled, then disable: FlushAsync must drop the buffer and
        // never touch the network. (We assert the buffer was cleared; a UDP send
        // would require an enabled+identity flush, which is bench-validated.)
        var spotting = NewSpotting(pskEnabled: true, call: "K1ABC", grid: "FN42");
        using var r = new PskReporterReporter(NullLogger<PskReporterReporter>.Instance, spotting);
        r.HandleDecodes(Ft8Batch(0, "CQ W1AW FN31"), dialHz: 14_074_000);
        Assert.Equal(1, r.PendingCount);

        spotting.SetConfig(new SpottingRuntimeConfig(PskReporterEnabled: false, Callsign: "K1ABC", Grid: "FN42"));
        await r.FlushAsyncForTests();

        Assert.Equal(0, r.PendingCount);
    }

    [Fact]
    public async Task Psk_Flush_Logs_Heartbeat_On_Success()
    {
        // Redirect the UDP egress to a bound loopback socket so the success path —
        // and its single Info heartbeat — runs without real DNS/network.
        var spotting = NewSpotting(pskEnabled: true, call: "K1ABC", grid: "FN42");
        var log = new CapturingLogger<PskReporterReporter>();
        using var sink = new UdpClient(0); // bind an ephemeral loopback port
        int port = ((IPEndPoint)sink.Client.LocalEndPoint!).Port;

        using var r = new PskReporterReporter(log, spotting)
        {
            TargetHost = "127.0.0.1",
            TargetPort = port,
        };
        r.HandleDecodes(Ft8Batch(0, "CQ W1AW FN31", "K1ABC G0XYZ IO91"), dialHz: 14_074_000);
        Assert.Equal(2, r.PendingCount);

        await r.FlushAsyncForTests();

        Assert.Equal(0, r.PendingCount); // flushed
        Assert.Contains(log.Entries, e =>
            e.Level == LogLevel.Information &&
            e.Message.Contains("psk-reporter.flush") &&
            e.Message.Contains("spots=2"));
    }

    // ---- PSK Reporter datagram chunking ----------------------------------------

    private static PskReporterEncoder.SenderRecord Rec(string call) =>
        new(call, "FN31", 14_074_000, -10, "FT8", 1700000000);

    [Fact]
    public void Chunk_Splits_On_Datagram_Boundary()
    {
        var recs = new List<PskReporterEncoder.SenderRecord>();
        for (int i = 0; i < 41; i++) recs.Add(Rec($"W{i}ABC"));

        var chunks = PskReporterReporter.Chunk(recs, 40);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(40, chunks[0].Count);
        Assert.Single(chunks[1]);
    }

    [Fact]
    public void Chunk_Single_Datagram_When_Under_Limit()
    {
        var recs = new List<PskReporterEncoder.SenderRecord> { Rec("W1AW"), Rec("G0XYZ") };
        var chunks = PskReporterReporter.Chunk(recs, 40);
        Assert.Single(chunks);
        Assert.Equal(2, chunks[0].Count);
    }

    [Fact]
    public void Chunk_Empty_Yields_No_Datagrams()
    {
        Assert.Empty(PskReporterReporter.Chunk(Array.Empty<PskReporterEncoder.SenderRecord>(), 40));
    }

    // ---- WSPRnet ----------------------------------------------------------------

    private static WsprSpotBatch WsprBatch(params string[] messages)
    {
        var spots = new List<WsprSpot>();
        foreach (var m in messages)
            spots.Add(new WsprSpot(SnrDb: -20, DtSec: 0.0f, FreqMhz: 14.097061f, DriftHz: 0, Message: m));
        return new WsprSpotBatch(0, new DateTime(2026, 6, 26, 14, 0, 0, DateTimeKind.Utc), 14.0956, spots);
    }

    [Fact]
    public async Task Wsprnet_Disabled_Sends_Nothing()
    {
        var handler = new RecordingHandler();
        var spotting = NewSpotting(wsprEnabled: false, call: "K1ABC", grid: "FN42");
        using var r = new WsprnetReporter(NullLogger<WsprnetReporter>.Instance, spotting, handler);

        Assert.Equal(0, await r.HandleSpotsAsync(WsprBatch("W1AW FN31 37")));
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task Wsprnet_Enabled_Without_Identity_Sends_Nothing()
    {
        var handler = new RecordingHandler();
        var spotting = NewSpotting(wsprEnabled: true); // no call/grid
        using var r = new WsprnetReporter(NullLogger<WsprnetReporter>.Instance, spotting, handler);

        Assert.Equal(0, await r.HandleSpotsAsync(WsprBatch("W1AW FN31 37")));
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task Wsprnet_Enabled_With_Identity_Sends_One_Per_Spot()
    {
        var handler = new RecordingHandler();
        var spotting = NewSpotting(wsprEnabled: true, call: "K1ABC", grid: "FN42");
        using var r = new WsprnetReporter(NullLogger<WsprnetReporter>.Instance, spotting, handler);

        // Two valid spots + one hashed call (dropped) + one junk line (dropped).
        int sent = await r.HandleSpotsAsync(
            WsprBatch("W1AW FN31 37", "G0XYZ IO91 30", "<PJ4/K1ABC> FK52UD 33", "NOT A SPOT"));

        Assert.Equal(2, sent);
        Assert.Equal(2, handler.Count);
    }

    [Fact]
    public async Task Wsprnet_Logs_Heartbeat_On_Success()
    {
        var handler = new RecordingHandler(); // returns 200 OK for every POST
        var spotting = NewSpotting(wsprEnabled: true, call: "K1ABC", grid: "FN42");
        var log = new CapturingLogger<WsprnetReporter>();
        using var r = new WsprnetReporter(log, spotting, handler);

        int sent = await r.HandleSpotsAsync(WsprBatch("W1AW FN31 37", "G0XYZ IO91 30"));

        Assert.Equal(2, sent);
        Assert.Contains(log.Entries, e =>
            e.Level == LogLevel.Information &&
            e.Message.Contains("wsprnet.upload") &&
            e.Message.Contains("sent=2"));
    }

    [Fact]
    public async Task Wsprnet_Failed_Uploads_Count_Zero_And_Log_No_Heartbeat()
    {
        // Every POST returns 500: the survivors are still attempted (handler sees
        // them), but HandleSpotsAsync must report 0 accepted and emit NO Info
        // heartbeat (the heartbeat fires only when at least one spot landed).
        var handler = new StatusHandler(HttpStatusCode.InternalServerError);
        var spotting = NewSpotting(wsprEnabled: true, call: "K1ABC", grid: "FN42");
        var log = new CapturingLogger<WsprnetReporter>();
        using var r = new WsprnetReporter(log, spotting, handler);

        int sent = await r.HandleSpotsAsync(WsprBatch("W1AW FN31 37", "G0XYZ IO91 30"));

        Assert.Equal(0, sent);          // none accepted
        Assert.Equal(2, handler.Count); // but both were attempted
        Assert.DoesNotContain(log.Entries, e =>
            e.Level == LogLevel.Information && e.Message.Contains("wsprnet.upload"));
    }

    [Fact]
    public async Task Wsprnet_Partial_Failure_Counts_Only_Successes()
    {
        // First POST 200, second 500: exactly one accepted, both attempted, and a
        // single heartbeat for the one that landed.
        var handler = new SequenceHandler(HttpStatusCode.OK, HttpStatusCode.InternalServerError);
        var spotting = NewSpotting(wsprEnabled: true, call: "K1ABC", grid: "FN42");
        var log = new CapturingLogger<WsprnetReporter>();
        using var r = new WsprnetReporter(log, spotting, handler);

        int sent = await r.HandleSpotsAsync(WsprBatch("W1AW FN31 37", "G0XYZ IO91 30"));

        Assert.Equal(1, sent);
        Assert.Equal(2, handler.Count);
        Assert.Contains(log.Entries, e =>
            e.Level == LogLevel.Information &&
            e.Message.Contains("wsprnet.upload") &&
            e.Message.Contains("sent=1"));
    }

    // Minimal capturing logger so a success heartbeat can be asserted directly.
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly object _gate = new();
        private readonly List<(LogLevel Level, string Message)> _entries = new();

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get { lock (_gate) return _entries.ToArray(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_gate) _entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    // Always returns a fixed status (e.g. 500) so the failure-counting path runs.
    private sealed class StatusHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private int _count;
        public StatusHandler(HttpStatusCode status) => _status = status;
        public int Count => Volatile.Read(ref _count);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            return Task.FromResult(new HttpResponseMessage(_status));
        }
    }

    // Returns each supplied status in turn (last one repeats once exhausted), so a
    // mixed success/failure batch can be exercised.
    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode[] _statuses;
        private int _index = -1;
        private int _count;
        public SequenceHandler(params HttpStatusCode[] statuses) => _statuses = statuses;
        public int Count => Volatile.Read(ref _count);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            int i = Interlocked.Increment(ref _index);
            var status = _statuses[Math.Min(i, _statuses.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
