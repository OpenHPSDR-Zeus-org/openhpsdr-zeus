// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Net;
using System.Net.Sockets;
using Zeus.Contracts;
using Zeus.Protocol1;
using Zeus.Server;
using Zeus.VirtualRadio;
using Zeus.VirtualRadio.P1;

namespace Zeus.VirtualRadio.Tests;

/// <summary>
/// Phase-0 scaffold sanity: the frozen shared types compile and the profile
/// factory enforces board/protocol legality. The real wire round-trip and
/// calibration-inversion tests land with the module bodies (Phase 1).
/// </summary>
public class ScaffoldSanityTests
{
    [Fact]
    public void Create_AllowsLegalTriple()
    {
        var profile = VirtualRadioProfile.Create(HpsdrBoardKind.HermesII, ProtocolVersion.P1);
        Assert.Equal(HpsdrBoardKind.HermesII, profile.Board);
        Assert.Equal(ProtocolVersion.P1, profile.Protocol);
    }

    [Fact]
    public void Create_RejectsIllegalTriple_Hl2OnP2()
    {
        Assert.Throws<ArgumentException>(() =>
            VirtualRadioProfile.Create(HpsdrBoardKind.HermesLite2, ProtocolVersion.P2));
    }

    [Fact]
    public void BoardProtocolSupport_SeedMatchesPlan()
    {
        Assert.Equal(ProtocolSupport.P1, BoardProtocolSupport.For(HpsdrBoardKind.Metis));
        Assert.Equal(ProtocolSupport.P1, BoardProtocolSupport.For(HpsdrBoardKind.HermesLite2));
        Assert.Equal(ProtocolSupport.Both, BoardProtocolSupport.For(HpsdrBoardKind.HermesII));
        Assert.True(BoardProtocolSupport.Supports(HpsdrBoardKind.Orion, ProtocolVersion.P2));
    }
}

/// <summary>
/// Gated loopback integration test for the live RX path. Spins the emulator
/// in-process on <c>127.0.0.1:1024</c> and points the real Protocol-1 client at
/// it over loopback. This is the ONE socket test in the suite and MUST stay
/// gated behind the <c>ZEUS_VRADIO_LOOPBACK</c> env var (the repo rule: no real
/// sockets in normal CI — a Windows CI host minidumps on real socket loops in
/// unit tests). The implementer fills the body in Phase 1.
/// </summary>
public class Protocol1Emulator_LoopbackRx_IntegrationTest
{
    // ANAN-10E impersonation → HermesII → RadioCalibration.Hermes. Used to turn
    // the FWD/REF ADC counts the client reads back into watts via Zeus's own
    // forward math (TxMetersService.ComputeMeters) — the calibration round-trip.
    private static readonly RadioCalibration Cal = RadioCalibration.Hermes;

    // TxMetersService C0-echo address constants (the FWD/REF meter slot map).
    private const byte C0AddrMask = 0x7E;
    private const byte C0AddrAlexFwd = 0x08; // Ain1 = forward-power ADC
    private const byte C0AddrAlexRef = 0x10; // Ain0 = reflected-power ADC

    [SkippableFact]
    public async Task LoopbackRx_DeliversFrames()
    {
        Skip.If(
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ZEUS_VRADIO_LOOPBACK")),
            "Set ZEUS_VRADIO_LOOPBACK=1 to run the loopback RX integration test (binds a real UDP socket).");

        int port = GetFreeUdpPort();
        var endpoint = new IPEndPoint(IPAddress.Loopback, port);

        // ANAN-10E on P1 with one S9 tone at 14.074 MHz and a quiet noise floor.
        var profile = VirtualRadioProfile.Create(HpsdrBoardKind.HermesII, ProtocolVersion.P1) with
        {
            BindAddress = IPAddress.Loopback,
            SampleRateKhz = 48,
            TunedHz = 14_074_000,
            Tones = new[] { new ToneSpec(14_074_000, -73) },
            NoiseFloorDbc = -110,
        };

        var engine = new Protocol1Engine(profile, logger: null, port: port);
        using var engineCts = new CancellationTokenSource();
        Task engineTask = engine.RunAsync(engineCts.Token);

        // Capture the FWD/REF ADC counts the client parses off the wire.
        ushort fwdAdc = 0, refAdc = 0;

        using var client = new Protocol1Client();
        client.SetBoardKind(HpsdrBoardKind.HermesII);
        client.TelemetryReceived += reading =>
        {
            switch (reading.C0Address & C0AddrMask)
            {
                case C0AddrAlexFwd: Volatile.Write(ref fwdAdc, reading.Ain1); break;
                case C0AddrAlexRef: Volatile.Write(ref refAdc, reading.Ain0); break;
            }
        };

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            await client.ConnectAsync(endpoint, cts.Token).ConfigureAwait(false);
            client.SetVfoAHz(14_074_000);
            await client.StartAsync(
                new StreamConfig(HpsdrSampleRate.Rate48k, PreampOn: false, Atten: HpsdrAtten.Zero),
                cts.Token).ConfigureAwait(false);

            // --- RX: assert >=300 EP6 frames with <10% drop and non-garbage IQ.
            int frameCount = 0;
            double maxMag = 0;
            await foreach (var frame in client.IqFrames.ReadAllAsync(cts.Token).ConfigureAwait(false))
            {
                Assert.Equal(48_000, frame.SampleRateHz);
                Assert.Equal(PacketParser.ComplexSamplesPerPacket, frame.SampleCount);
                var span = frame.InterleavedSamples.Span;
                for (int s = 0; s < frame.SampleCount; s++)
                {
                    double i = span[2 * s], q = span[2 * s + 1];
                    double mag = Math.Sqrt(i * i + q * q);
                    if (mag > maxMag) maxMag = mag;
                }
                if (++frameCount >= 300) break;
            }

            long total = client.TotalFrames;
            long dropped = client.DroppedFrames;
            Assert.True(total >= 300, $"expected >=300 parsed frames, got {total}");
            Assert.True(dropped * 10 < total, $"dropped {dropped}/{total} > 10%");
            // The -73 dBc tone should be plainly above the -110 dBc floor: a tone
            // amplitude near 10^(-73/20) ≈ 2.2e-4 full scale, far above noise.
            Assert.True(maxMag > 5e-5, $"RX IQ looks like silence/garbage (maxMag={maxMag:E2})");

            // --- TX path: drive the radio with a known drive byte + MOX and assert
            // the engine decoded it AND telemetry reads back as forward power.
            const byte testDrive = 192;
            client.SetDriveByte(testDrive);
            client.SetMox(true);

            await WaitUntilAsync(
                () => engine.DecodedMox && engine.DecodedDriveByte == testDrive,
                cts.Token, TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            Assert.True(engine.DecodedMox, "engine did not decode MOX");
            Assert.Equal(testDrive, engine.DecodedDriveByte);

            // Let a few keyed EP6 telemetry slots arrive, then read back watts.
            await WaitUntilAsync(
                () => Volatile.Read(ref fwdAdc) > Cal.AdcCalOffset + 10,
                cts.Token, TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            var (fwdWattsOn, _, _) = TxMetersService.ComputeMeters(
                Volatile.Read(ref fwdAdc), Volatile.Read(ref refAdc), Cal);
            Assert.True(fwdWattsOn > 0.0, $"expected fwd watts > 0 while keyed, got {fwdWattsOn:F3} W");

            // --- Unkey: forward power must collapse back to ~0 W.
            client.SetMox(false);
            await WaitUntilAsync(
                () => Volatile.Read(ref fwdAdc) <= Cal.AdcCalOffset + 2,
                cts.Token, TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            var (fwdWattsOff, _, _) = TxMetersService.ComputeMeters(
                Volatile.Read(ref fwdAdc), Volatile.Read(ref refAdc), Cal);
            Assert.True(fwdWattsOff < 0.01, $"expected ~0 fwd watts unkeyed, got {fwdWattsOff:F4} W");

            Console.WriteLine(
                $"[vradio-loopback] frames={total} dropped={dropped} ({(double)dropped / total * 100:F2}%) " +
                $"maxIqMag={maxMag:E3} ep6Sent={engine.Snapshot().Ep6PacketsSent} " +
                $"ep2Recv={engine.Snapshot().Ep2PacketsReceived} decodedDrive={engine.DecodedDriveByte} " +
                $"fwdWattsKeyed={fwdWattsOn:F3} fwdWattsUnkeyed={fwdWattsOff:F4}");

            await client.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await client.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            engineCts.Cancel();
            try { await engineTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception) { /* shutdown best-effort */ }
        }
    }

    /// <summary>Poll <paramref name="condition"/> until true or the timeout lapses.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) return;
            await Task.Delay(20, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Grab a currently-free loopback UDP port for the hermetic test.</summary>
    private static int GetFreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
