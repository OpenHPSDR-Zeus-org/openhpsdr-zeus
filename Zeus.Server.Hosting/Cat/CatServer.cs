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
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using Zeus.Contracts;

namespace Zeus.Server.Cat;

/// <summary>
/// CAT (Computer Aided Transceiver) server. Accepts Kenwood TS-2000 ASCII
/// clients on a dedicated raw-TCP listener (default :19090). Spoken by loggers
/// (N1MM+, Log4OM), digital-mode apps (WSJT-X, JTDX, fldigi), and the Hamlib
/// <c>rigctl</c> bridge. Mirrors <see cref="Tci.TciServer"/>; the one structural
/// difference is that CAT owns its <see cref="TcpListener"/> accept loop (TCI's
/// accept loop is provided by Kestrel, which cannot serve a raw line protocol).
///
/// Control-only: unlike TCI there is no IQ / RX-audio / TX-chrono streaming, so
/// none of that machinery exists here. Unsolicited frames go only to clients
/// that enabled Auto-Information (AI1/AI2); a fresh connection is silent until
/// the client speaks, and CAT never auto-keys TX.
/// </summary>
public sealed class CatServer : IHostedService, IDisposable
{
    private readonly ILogger<CatServer> _log;
    private readonly CatOptions _options;
    private readonly RadioService _radio;
    private readonly TxService _tx;
    private readonly DspPipelineService _pipeline;
    private readonly ILoggerFactory _loggerFactory;

    private readonly ConcurrentDictionary<Guid, CatSession> _clients = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _acceptCts;
    private Task? _acceptTask;
    private bool _subscribed;

    // Latest RX signal level (dBm) for the SM (S-meter) poll. Stored as long
    // bits + Interlocked so a 32-bit runtime (Pi) can't read a torn double.
    private long _latestRxDbmBits = BitConverter.DoubleToInt64Bits(-130.0);

    public CatServer(
        IOptions<CatOptions> options,
        RadioService radio,
        TxService tx,
        DspPipelineService pipeline,
        ILoggerFactory loggerFactory)
    {
        _log = loggerFactory.CreateLogger<CatServer>();
        _options = options.Value;
        _radio = radio;
        _tx = tx;
        _pipeline = pipeline;
        _loggerFactory = loggerFactory;
    }

    public int ClientCount => _clients.Count;

    public double LatestRxDbm => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _latestRxDbmBits));

    public Task StartAsync(CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            _log.LogInformation("cat.disabled (set Cat:Enabled=true to enable)");
            return Task.CompletedTask;
        }

        IPAddress ip;
        if (_options.BindAddress is "0.0.0.0" or "*" or "")
            ip = IPAddress.Any;
        else if (string.Equals(_options.BindAddress, "localhost", StringComparison.OrdinalIgnoreCase))
            ip = IPAddress.Loopback;
        else if (!IPAddress.TryParse(_options.BindAddress, out ip!))
        {
            _log.LogWarning("cat.bind.invalid bind={Bind} — CAT not started", _options.BindAddress);
            return Task.CompletedTask;
        }

        try
        {
            _listener = new TcpListener(ip, _options.Port);
            _listener.Start();
        }
        catch (SocketException ex)
        {
            _log.LogWarning(ex, "cat.listen.failed bind={Bind} port={Port} — CAT not started",
                _options.BindAddress, _options.Port);
            _listener = null;
            return Task.CompletedTask;
        }

        _radio.StateChanged += OnRadioStateChanged;
        _radio.MoxChanged += OnMoxChanged;
        _pipeline.RxMeterUpdated += OnRxMeterUpdated;
        _subscribed = true;

        _acceptCts = new CancellationTokenSource();
        _acceptTask = AcceptLoopAsync(_acceptCts.Token);

        _log.LogInformation("cat.listening bind={Bind} port={Port}", _options.BindAddress, _options.Port);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_subscribed)
        {
            _radio.StateChanged -= OnRadioStateChanged;
            _radio.MoxChanged -= OnMoxChanged;
            _pipeline.RxMeterUpdated -= OnRxMeterUpdated;
            _subscribed = false;
        }

        _acceptCts?.Cancel();
        try { _listener?.Stop(); } catch { /* already torn down */ }

        _log.LogInformation("cat.stopping active={Count}", _clients.Count);
        foreach (var session in _clients.Values) session.Dispose();
        _clients.Clear();

        if (_acceptTask is not null)
        {
            try { await _acceptTask.WaitAsync(TimeSpan.FromSeconds(2), ct); }
            catch { /* accept loop drained or timed out */ }
        }
        _acceptCts?.Dispose();
        _acceptCts = null;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener!;
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) when (ct.IsCancellationRequested) { return; }
            catch (SocketException ex)
            {
                _log.LogDebug(ex, "cat.accept transient error");
                continue;
            }

            _ = HandleClientAsync(client, ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var sessionLog = _loggerFactory.CreateLogger<CatSession>();
        var session = new CatSession(id, client, sessionLog, _radio, _tx, _options, () => LatestRxDbm);
        _clients[id] = session;
        _log.LogInformation("cat.client.connected id={Id} total={Count}", id, _clients.Count);
        try
        {
            await session.RunAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "cat.client ended with error id={Id}", id);
        }
        finally
        {
            _clients.TryRemove(id, out _);
            session.Dispose();
            _log.LogInformation("cat.client.disconnected id={Id} total={Count}", id, _clients.Count);
        }
    }

    // --- Auto-Information pushes (AI1/AI2 sessions only) ---

    private void OnRadioStateChanged(StateDto state)
    {
        if (_clients.IsEmpty) return;
        // VFO can fire rapidly during tuning → rate-limit FA and IF.
        BroadcastRateLimited("FA", CatProtocol.Response("FA", CatProtocol.FormatFreq(state.VfoHz)));
        Broadcast(CatProtocol.Response("MD", CatProtocol.ModeDigit(state.Mode)));
        BroadcastRateLimited("IF",
            CatProtocol.Response("IF", CatProtocol.BuildIfBody(state.VfoHz, state.Mode, _tx.IsMoxOn, split: false)));
    }

    private void OnMoxChanged(bool moxOn)
    {
        if (_clients.IsEmpty) return;
        var state = _radio.Snapshot();
        // TX/RX transition is immediate (not rate-limited) so a logger sees the
        // edge promptly. IF carries the TX/RX bit.
        Broadcast(CatProtocol.Response("IF", CatProtocol.BuildIfBody(state.VfoHz, state.Mode, moxOn, split: false)));
    }

    private void OnRxMeterUpdated(int channelId, double dbm)
    {
        // RX1 only for the SM poll; just cache the latest level (no push — SM is
        // a polled command in the Kenwood protocol).
        if (channelId == 0)
            Interlocked.Exchange(ref _latestRxDbmBits, BitConverter.DoubleToInt64Bits(dbm));
    }

    private void Broadcast(string line)
    {
        foreach (var session in _clients.Values)
            if (session.AutoInfoEnabled) session.Send(line);
    }

    private void BroadcastRateLimited(string key, string line)
    {
        foreach (var session in _clients.Values)
            if (session.AutoInfoEnabled) session.SendRateLimited(key, line);
    }

    public void Dispose()
    {
        foreach (var session in _clients.Values) session.Dispose();
        _clients.Clear();
        _acceptCts?.Dispose();
    }
}
