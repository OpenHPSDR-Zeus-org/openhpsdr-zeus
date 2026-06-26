// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using Zeus.Server.Tci; // reuse TciRateLimiter (DRY — generic key/interval coalescer)

namespace Zeus.Server.Cat;

/// <summary>
/// Per-client CAT session over a raw TCP socket. Mirrors <see cref="TciSession"/>:
/// a send loop drains an outbound queue; a receive loop accumulates bytes until
/// a ';' terminator and hands each complete Kenwood command to a
/// <see cref="CatCommandHandler"/>. This class owns only the socket I/O and the
/// framing; the command logic (and its safety properties — no auto-key,
/// per-source MOX ownership) lives in the handler so it can be unit-tested
/// without a TCP connection.
/// </summary>
public sealed class CatSession : IDisposable
{
    // The longest legal Kenwood command is well under this; a token that grows
    // past it without a terminator is a misbehaving/abusive client → dropped.
    private const int MaxPendingChars = 256;

    private readonly Guid _id;
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly ILogger<CatSession> _log;
    private readonly TciRateLimiter _rateLimiter;
    private readonly CatCommandHandler _handler;

    private readonly ConcurrentQueue<string> _outbound = new();
    private readonly SemaphoreSlim _outboundSignal = new(0);
    private int _disposed;

    public CatSession(
        Guid id,
        TcpClient client,
        ILogger<CatSession> log,
        RadioService radio,
        TxService tx,
        CatOptions options,
        Func<double> latestRxDbm)
    {
        _id = id;
        _client = client;
        _stream = client.GetStream();
        _log = log;
        _rateLimiter = new TciRateLimiter(options.RateLimitMs, Send);
        _handler = new CatCommandHandler(radio, tx, options, latestRxDbm, Send);
    }

    /// <summary>True once the client has issued AI1/AI2 — gates unsolicited
    /// state-change pushes the server broadcasts.</summary>
    public bool AutoInfoEnabled => _handler.AutoInfoEnabled;

    public async Task RunAsync(CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var sendTask = SendLoopAsync(linkedCts.Token);
            var recvTask = ReceiveLoopAsync(linkedCts.Token);
            await Task.WhenAny(sendTask, recvTask);
            linkedCts.Cancel();
            try { await Task.WhenAll(sendTask, recvTask); } catch { /* drained */ }
        }
        finally
        {
            _rateLimiter.Dispose();
        }
    }

    /// <summary>Enqueue a terminated CAT response for sending.</summary>
    public void Send(string line)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _outbound.Enqueue(line);
        try { _outboundSignal.Release(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Enqueue a rate-limited (coalesced-by-key) push, e.g. FA during a
    /// VFO spin. Bursts collapse to one send per RateLimitMs.</summary>
    public void SendRateLimited(string key, string line) => _rateLimiter.Enqueue(key, line);

    private async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _outboundSignal.WaitAsync(ct);
                while (_outbound.TryDequeue(out var line))
                {
                    var bytes = Encoding.ASCII.GetBytes(line);
                    await _stream.WriteAsync(bytes, ct);
                    await _stream.FlushAsync(ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException ex) { _log.LogDebug(ex, "cat send loop ended id={Id}", _id); }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { _log.LogDebug(ex, "cat send loop failed id={Id}", _id); }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buf = new byte[2048];
        var acc = new StringBuilder();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await _stream.ReadAsync(buf, ct);
                if (n == 0) return; // peer closed

                acc.Append(Encoding.ASCII.GetString(buf, 0, n));
                var (commands, remainder) = CatProtocol.ExtractCommands(acc.ToString());
                acc.Clear();
                // Bound an un-terminated command so a client that never sends ';'
                // can't grow the buffer without limit.
                acc.Append(remainder.Length > MaxPendingChars ? string.Empty : remainder);

                foreach (var token in commands)
                {
                    try { _handler.Dispatch(token); }
                    catch (Exception ex) { _log.LogDebug(ex, "cat dispatch error id={Id} token={Token}", _id, token); }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException ex) { _log.LogDebug(ex, "cat recv loop ended id={Id}", _id); }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { _log.LogDebug(ex, "cat recv loop failed id={Id}", _id); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _outboundSignal.Release(); } catch { }
        try { _stream.Dispose(); } catch { }
        try { _client.Dispose(); } catch { }
        _outboundSignal.Dispose();
    }
}
