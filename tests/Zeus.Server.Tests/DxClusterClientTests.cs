// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// DxClusterClient session test driven by a FAKE in-memory duplex stream — NO
// real sockets (a real ConnectAsync in a test loop minidump-crashes the Windows
// CI host). We feed banner → login prompt → spot through the read side and assert
// (a) the callsign was written back and (b) a parsed spot reached the existing
// SpotManager ingest seam.

using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server.DxCluster;
using Zeus.Server.Tci;

namespace Zeus.Server.Tests;

public class DxClusterClientTests
{
    [Fact]
    public async Task Session_LoginAndSpot_ReachesIngestSeam()
    {
        var spots = new SpotManager();
        var client = new DxClusterClient(NullLogger<DxClusterClient>.Instance, spots, connector: null);
        var stream = new FakeDuplexStream();
        var cfg = new DxClusterConfig(
            Enabled: true, Host: "fake", Port: 7373, Callsign: "K1ABC");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var session = client.RunSessionAsync(cfg, stream, cts.Token);

        // Server banner, then a login prompt, then a spot line.
        stream.ServerSend("Welcome to the DX cluster\r\n");
        stream.ServerSend("login: ");
        stream.ServerSend("\r\n");
        stream.ServerSend("DX de W3LPL:    14074.0  K1XYZ   FT8  -12 dB   1432Z\r\n");

        // Wait for the spot to land in the SpotManager.
        await WaitForAsync(() => spots.GetAll().Length > 0, TimeSpan.FromSeconds(5));

        var all = spots.GetAll();
        var spot = Assert.Single(all);
        Assert.Equal("K1XYZ", spot.Callsign);
        Assert.Equal("FT8", spot.Mode);
        Assert.Equal(14074000L, spot.FreqHz);
        Assert.Equal(1, client.SpotsReceived);
        Assert.Equal("K1XYZ", client.LastSpotCallsign);

        // The callsign must have been written back to the cluster.
        await WaitForAsync(() => stream.WrittenText().Contains("K1ABC"), TimeSpan.FromSeconds(5));
        Assert.Contains("K1ABC\r\n", stream.WrittenText());

        // End the session cleanly (EOF) and confirm the task completes.
        stream.ServerClose();
        await session;
    }

    [Fact]
    public async Task Session_PasswordPrompt_SendsConfiguredPassword()
    {
        var spots = new SpotManager();
        var client = new DxClusterClient(NullLogger<DxClusterClient>.Instance, spots, connector: null);
        var stream = new FakeDuplexStream();
        var cfg = new DxClusterConfig(
            Enabled: true, Host: "fake", Port: 7373, Callsign: "K1ABC", Password: "topsecret");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var session = client.RunSessionAsync(cfg, stream, cts.Token);

        stream.ServerSend("login: \r\n");
        await WaitForAsync(() => stream.WrittenText().Contains("K1ABC"), TimeSpan.FromSeconds(5));
        stream.ServerSend("password: \r\n");
        await WaitForAsync(() => stream.WrittenText().Contains("topsecret"), TimeSpan.FromSeconds(5));

        Assert.Contains("K1ABC\r\n", stream.WrittenText());
        Assert.Contains("topsecret\r\n", stream.WrittenText());

        stream.ServerClose();
        await session;
    }

    [Fact]
    public async Task Session_TelnetIacBytes_StrippedBeforeParse()
    {
        var spots = new SpotManager();
        var client = new DxClusterClient(NullLogger<DxClusterClient>.Instance, spots, connector: null);
        var stream = new FakeDuplexStream();
        var cfg = new DxClusterConfig(Enabled: true, Host: "fake", Port: 7373, Callsign: "K1ABC");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var session = client.RunSessionAsync(cfg, stream, cts.Token);

        // Prepend a Telnet IAC DO ECHO (0xFF 0xFD 0x01) negotiation before the
        // spot — it must be stripped so the spot still parses.
        var iac = new byte[] { 0xFF, 0xFD, 0x01 };
        stream.ServerSendBytes(iac);
        stream.ServerSend("DX de G3XYZ:   7025.0  DL1AB   CW   0901Z\r\n");

        await WaitForAsync(() => spots.GetAll().Length > 0, TimeSpan.FromSeconds(5));
        var spot = Assert.Single(spots.GetAll());
        Assert.Equal("DL1AB", spot.Callsign);

        stream.ServerClose();
        await session;
    }

    [Fact]
    public async Task Session_EofImmediately_CompletesWithoutSpots()
    {
        var spots = new SpotManager();
        var client = new DxClusterClient(NullLogger<DxClusterClient>.Instance, spots, connector: null);
        var stream = new FakeDuplexStream();
        var cfg = new DxClusterConfig(Enabled: true, Host: "fake", Port: 7373, Callsign: "K1ABC");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var session = client.RunSessionAsync(cfg, stream, cts.Token);
        stream.ServerClose();
        await session; // returns on EOF, no exception
        Assert.Empty(spots.GetAll());
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        if (!condition())
            throw new TimeoutException("condition not met within timeout");
    }

    // In-memory bidirectional stream. The "server" pushes bytes into the read
    // side via ServerSend/ServerSendBytes/ServerClose; everything the client
    // writes is captured for assertions. No sockets, deterministic.
    private sealed class FakeDuplexStream : Stream
    {
        private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
        private byte[] _leftover = Array.Empty<byte>();
        private int _leftoverPos;
        private readonly List<byte> _written = new();
        private readonly object _writeSync = new();

        public void ServerSend(string s) => ServerSendBytes(Encoding.Latin1.GetBytes(s));
        public void ServerSendBytes(byte[] bytes) => _incoming.Writer.TryWrite(bytes);
        public void ServerClose() => _incoming.Writer.TryComplete();

        public string WrittenText()
        {
            lock (_writeSync) return Encoding.Latin1.GetString(_written.ToArray());
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_leftoverPos >= _leftover.Length)
            {
                try
                {
                    if (!await _incoming.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                        return 0; // completed → EOF
                }
                catch (ChannelClosedException)
                {
                    return 0;
                }
                if (!_incoming.Reader.TryRead(out var chunk) || chunk is null)
                    return 0;
                _leftover = chunk;
                _leftoverPos = 0;
                if (_leftover.Length == 0)
                    return 0;
            }

            int n = Math.Min(buffer.Length, _leftover.Length - _leftoverPos);
            _leftover.AsSpan(_leftoverPos, n).CopyTo(buffer.Span);
            _leftoverPos += n;
            return n;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            lock (_writeSync) _written.AddRange(buffer.ToArray());
            return ValueTask.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
