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
/// End-to-end "Zeus headless + emulator" loopback test of the ANAN-G2E
/// (<see cref="HpsdrBoardKind.HermesC10"/>) single-ADC PureSignal time-mux — the
/// sibling of <c>Protocol2Emulator_LoopbackPs_IntegrationTest</c> (the 10E /
/// HermesII variant). The emulator runs in-process on 127.0.0.1 impersonating a
/// HermesC10 (discovery board byte 0x14), a REAL <see cref="Protocol2Client"/>
/// direct-connects over loopback, and the test arms PS + keys TX and asserts the
/// FEEDBACK DATA PATH works end-to-end: the client puts byte 1363 = 0x02 on the
/// wire during the burst, the emulator returns the coupler/reference interleave,
/// and the client decodes <see cref="PsFeedbackFrame"/>s. Unkeying returns DDC0
/// to the user RX.
///
/// THE G2E DELTA vs the 10E (the open, KB2UKA-gated item this test documents):
/// the 10E host path seeds byte 59 (Angelia_atten_Tx0) to the protective floor
/// (31 dB) on arm (<see cref="Protocol2Client.SeedsTxAdcProtection"/> is true for
/// HermesII), so its loopback test asserts byte 59 >= 1. The G2E host path does
/// NOT seed byte 59 — <c>SeedsTxAdcProtection(HermesC10)</c> is deliberately
/// false (the G2E byte-59 seed is a separate, independently-gated pre-condition
/// per the <see cref="Protocol2Client.G2ePsTimeMuxOnAir"/> doc, pre-condition A,
/// and touching it is a PureSignal-hard-rule change requiring KB2UKA sign-off).
/// So on the G2E byte 59 stays at the operator default (0) through the keyed
/// burst, and the emulator — modelling the single-ADC hazard — raises
/// first-key-down ADC overload in its hi-priority status. This test asserts BOTH:
/// the feedback frames flow (the path is functionally complete) AND the overload
/// latches (the byte-59 protective seed is still missing). That is exactly the
/// "does the dark G2E PS path work, and what needs sign-off before real RF"
/// answer, captured as a deterministic, hardware-free bench.
///
/// Gated behind <c>ZEUS_VRADIO_LOOPBACK</c> (it binds the well-known radio ports
/// and uses real loopback sockets), so it never runs in the default socketless
/// CI unit pass. Flipping <see cref="Protocol2Client.G2ePsTimeMuxOnAir"/> is a
/// TEST-ONLY, try/finally-restored, process-global mutation; the burn-zone
/// production flag stays false. NOTHING here flips the seed itself.
/// </summary>
public class Protocol2Emulator_LoopbackG2ePs_IntegrationTest
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
    public async Task ArmPs_KeyTx_DeliversFeedback_ButByte59Unseeded_RaisesAdcOverload()
    {
        Skip.IfNot(
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ZEUS_VRADIO_LOOPBACK")),
            "Set ZEUS_VRADIO_LOOPBACK to run the G2E emulator loopback integration test.");

        const long toneHz = 14_074_000;
        var profile = VirtualRadioProfile.Create(HpsdrBoardKind.HermesC10, ProtocolVersion.P2) with
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

        bool priorFlag = Protocol2Client.G2ePsTimeMuxOnAir;
        Protocol2Client.G2ePsTimeMuxOnAir = true;
        var client = new Protocol2Client(NullLogger<Protocol2Client>.Instance);
        var sink = new CaptureSink();

        try
        {
            client.AttachRxSink(sink);
            client.SetBoardKind(HpsdrBoardKind.HermesC10);
            client.SetNumAdc(1);
            client.SetVfoAHz(toneHz);

            await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 1024), engineCts.Token);
            await client.StartAsync(48, engineCts.Token);

            // 1) RX alive at rest (the #960 regression: arming PS must not kill RX).
            Assert.True(await WaitUntil(() => sink.IqCount > 5, TimeSpan.FromSeconds(5)),
                "no user-RX IQ frames at rest");

            // 2) Arm PS and key TX. Note: on the G2E this does NOT seed byte 59
            //    (SeedsTxAdcProtection(HermesC10) == false), unlike the 10E.
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

            // 3) The client put the G2E PS time-mux on the wire and the emulator saw it.
            Assert.True(engine.PsBurstArmed, "emulator never saw CmdRx byte 1363 = 0x02 while keyed");

            // 4) The FEEDBACK DATA PATH is functionally complete: frames delivered,
            //    all finite. The dark G2E path genuinely works end-to-end.
            Assert.True(gotFrames, "no PS feedback frames delivered while keyed");
            Assert.True(sink.AllSamplesFinite, "PS feedback carried a non-finite sample");

            // 5) THE OPEN GAP (KB2UKA sign-off + G2E bench, #289): byte 59 was NOT
            //    seeded to the protective floor on the G2E (it stays at the operator
            //    default 0 < floor), so the emulator's single-ADC model raises
            //    first-key-down ADC overload. A real G2E would slam the PA coupler
            //    into its one RX ADC at 0 dB here. This is the byte-59
            //    (Angelia_atten_Tx0) protective-seed item that must land — as a
            //    PureSignal-hard-rule change — before the flag could ever go on-air.
            Assert.True(engine.DecodedTxStepAttnDb < Protocol2Engine.TxAdcProtectFloorDb,
                $"byte 59 was unexpectedly protective on the G2E (was {engine.DecodedTxStepAttnDb}); " +
                "the G2E byte-59 seed is supposed to be unimplemented/KB2UKA-gated");
            Assert.True(await WaitUntil(() => engine.PsAdcOverloadLatched, TimeSpan.FromSeconds(2)),
                "emulator did not raise the expected first-key-down ADC overload for the unseeded G2E byte 59");

            // 6) Unkey + disarm → DDC0 returns to user RX. Byte 59 was never seeded,
            //    so there is nothing to restore (stays 0).
            client.SetMox(false);
            client.SetPsFeedbackEnabled(false);

            long iqAtUnkey = sink.IqCount;
            Assert.True(await WaitUntil(() => sink.IqCount > iqAtUnkey + 5, TimeSpan.FromSeconds(5)),
                "user-RX did not resume after unkey");
            Assert.False(engine.PsBurstArmed, "PS burst still armed after unkey");
            Assert.Equal((byte)0, engine.DecodedTxStepAttnDb); // never seeded on the G2E
        }
        finally
        {
            try { await client.StopAsync(CancellationToken.None); } catch { }
            client.Dispose();
            Protocol2Client.G2ePsTimeMuxOnAir = priorFlag;
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
