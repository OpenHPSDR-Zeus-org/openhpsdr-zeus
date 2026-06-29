// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// End-to-end wiring test for the hardware PTT-IN → MOX path on a Protocol-2
// radio (G2 / G2-Ultra / OrionMkII class). Unlike ExternalPttServiceTests —
// which drives the shared engine through ExternalPttService's own test seams —
// this exercises the REAL event chain:
//
//   ExternalPttService.StartAsync()                  (subscribes to P2Connected)
//     → RadioService.MarkProtocol2Connected(client)  (fires P2Connected, the
//                                                      production connect path)
//       → OnP2Connected: client.TelemetryReceived += OnP2Telemetry
//         → Protocol2Client.RaiseHiPriStatusForTest() (same decode+dispatch the
//                                                       UDP-1025 RX loop runs)
//           → OnP2Telemetry → MOX
//
// The only stubbed link is the UDP socket transport (covered separately by
// Protocol2 decode tests + the P1 loopback event test). Critically, the gate is
// left at its DEFAULT — no PttSettingsStore.Set() — so this also proves the
// footswitch keys MOX out of the box on a fresh install (default ON).

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Protocol2;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class HardwarePttEndToEndTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-ptt-e2e-{Guid.NewGuid():N}.db");
    private readonly string _pttDbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-ptt-e2e-ptt-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var p in new[] { _dbPath, _dbPath + ".pa", _pttDbPath })
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { }
        }
    }

    private sealed class Rig : IDisposable
    {
        public RadioService Radio { get; }
        public TxService Tx { get; }
        public PttSettingsStore Settings { get; }
        public ExternalPttService Service { get; }
        public Protocol2Client Client { get; }
        private readonly DspSettingsStore _dspStore;
        private readonly PaSettingsStore _paStore;

        public Rig(string dbPath, string pttDbPath)
        {
            var lf = NullLoggerFactory.Instance;
            _dspStore = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, dbPath);
            _paStore = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, dbPath + ".pa");
            var dspStore = _dspStore;
            var paStore = _paStore;
            Radio = new RadioService(lf, dspStore, paStore);
            var hub = new StreamingHub(new NullLogger<StreamingHub>());
            var pipeline = new DspPipelineService(Radio, hub, Array.Empty<IRxAudioSink>(), lf);
            Tx = new TxService(Radio, pipeline, hub, NullBandPlanService.Instance, new NullLogger<TxService>());
            // NOTE: no Settings.Set(...) — exercise the power-on DEFAULT.
            Settings = new PttSettingsStore(NullLogger<PttSettingsStore>.Instance, pttDbPath);
            Service = new ExternalPttService(Radio, Tx, hub, Settings, new NullLogger<ExternalPttService>());
            Client = new Protocol2Client(NullLogger<Protocol2Client>.Instance);
        }

        // Wire the service to RadioService events, then run the production P2
        // connect path with the live client so P2Connected → OnP2Connected
        // subscribes the telemetry handler.
        public void StartAndConnect()
        {
            Service.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            Radio.MarkProtocol2Connected("127.0.0.1:1024", 48_000, Client, HpsdrBoardKind.OrionMkII);
        }

        public void Dispose()
        {
            try { Service.StopAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { }
            Client.Dispose();
            Service.Dispose();
            // Close the RadioService and the LiteDB-backed stores (releasing
            // their shared-engine leases) BEFORE the test class deletes the temp
            // prefs files, so the engines flush and close instead of being
            // deleted out from under an open handle.
            Radio.Dispose();
            Settings.Dispose();
            _paStore.Dispose();
            _dspStore.Dispose();
        }
    }

    // Hi-priority status body (the bytes AFTER the 4-byte sequence header).
    // byte 0: bit0 PTT, bit1 Dot, bit2 Dash, bit4 PLL-locked.
    private static byte[] HiPriBody(bool pttIn) =>
        BuildBody((byte)((pttIn ? 0x01 : 0x00) | 0x10)); // PLL always locked

    private static byte[] BuildBody(byte byte0)
    {
        var body = new byte[56];
        body[0] = byte0;
        return body;
    }

    private static bool WaitFor(Func<bool> cond, int timeoutMs = 2000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond()) return true;
            Thread.Sleep(5);
        }
        return cond();
    }

    [Fact]
    public void DefaultGate_FootswitchPress_KeysMox_ThroughLiveP2Wiring()
    {
        using var rig = new Rig(_dbPath, _pttDbPath);
        rig.StartAndConnect();

        // Sanity: the gate is at its untouched power-on default.
        Assert.True(rig.Settings.Get(), "fresh-install default must be ON (Thetis-faithful)");

        // Exercise the CW hang explicitly: only CW modes hold MOX past the
        // falling edge (issue #870 — voice/data modes drop on the edge). Without
        // this the radio sits in the default USB voice mode, where the release
        // fires immediately on a background Task.Run and the "MOX holds" assert
        // below becomes a race against that task (it lost on a slow runner).
        rig.Radio.SetMode(RxMode.CWU);

        // Footswitch down: a real hi-pri status packet with PTT bit set, run
        // through the live Protocol2Client → RadioService → ExternalPttService
        // chain (no test seam on the service itself).
        rig.Client.RaiseHiPriStatusForTest(HiPriBody(pttIn: true));

        Assert.True(WaitFor(() => rig.Tx.IsMoxOn), "footswitch press did not key MOX e2e");
        Assert.Equal(MoxSource.Hardware, rig.Tx.MoxOwner);
        Assert.True(rig.Service.IsKeyed);

        // Footswitch up: in CW the falling edge arms the 250 ms hang timer
        // (synchronous on the RX thread) rather than releasing, so MOX is
        // deterministically still up here, then releases after the hang.
        rig.Client.RaiseHiPriStatusForTest(HiPriBody(pttIn: false));
        Assert.True(rig.Tx.IsMoxOn, "MOX dropped before the hang elapsed");
        Assert.False(rig.Service.IsKeyed); // lamp tracks the raw input immediately

        Assert.True(WaitFor(() => !rig.Tx.IsMoxOn), "MOX never released after the hang");
        Assert.Null(rig.Tx.MoxOwner);
    }

    [Fact]
    public void GateOff_FootswitchPress_DoesNotKey_ButLampTracks_ThroughLiveP2Wiring()
    {
        using var rig = new Rig(_dbPath, _pttDbPath);
        rig.Settings.Set(false); // operator chose UI-only keying
        rig.StartAndConnect();

        rig.Client.RaiseHiPriStatusForTest(HiPriBody(pttIn: true));
        Thread.Sleep(60);

        Assert.False(rig.Tx.IsMoxOn);     // no promotion when the gate is off…
        Assert.True(rig.Service.IsKeyed); // …but the lamp still tracks the pedal
    }
}
