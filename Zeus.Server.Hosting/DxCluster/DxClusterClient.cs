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
using System.Text;
using Microsoft.Extensions.Logging;
using Zeus.Contracts;
using Zeus.Server.Tci;

namespace Zeus.Server.DxCluster;

/// <summary>
/// Connects to a Telnet DX cluster, logs in, and feeds received spots into the
/// existing <see cref="SpotManager"/> ingest path (the same path TCI spots use →
/// SpotBroadcastService → SpotOverlay on the panadapter). No parallel pipeline.
///
/// <para>The socket sits behind an injectable seam (<see cref="_connector"/>):
/// real TCP in production, a fake in-memory duplex stream in tests. No unit test
/// ever opens a real socket. A single session over a given stream is driven by
/// <see cref="RunSessionAsync"/>, which is what tests call directly.</para>
///
/// <para>The connect/backoff loop ((<see cref="Start"/>/<see cref="Stop"/>) owns
/// a CancellationTokenSource and a single Task; nothing is left running past
/// <see cref="Stop"/> or <see cref="Dispose"/>.</para>
/// </summary>
public sealed class DxClusterClient : IDisposable
{
    // Default spot tick/label colour: Zeus accent blue (#4A9EFF). Packed ARGB with
    // A=0, which SpotOverlay treats as fully opaque (see argbToRgba). This is a
    // numeric wire value handed to the existing overlay, not a CSS token.
    private const uint DefaultSpotArgb = 0x004A9EFFu;

    private static readonly Encoding Latin1 = Encoding.Latin1;
    private const int MaxLineLength = 4096;

    private readonly ILogger _log;
    private readonly SpotManager _spots;
    private readonly Func<string, int, CancellationToken, Task<Stream>> _connector;

    private readonly object _sync = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private volatile DxClusterConnectionState _state = DxClusterConnectionState.Disconnected;
    private int _spotsReceived;
    private string? _lastSpotCallsign;
    private volatile string? _lastError;

    public DxClusterClient(ILogger<DxClusterClient> log, SpotManager spots)
        : this(log, spots, connector: null)
    {
    }

    // Test/seam ctor: a null connector wires the production TCP connector.
    internal DxClusterClient(
        ILogger log,
        SpotManager spots,
        Func<string, int, CancellationToken, Task<Stream>>? connector)
    {
        _log = log;
        _spots = spots;
        _connector = connector ?? DefaultTcpConnector;
    }

    public DxClusterConnectionState State => _state;
    public int SpotsReceived => Volatile.Read(ref _spotsReceived);
    public string? LastSpotCallsign => Volatile.Read(ref _lastSpotCallsign);
    public string? LastError => _lastError;
    public bool IsRunning { get { lock (_sync) return _loop is { IsCompleted: false }; } }

    /// <summary>Start the connect/backoff loop for the given config. Idempotent —
    /// a second Start while running is a no-op.</summary>
    public void Start(DxClusterConfig cfg)
    {
        lock (_sync)
        {
            if (_loop is { IsCompleted: false })
                return;
            _lastError = null;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _loop = Task.Run(() => RunLoopAsync(cfg, token), CancellationToken.None);
        }
    }

    /// <summary>Stop the loop and wait for it to unwind. Safe to call when idle.</summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_sync)
        {
            cts = _cts;
            loop = _loop;
            _cts = null;
            _loop = null;
        }

        if (cts is null)
            return;

        try { cts.Cancel(); } catch { /* already disposed */ }
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.LogDebug(ex, "dxcluster.loop ended with exception"); }
        }
        cts.Dispose();
        _state = DxClusterConnectionState.Disconnected;
    }

    private async Task RunLoopAsync(DxClusterConfig cfg, CancellationToken ct)
    {
        // Bounded exponential backoff: 2s → 60s, reset after a healthy session.
        var backoff = TimeSpan.FromSeconds(2);
        var maxBackoff = TimeSpan.FromSeconds(60);

        while (!ct.IsCancellationRequested)
        {
            Stream? stream = null;
            try
            {
                _state = DxClusterConnectionState.Connecting;
                _log.LogInformation("dxcluster.connect host={Host} port={Port}", cfg.Host, cfg.Port);
                stream = await _connector(cfg.Host, cfg.Port, ct).ConfigureAwait(false);

                _state = DxClusterConnectionState.Connected;
                _lastError = null;
                backoff = TimeSpan.FromSeconds(2);

                await RunSessionAsync(cfg, stream, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _log.LogWarning("dxcluster.session error: {Msg}", ex.Message);
            }
            finally
            {
                stream?.Dispose();
            }

            if (ct.IsCancellationRequested)
                break;

            _state = DxClusterConnectionState.Reconnecting;
            try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            backoff = TimeSpan.FromMilliseconds(Math.Min(maxBackoff.TotalMilliseconds, backoff.TotalMilliseconds * 2));
        }

        _state = DxClusterConnectionState.Disconnected;
    }

    /// <summary>
    /// Drive one session over an already-connected duplex stream: run the login
    /// handshake, strip Telnet IAC bytes, split newline-delimited lines, parse
    /// spots, and ingest them. Returns when the stream ends or is cancelled.
    /// Tests call this directly with a fake in-memory duplex stream.
    /// </summary>
    internal async Task RunSessionAsync(DxClusterConfig cfg, Stream stream, CancellationToken ct)
    {
        var handshake = new DxClusterHandshake(cfg.Callsign, cfg.Password, SplitLoginCommands(cfg.LoginCommands));
        var filter = new TelnetByteFilter();
        var readBuf = new byte[4096];
        var filtered = new List<byte>(4096);
        var lineBuf = new List<byte>(256);

        while (!ct.IsCancellationRequested)
        {
            int n = await stream.ReadAsync(readBuf.AsMemory(0, readBuf.Length), ct).ConfigureAwait(false);
            if (n <= 0)
                break; // EOF — peer closed

            filtered.Clear();
            filter.Process(readBuf, n, filtered);

            foreach (var b in filtered)
            {
                if (b == (byte)'\n')
                {
                    await EmitLineAsync(lineBuf, handshake, stream, ct).ConfigureAwait(false);
                    lineBuf.Clear();
                }
                else if (b == (byte)'\r')
                {
                    // ignore CR; line terminates on LF
                }
                else
                {
                    if (lineBuf.Count < MaxLineLength)
                        lineBuf.Add(b);
                    // Some clusters print a non-newline-terminated prompt ("login: ").
                    // Treat a trailing-space prompt opportunistically: see EmitPromptProbe.
                }
            }

            // A login/password prompt may arrive WITHOUT a trailing newline. After
            // draining a chunk, probe the pending partial line for a prompt so the
            // handshake doesn't stall waiting for a LF that never comes.
            await ProbePromptAsync(lineBuf, handshake, stream, ct).ConfigureAwait(false);
        }
    }

    private async Task EmitLineAsync(List<byte> lineBytes, DxClusterHandshake handshake, Stream stream, CancellationToken ct)
    {
        var line = Latin1.GetString(lineBytes.ToArray()).Trim();
        if (line.Length == 0)
            return;

        var replies = handshake.OnLine(line);
        await SendRepliesAsync(replies, stream, ct).ConfigureAwait(false);

        if (DxSpotLineParser.TryParse(line, out var spot))
        {
            _spots.AddSpot(spot.DxCall, spot.Mode, spot.FreqHz, DefaultSpotArgb,
                string.IsNullOrEmpty(spot.Comment) ? null : spot.Comment);
            Interlocked.Increment(ref _spotsReceived);
            Volatile.Write(ref _lastSpotCallsign, spot.DxCall);
            _log.LogDebug("dxcluster.spot {Call} {Mode} {Freq}Hz", spot.DxCall, spot.Mode, spot.FreqHz);
        }
    }

    // Probe a not-yet-terminated partial line for a login/password prompt. Only
    // sends when the handshake actually emits a reply, so non-prompt partial text
    // is left to accumulate until its newline.
    private async Task ProbePromptAsync(List<byte> lineBytes, DxClusterHandshake handshake, Stream stream, CancellationToken ct)
    {
        if (lineBytes.Count == 0)
            return;
        var partial = Latin1.GetString(lineBytes.ToArray());
        var lower = partial.ToLowerInvariant();
        // Cheap pre-filter: only invoke the handshake for prompt-shaped partials.
        if (!(lower.Contains("login") || lower.Contains("call") || lower.Contains("password")))
            return;
        var replies = handshake.OnLine(partial.Trim());
        if (replies.Count > 0)
        {
            await SendRepliesAsync(replies, stream, ct).ConfigureAwait(false);
            lineBytes.Clear(); // consumed as a prompt; don't double-feed at LF
        }
    }

    private static async Task SendRepliesAsync(IReadOnlyList<string> replies, Stream stream, CancellationToken ct)
    {
        if (replies.Count == 0)
            return;
        foreach (var r in replies)
        {
            var bytes = Latin1.GetBytes(r + "\r\n");
            await stream.WriteAsync(bytes.AsMemory(), ct).ConfigureAwait(false);
        }
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Split the newline-separated login-commands blob into trimmed,
    /// non-empty commands. Pure helper, shared with the handshake.</summary>
    public static IReadOnlyList<string> SplitLoginCommands(string? blob)
    {
        if (string.IsNullOrWhiteSpace(blob))
            return Array.Empty<string>();
        return blob
            .Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static async Task<Stream> DefaultTcpConnector(string host, int port, CancellationToken ct)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);
            // The TcpClient owns the socket; the returned stream's Dispose (called
            // by the loop's finally) tears the connection down. Keep a reference so
            // disposing the stream also disposes the client.
            return new TcpClientStream(tcp);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        try { StopAsync().GetAwaiter().GetResult(); } catch { /* best effort */ }
    }

    // Wraps a TcpClient's NetworkStream so disposing the stream also disposes the
    // owning client (and thus the socket). Keeps the production connector to a
    // single Stream return type that matches the test seam.
    private sealed class TcpClientStream : Stream
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _inner;

        public TcpClientStream(TcpClient client)
        {
            _client = client;
            _inner = client.GetStream();
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _inner.ReadAsync(buffer, ct);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => _inner.WriteAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _client.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
