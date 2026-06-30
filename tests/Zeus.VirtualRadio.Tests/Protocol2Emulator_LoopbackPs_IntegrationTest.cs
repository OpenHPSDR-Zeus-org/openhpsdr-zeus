// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Protocol2;
using Zeus.VirtualRadio;
using Zeus.VirtualRadio.P2;

namespace Zeus.VirtualRadio.Tests;

/// <summary>
/// End-to-end "Zeus headless + emulator" loopback test of the ANAN-10E
/// (HermesII) single-ADC PureSignal time-mux. The emulator runs in-process on
/// 127.0.0.1, a REAL <see cref="Protocol2Client"/> direct-connects over
/// loopback, and the test arms PS + keys TX and asserts the full feedback loop:
/// the client puts byte 1363 = 0x02 and a protective byte 59 on the wire, the
/// emulator returns the coupler/reference interleave, and the client decodes
/// <see cref="PsFeedbackFrame"/>s. Unkeying returns DDC0 to user RX and restores
/// byte 59.
///
/// Gated behind <c>ZEUS_VRADIO_LOOPBACK</c> (it binds the well-known radio
/// ports and uses real loopback sockets), so it never runs in the default CI
/// unit pass — that pass stays socketless. Flipping
/// <see cref="Protocol2Client.Hermes10ePsTimeMuxOnAir"/> is a TEST-ONLY,
/// try/finally-restored, process-global mutation; the burn-zone production flag
/// stays false.
/// </summary>
public class Protocol2Emulator_LoopbackPs_IntegrationTest
{
    private sealed class CaptureSink : IRxPacketSink
    {
        private long _iq;
        private long _ps;
        private volatile bool _sawNonFiniteSample;

        public long IqCount => Interlocked.Read(ref _iq);
        public long PsCount => Interlocked.Read(ref _ps);
        public bool AllSamplesFinite => !_sawNonFiniteSample;

        public void OnIqFrame(in IqFrame frame) => Interlocked.Increment(ref _iq);

        public void OnPsFeedbackFrame(in PsFeedbackFrame frame)
        {
            // Spot-check the first few samples are finite (a real WDSP feed).
            for (int i = 0; i < 4 && i < frame.RxI.Length; i++)
            {
                if (!float.IsFinite(frame.RxI[i]) || !float.IsFinite(frame.TxI[i]))
                    _sawNonFiniteSample = true;
            }
            Interlocked.Increment(ref _ps);
        }
    }

    [SkippableFact]
    public async Task ArmPs_KeyTx_DeliversFeedbackFrames_ThenUnkeyRestoresRx()
    {
        Skip.IfNot(
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ZEUS_VRADIO_LOOPBACK")),
            "Set ZEUS_VRADIO_LOOPBACK to run the P2 emulator loopback integration test.");

        const long toneHz = 14_074_000;
        var profile = VirtualRadioProfile.Create(HpsdrBoardKind.HermesII, ProtocolVersion.P2) with
        {
            BindAddress = IPAddress.Loopback,
            SampleRateKhz = 48,
            Tones = new[] { new ToneSpec(toneHz, -40) },
            NoiseFloorDbc = -110,
        };

        using var engineCts = new CancellationTokenSource();
        var engine = new Protocol2Engine(profile, logger: null, psDistortion: true);
        Task engineRun = engine.RunAsync(engineCts.Token);

        // Let the emulator bind its ports before the client connects.
        await Task.Delay(400);

        bool priorFlag = Protocol2Client.Hermes10ePsTimeMuxOnAir;
        Protocol2Client.Hermes10ePsTimeMuxOnAir = true;
        var client = new Protocol2Client(NullLogger<Protocol2Client>.Instance);
        var sink = new CaptureSink();

        try
        {
            client.AttachRxSink(sink);
            client.SetBoardKind(HpsdrBoardKind.HermesII);
            client.SetNumAdc(1);
            client.SetVfoAHz(toneHz);

            await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 1024), engineCts.Token);
            await client.StartAsync(48, engineCts.Token);

            // 1) RX alive at rest.
            Assert.True(await WaitUntil(() => sink.IqCount > 5, TimeSpan.FromSeconds(5)),
                "no user-RX IQ frames at rest");

            // 2) Arm PS (seeds byte 59 protective) and key TX.
            client.SetPsFeedbackEnabled(true);
            client.SetDriveByte(200);
            client.SetMox(true);

            // Feed a little TX-IQ to exercise the 1029 ingest path (the emulator
            // also synthesizes a fallback reference, so the loop never starves).
            var txIq = new float[240 * 2];
            for (int n = 0; n < 240; n++)
            {
                txIq[2 * n] = 0.5f * (float)Math.Cos(2 * Math.PI * n / 16.0);
                txIq[2 * n + 1] = 0.5f * (float)Math.Sin(2 * Math.PI * n / 16.0);
            }

            long psBefore = sink.PsCount;
            var keyed = Stopwatch.StartNew();
            bool gotFrames = false;
            while (keyed.Elapsed < TimeSpan.FromSeconds(6))
            {
                client.SendTxIq(txIq);
                if (sink.PsCount - psBefore >= 2) { gotFrames = true; break; }
                await Task.Delay(20);
            }

            // 3) The client put the PS time-mux on the wire and the emulator saw it.
            Assert.True(engine.PsBurstArmed, "emulator never saw CmdRx byte 1363 = 0x02 while keyed");
            // 4) Byte-59 safety seed reached the wire (protective, >= 1).
            Assert.True(engine.DecodedTxStepAttnDb >= Protocol2Engine.TxAdcProtectFloorDb,
                $"byte 59 not protective while keyed (was {engine.DecodedTxStepAttnDb})");
            // 5) Feedback frames delivered, all finite.
            Assert.True(gotFrames, "no PS feedback frames delivered while keyed");
            Assert.True(sink.AllSamplesFinite, "PS feedback carried a non-finite sample");

            // 6) Unkey + disarm → DDC0 returns to user RX and byte 59 restores.
            client.SetMox(false);
            client.SetPsFeedbackEnabled(false);

            long iqAtUnkey = sink.IqCount;
            Assert.True(await WaitUntil(() => sink.IqCount > iqAtUnkey + 5, TimeSpan.FromSeconds(5)),
                "user-RX did not resume after unkey");
            Assert.False(engine.PsBurstArmed, "PS burst still armed after unkey");
            Assert.True(await WaitUntil(() => engine.DecodedTxStepAttnDb == 0, TimeSpan.FromSeconds(2)),
                $"byte 59 not restored after disarm (was {engine.DecodedTxStepAttnDb})");
        }
        finally
        {
            try { await client.StopAsync(CancellationToken.None); } catch { }
            client.Dispose();
            Protocol2Client.Hermes10ePsTimeMuxOnAir = priorFlag;
            engineCts.Cancel();
            try { await engineRun; } catch (OperationCanceledException) { } catch { }
        }
    }

    private static async Task<bool> WaitUntil(Func<bool> predicate, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (predicate()) return true;
            await Task.Delay(25);
        }
        return predicate();
    }
}
