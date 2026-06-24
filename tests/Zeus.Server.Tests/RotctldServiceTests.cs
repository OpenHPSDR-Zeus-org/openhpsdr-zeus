// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Issue #917 — auto-route decision truth table + live rotctld wire-protocol
// behaviour, proven in-process with a scripted fake-rotctld TcpListener. The
// implementing team has NO rotator hardware; these tests retire the risks a
// bench would otherwise have to (RPRT-error desync, silent-peer _io
// starvation, P/S framing) without any hardware.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class RotctldServiceTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-rotsvc-{Guid.NewGuid():N}.db");
    private readonly List<IDisposable> _disposables = new();

    public void Dispose()
    {
        foreach (var d in _disposables) { try { d.Dispose(); } catch { /* ignore */ } }
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* ignore */ }
    }

    private RotctldService NewService(RotctldMultiConfig cfg)
    {
        var store = new RotctldConfigStore(NullLogger<RotctldConfigStore>.Instance, _dbPath);
        store.Set(cfg);
        var svc = new RotctldService(NullLogger<RotctldService>.Instance, store, radio: null);
        _disposables.Add(svc);   // dispose service before store
        _disposables.Add(store);
        return svc;
    }

    private static StateDto StateAt(long vfoHz, int txRxIndex = 0, long vfoBHz = 14_200_000) =>
        new StateDto(
            Status: ConnectionStatus.Connected,
            Endpoint: "test",
            VfoHz: vfoHz,
            Mode: RxMode.USB,
            FilterLowHz: 150,
            FilterHighHz: 2850,
            SampleRate: 192_000,
            TxReceiverIndex: txRxIndex,
            VfoBHz: vfoBHz);

    private static RotctldSlot Slot(int id, bool enabled, params string[] bands) =>
        new RotctldSlot(id, $"Rotator {id}", enabled, "127.0.0.1", 4533, bands, 100);

    // ---- RouteForState truth table (pure, no socket) -----------------------

    [Fact]
    public void RouteForState_AutoRouteOff_NeverRoutes()
    {
        var svc = NewService(new RotctldMultiConfig(1, AutoRoute: false,
            new[] { Slot(1, true, "20m"), Slot(2, true, "6m") }));
        Assert.Null(svc.RouteForState(StateAt(50_100_000)));
    }

    [Fact]
    public void RouteForState_CrossingIntoOtherSlotsBand_RoutesToIt()
    {
        var svc = NewService(new RotctldMultiConfig(1, AutoRoute: true,
            new[] { Slot(1, true, "20m"), Slot(2, true, "6m") }));
        var r = svc.RouteForState(StateAt(50_100_000)); // 6m
        Assert.NotNull(r);
        Assert.Equal(2, r!.Value.SlotId);
        Assert.Equal("6m", r.Value.Band);
    }

    [Fact]
    public void RouteForState_SameBandTwice_DedupesSecondCall()
    {
        var svc = NewService(new RotctldMultiConfig(1, AutoRoute: true,
            new[] { Slot(1, true, "20m"), Slot(2, true, "6m") }));
        Assert.NotNull(svc.RouteForState(StateAt(50_100_000)));
        Assert.Null(svc.RouteForState(StateAt(50_200_000))); // still 6m → no re-route
    }

    [Fact]
    public void RouteForState_AlreadyActiveSlotOwnsBand_NoOp()
    {
        var svc = NewService(new RotctldMultiConfig(2, AutoRoute: true,
            new[] { Slot(1, true, "20m"), Slot(2, true, "6m") }));
        Assert.Null(svc.RouteForState(StateAt(50_100_000))); // slot 2 already active
    }

    [Fact]
    public void RouteForState_OnlyDisabledSlotOwnsBand_DoesNotSwitch()
    {
        // Auto-routing onto a disabled slot would disconnect the working
        // rotator and silently park. We must leave the active slot alone.
        var svc = NewService(new RotctldMultiConfig(1, AutoRoute: true,
            new[] { Slot(1, true, "20m"), Slot(2, enabled: false, "6m") }));
        Assert.Null(svc.RouteForState(StateAt(50_100_000)));
    }

    [Fact]
    public void RouteForState_OutOfBand_NoOp()
    {
        var svc = NewService(new RotctldMultiConfig(1, AutoRoute: true,
            new[] { Slot(1, true, "20m"), Slot(2, true, "6m") }));
        Assert.Null(svc.RouteForState(StateAt(144_000_000))); // 2m → FreqToBand null
    }

    [Fact]
    public void RouteForState_TwoSlotsSameBand_FirstWins()
    {
        var svc = NewService(new RotctldMultiConfig(3, AutoRoute: true,
            new[] { Slot(1, true, "6m"), Slot(2, true, "6m"), Slot(3, true, "20m") }));
        var r = svc.RouteForState(StateAt(50_100_000)); // currently active = slot 3
        Assert.NotNull(r);
        Assert.Equal(1, r!.Value.SlotId);
    }

    [Fact]
    public void RouteForState_Split_FollowsTxVfoB()
    {
        // TX on VFO B (index 1) at 6m, VFO A on 20m → route by the TX band (6m).
        var svc = NewService(new RotctldMultiConfig(1, AutoRoute: true,
            new[] { Slot(1, true, "20m"), Slot(2, true, "6m") }));
        var r = svc.RouteForState(StateAt(14_100_000, txRxIndex: 1, vfoBHz: 50_100_000));
        Assert.NotNull(r);
        Assert.Equal(2, r!.Value.SlotId);
    }

    // ---- SetActiveSlot edge cases (no socket) ------------------------------

    [Fact]
    public async Task SetActiveSlot_UnknownId_KeepsActive_SetsError()
    {
        var svc = NewService(new RotctldMultiConfig(1, false, new[] { Slot(1, false, "20m") }));
        var status = await svc.SetActiveSlotAsync(7, CancellationToken.None);
        Assert.Equal(1, status.ActiveSlotId);
        Assert.Equal("unknown slot id 7", status.Error);
    }

    [Fact]
    public async Task SetActiveSlot_DisabledSlot_SwitchesButReportsDisabled()
    {
        var svc = NewService(new RotctldMultiConfig(1, false,
            new[] { Slot(1, true, "20m"), Slot(2, enabled: false, "6m") }));
        var status = await svc.SetActiveSlotAsync(2, CancellationToken.None);
        Assert.Equal(2, status.ActiveSlotId);
        Assert.False(status.Enabled);
    }

    // ---- Live wire protocol via fake rotctld -------------------------------

    [Fact]
    public async Task Poll_HappyPath_UpdatesCurrentAz()
    {
        await using var fake = new FakeRotctld(_ => "180.0\n0.0\n");
        var svc = NewService(new RotctldMultiConfig(1, false,
            new[] { new RotctldSlot(1, "R1", true, "127.0.0.1", fake.Port, new[] { "20m" }, 100) }));
        await svc.StartAsync(CancellationToken.None);
        try
        {
            var ok = await WaitUntil(() => svc.GetStatus().CurrentAz == 180.0, 5000);
            Assert.True(ok, "CurrentAz should reach 180.0 from the fake poll reply");
            Assert.True(svc.GetStatus().Connected);
        }
        finally { await svc.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Poll_SingleLineRprtError_DoesNotDesyncStream()
    {
        // First 'p' answers with a single 'RPRT -1' (error) line; subsequent
        // 'p' answer normally. The OLD two-line-read code would consume the
        // next poll's azimuth as the missing 'el' and desync forever; the fix
        // must keep the stream in sync so a later good poll still lands.
        int n = 0;
        await using var fake = new FakeRotctld(cmd =>
        {
            if (!cmd.StartsWith("p")) return "RPRT 0\n";
            return Interlocked.Increment(ref n) == 1 ? "RPRT -1\n" : "175.0\n0.0\n";
        });
        var svc = NewService(new RotctldMultiConfig(1, false,
            new[] { new RotctldSlot(1, "R1", true, "127.0.0.1", fake.Port, new[] { "20m" }, 100) }));
        await svc.StartAsync(CancellationToken.None);
        try
        {
            var ok = await WaitUntil(() => svc.GetStatus().CurrentAz == 175.0, 5000);
            Assert.True(ok, "a good poll after a single-line RPRT error must still update CurrentAz");
        }
        finally { await svc.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task SilentPeer_DoesNotWedge_IoForOtherCalls()
    {
        // Fake accepts but never replies. The poll read must time out and
        // release _io so a concurrent slot switch still completes — proving a
        // wedged-but-connected rotctld cannot freeze the whole subsystem.
        await using var fake = new FakeRotctld(_ => null); // never respond
        var svc = NewService(new RotctldMultiConfig(1, false, new[]
        {
            new RotctldSlot(1, "R1", true, "127.0.0.1", fake.Port, new[] { "20m" }, 100),
            new RotctldSlot(2, "R2", false, "127.0.0.1", fake.Port, new[] { "6m" }, 100),
        }));
        await svc.StartAsync(CancellationToken.None);
        try
        {
            // Let the poll loop connect and enter its (now bounded) read.
            await Task.Delay(300);
            var sw = Stopwatch.StartNew();
            var switchTask = svc.SetActiveSlotAsync(2, CancellationToken.None);
            var done = await Task.WhenAny(switchTask, Task.Delay(8000));
            sw.Stop();
            Assert.True(done == switchTask,
                $"SetActiveSlotAsync must not hang behind a silent poll read (waited {sw.ElapsedMilliseconds} ms)");
            Assert.Equal(2, (await switchTask).ActiveSlotId);
        }
        finally { await svc.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task SetAz_SendsExactCommand_AndParsesRprt()
    {
        string? seen = null;
        await using var fake = new FakeRotctld(cmd =>
        {
            if (cmd.StartsWith("P ")) { seen = cmd; return "RPRT 0\n"; }
            return "90.0\n0.0\n"; // poll replies
        });
        var svc = NewService(new RotctldMultiConfig(1, false,
            new[] { new RotctldSlot(1, "R1", true, "127.0.0.1", fake.Port, new[] { "20m" }, 100) }));
        await svc.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntil(() => svc.GetStatus().Connected, 5000);
            var status = await svc.SetAzAsync(270, CancellationToken.None);
            Assert.Equal(270.0, status.TargetAz);
            Assert.Null(status.Error);
            await WaitUntil(() => seen != null, 2000);
            Assert.Equal("P 270.00 0", seen);
        }
        finally { await svc.StopAsync(CancellationToken.None); }
    }

    private static async Task<bool> WaitUntil(Func<bool> cond, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond()) return true;
            await Task.Delay(25);
        }
        return cond();
    }

    // Scripted in-process rotctld: listens on an ephemeral loopback port and,
    // per received command line, replies with whatever the respond delegate
    // returns (null = stay silent). One reader per connection, so a simple
    // counter in the delegate is safe.
    private sealed class FakeRotctld : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Func<string, string?> _respond;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public FakeRotctld(Func<string, string?> respond)
        {
            _respond = respond;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _loop = AcceptLoop();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        private async Task AcceptLoop()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                    catch (OperationCanceledException) { break; }
                    catch (ObjectDisposedException) { break; }
                    _ = HandleClient(client);
                }
            }
            catch { /* listener torn down */ }
        }

        private async Task HandleClient(TcpClient client)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, Encoding.ASCII))
                await using (var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\n" })
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync(_cts.Token);
                        if (line == null) break;
                        var reply = _respond(line);
                        if (reply != null) await writer.WriteAsync(reply);
                    }
                }
            }
            catch { /* client gone or cancelled */ }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { /* ignore */ }
            try { await _loop; } catch { /* ignore */ }
            _cts.Dispose();
        }
    }
}
