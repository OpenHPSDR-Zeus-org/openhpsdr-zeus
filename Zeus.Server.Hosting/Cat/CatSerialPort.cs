// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.IO.Ports;
using System.Text;
using Zeus.Server.Tci; // reuse TciRateLimiter (DRY — generic key/interval coalescer)

namespace Zeus.Server.Cat;

/// <summary>
/// One live serial CAT connection (a Thetis CAT1–4 port). The serial analogue
/// of <see cref="CatSession"/>: it owns ONLY the serial I/O and the ';' framing;
/// every command goes to a <see cref="CatCommandHandler"/>, so the safety
/// contract (no auto-key, per-source MOX ownership) and the full Tier-1 command
/// surface are shared byte-for-byte with the TCP path — no protocol is
/// duplicated here.
///
/// <para>Deliberately constructible standalone (device path + serial params)
/// so an integration test can drive it over a socat pty pair with no
/// <see cref="CatSerialService"/> / DI in the way.</para>
///
/// <para>Read loop: blocking-free <c>BaseStream.ReadAsync</c> under the caller's
/// cancellation token, exactly as <see cref="CatSession"/> and the shipped
/// <c>G2FrontPanelService</c> do. Because <c>SerialPort.BaseStream.ReadAsync</c>
/// has historically been unreliable about honouring its cancellation token,
/// <see cref="Dispose"/> also closes the port, which force-unblocks any
/// in-flight read — so a settings change or shutdown always tears the loop down
/// promptly.</para>
/// </summary>
internal sealed class CatSerialPort : IDisposable
{
    // Longest legal Kenwood command is well under this; a token that grows past
    // it without a ';' is a misbehaving client → its buffer is dropped.
    private const int MaxPendingChars = 256;

    private readonly string _path;
    private readonly int _baud;
    private readonly Parity _parity;
    private readonly int _dataBits;
    private readonly StopBits _stopBits;
    private readonly ILogger _log;

    private readonly TciRateLimiter _rateLimiter;
    private readonly CatCommandHandler _handler;
    private readonly object _writeLock = new();

    private SerialPort? _port;
    private int _disposed;
    private int _activity;

    public CatSerialPort(
        string path, int baud, Parity parity, int dataBits, StopBits stopBits,
        RadioService radio, TxService tx, CatOptions options,
        Func<double> latestRxDbm, ILogger log)
    {
        _path = path;
        _baud = baud;
        _parity = parity;
        _dataBits = dataBits;
        _stopBits = stopBits;
        _log = log;
        _rateLimiter = new TciRateLimiter(options.RateLimitMs, Send);
        _handler = new CatCommandHandler(radio, tx, options, latestRxDbm, Send);
    }

    /// <summary>True once the connected client issued AI1/AI2 — gates the
    /// unsolicited state-change pushes <see cref="CatSerialService"/> fans out.</summary>
    public bool AutoInfoEnabled => _handler.AutoInfoEnabled;

    /// <summary>Count of commands dispatched on this port (a cheap "is something
    /// talking to me" signal for the status panel).</summary>
    public int Activity => Volatile.Read(ref _activity);

    public bool IsOpen => _port?.IsOpen ?? false;

    /// <summary>Open the serial device. Throws on failure (port busy, missing,
    /// permission) — the caller logs and schedules a reconnect.</summary>
    public void Open()
    {
        var port = new SerialPort(_path, _baud, _parity, _dataBits, _stopBits)
        {
            Handshake = Handshake.None,
            // Finite timeouts: a stuck line must never wedge the read/write path.
            ReadTimeout = 500,
            WriteTimeout = 500,
            Encoding = Encoding.ASCII,
        };
        port.Open();
        // Assert RTS/DTR after open (Thetis's "soft rock ptt" hack — some
        // level-shifter interfaces need a line high). Best-effort: a virtual
        // pty that doesn't model line control must not fail the open.
        try { port.RtsEnable = true; } catch { /* line control unsupported */ }
        try { port.DtrEnable = true; } catch { /* line control unsupported */ }
        _port = port;
    }

    /// <summary>Read loop until cancelled or the port goes away. Frames on ';'
    /// via <see cref="CatProtocol.ExtractCommands"/> and dispatches each command
    /// through the shared handler.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var port = _port ?? throw new InvalidOperationException("Open() must precede RunAsync()");
        // SerialPort.BaseStream.ReadAsync has historically ignored its
        // cancellation token, which would wedge a settings-change/shutdown
        // teardown (the read never returns, so the caller's finally that would
        // dispose the port never runs). Closing the port force-unblocks the
        // in-flight read regardless. This callback is the load-bearing teardown
        // path; do not remove it.
        //
        // Offload the Close to the thread pool: CancellationTokenSource.Cancel()
        // runs registrations SYNCHRONOUSLY on the canceller's thread (the HTTP
        // PUT thread, or the host-shutdown thread), and SerialPort.Close() can
        // block on a surprise-removed USB adapter. Queuing it keeps the canceller
        // responsive while still unblocking the read promptly.
        await using var reg = ct.Register(() =>
            ThreadPool.QueueUserWorkItem(_ => { try { port.Close(); } catch { /* already torn down */ } }));
        var stream = port.BaseStream;
        var buf = new byte[2048];
        var acc = new StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            int n;
            try
            {
                n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct);
            }
            catch (TimeoutException) { continue; }

            if (n <= 0) { await Task.Delay(20, ct); continue; }

            acc.Append(Encoding.ASCII.GetString(buf, 0, n));
            var (commands, remainder) = CatProtocol.ExtractCommands(acc.ToString());
            acc.Clear();
            // Bound an un-terminated command so a client that never sends ';'
            // can't grow the buffer without limit.
            acc.Append(remainder.Length > MaxPendingChars ? string.Empty : remainder);

            foreach (var token in commands)
            {
                Interlocked.Increment(ref _activity);
                try { _handler.Dispatch(token); }
                catch (Exception ex) { _log.LogDebug(ex, "cat.serial dispatch error port={Port} token={Token}", _path, token); }
            }
        }
    }

    /// <summary>Write a terminated CAT response to the port. Synchronous under a
    /// lock (responses are tiny); mirrors G2FrontPanelService.Send. Called from
    /// the read loop (command replies) and the rate-limiter timer (AI pushes).</summary>
    public void Send(string line)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var port = _port;
        if (port is null || !port.IsOpen) return;
        try
        {
            lock (_writeLock)
            {
                if (port.IsOpen) port.Write(line);
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "cat.serial write failed port={Port}", _path);
        }
    }

    /// <summary>Enqueue a rate-limited (coalesced-by-key) push, e.g. FA during a
    /// VFO spin. Bursts collapse to one send per RateLimitMs.</summary>
    public void SendRateLimited(string key, string line) => _rateLimiter.Enqueue(key, line);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _rateLimiter.Dispose();
        var port = _port;
        _port = null;
        if (port is null) return;
        // Closing force-unblocks an in-flight BaseStream.ReadAsync (belt-and-
        // suspenders for the unreliable serial cancellation token).
        try { if (port.IsOpen) port.Close(); } catch { /* already torn down */ }
        port.Dispose();
    }
}
