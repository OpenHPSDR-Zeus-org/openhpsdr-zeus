// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zeus.Dsp;
using Zeus.Protocol1;

namespace Zeus.Server;

/// <summary>
/// Keeps WDSP TXA's sample pump running when TUN or TwoTone is armed but
/// the mic uplink isn't. Both PostGen modes (mode=0 TUN carrier, mode=1
/// TwoTone) live inside the TXA chain
/// (<see cref="Zeus.Dsp.Wdsp.WdspDspEngine.SetTxTune"/> /
/// <see cref="Zeus.Dsp.Wdsp.WdspDspEngine.SetTwoTone"/>), so they only
/// emit IQ when <c>fexchange2</c> is called at the block rate. During MOX
/// that call is driven by <see cref="TxAudioIngest"/> as mic frames arrive;
/// during TUN/TwoTone there's no mic, so this service synthesises silent
/// mic input at the WDSP block cadence (1024 samples @ 48 kHz ≈ 21 ms).
/// PostGen overwrites the silent midbuff regardless of mic content, so the
/// same pump path works for both modes — we just gate on either flag.
///
/// Starts and stops via <see cref="TxService.IsTunOn"/> /
/// <see cref="TxService.IsTwoToneOn"/> polling; not worth building a
/// subscription pattern for a feature that toggles at click rate.
/// </summary>
internal sealed class TxTuneDriver : BackgroundService
{
    private static readonly TimeSpan PollIdle = TimeSpan.FromMilliseconds(100);
    // Tick is derived from the engine's mic block size per loop iteration
    // (block_samples / 48 kHz, shaved slightly so we run a little faster
    // than WDSP's block clock). Fixed 20 ms fell behind on P2's 512-sample
    // block (10.67 ms) and starved the G2 DUC, producing close-in spurs.
    private const int MicRateHz = 48_000;
    // Blocks delivered back-to-back at key-down to fill the radio's TX FIFO to
    // its ~1250-sample target before steady pacing begins. One P2 block already
    // carries ~2048 output samples, so a handful primes the FIFO well clear of
    // the underrun floor without overshooting the sender's throttle headroom.
    private const int PrimeBlocks = 4;

    private readonly TxService _tx;
    private readonly DspPipelineService _pipeline;
    private readonly TxIqRing _ring;
    private readonly ILogger<TxTuneDriver> _log;

    public TxTuneDriver(TxService tx, DspPipelineService pipeline, TxIqRing ring, ILogger<TxTuneDriver> log)
    {
        _tx = tx;
        _pipeline = pipeline;
        _ring = ring;
        _log = log;
    }

    protected override Task ExecuteAsync(CancellationToken ct)
    {
        // Run the pump on a DEDICATED thread promoted to the platform pro-audio /
        // real-time class. The deadline pacing below keeps TX-IQ production locked
        // to the DAC block clock — but only if this thread isn't preempted. In
        // desktop mode the native-audio (CoreAudio) device threads run at RT and
        // starve a normal-priority ThreadPool pump, so production comes in bursts:
        // the two-tone amplitude swings (observed-peak wander) and the signal goes
        // dirty. Web has no competing audio threads, so the identical pacing stays
        // clean — the desktop gap (#559). LongRunning → a real dedicated thread we
        // can keep promoted; the loop is synchronous (no await) so it never bounces
        // back to the pool and loses the promotion mid-transmit.
        return Task.Factory.StartNew(
            () => PumpLoop(ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private void PumpLoop(CancellationToken ct)
    {
        Zeus.Protocol2.RealtimeThreadPriority.PromoteCallingThreadToProAudio(_log);
        float[]? micScratch = null;
        float[]? iqScratch = null;
        // Drift-free pacing state. `clock` is the monotonic reference; pacing is
        // false whenever TUN/TwoTone is idle so the next key-down re-primes and
        // re-anchors the schedule from scratch.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long nextDeadlineTicks = 0;
        int primeRemaining = 0;
        bool pacing = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_tx.IsTunOn && !_tx.IsTwoToneOn)
                {
                    pacing = false;
                    if (ct.WaitHandle.WaitOne(PollIdle)) return;
                    continue;
                }

                var engine = _pipeline.CurrentEngine;
                int micBlock = engine?.TxBlockSamples ?? 0;
                int iqOut = engine?.TxOutputSamples ?? 0;
                if (engine is null || micBlock <= 0 || iqOut <= 0)
                {
                    // No TXA yet — retry on the slow cadence.
                    pacing = false;
                    if (ct.WaitHandle.WaitOne(PollIdle)) return;
                    continue;
                }

                if (micScratch is null || micScratch.Length < micBlock)
                    micScratch = new float[micBlock];
                if (iqScratch is null || iqScratch.Length < 2 * iqOut)
                    iqScratch = new float[2 * iqOut];

                // Silent mic — the post-gen tone gets inserted after the mic
                // processing stage by WDSP, so fexchange2 still produces the
                // carrier even with zero mic input.
                Array.Clear(micScratch, 0, micBlock);
                int produced = engine.ProcessTxBlock(
                    new ReadOnlySpan<float>(micScratch, 0, micBlock),
                    new Span<float>(iqScratch, 0, 2 * iqOut));
                if (produced > 0)
                {
                    var iqSpan = new ReadOnlySpan<float>(iqScratch, 0, 2 * produced);
                    // P1 path: ring feeds the EP2 packer in Protocol1Client.
                    _ring.Write(iqSpan);
                    // P2 path: forward the same block directly to the active
                    // Protocol2Client's 1029-port DUC sender. No-op when P2
                    // isn't the active backend, so both protocols coexist
                    // without a conditional at this seam.
                    _pipeline.ForwardTxIqToP2(iqSpan);
                }

                // Wall-clock deadline pacing. A fixed Task.Delay(tickMs) was
                // overshot by the OS scheduler + ProcessTxBlock time, so the loop
                // ran ~13 ms instead of the 10.67 ms real-time period of a P2
                // 512-sample block — production fell ~6 % below the 192 kHz DAC
                // rate (≈750 vs 800 pkts/s), draining the radio's TX FIFO and
                // producing a gappy/dirty two-tone until a MOX cycle re-primed it.
                // Pacing against a monotonic deadline borrows per-block jitter back
                // on the next iteration, so the average production rate stays locked
                // to the block clock. We aim a hair (~3 %) above real-time so the
                // FIFO stays topped; the P2 sender's FIFO model throttles the small
                // surplus. TxTuneDriver only drives TUN / TwoTone (short bursts),
                // never voice — voice is pumped by the hardware-clocked mic path.
                long periodTicks = (long)(System.Diagnostics.Stopwatch.Frequency
                    * micBlock * 0.97 / MicRateHz);

                if (!pacing)
                {
                    // First block of this key-down: prime the radio FIFO by
                    // delivering a short burst back-to-back before steady pacing.
                    pacing = true;
                    primeRemaining = PrimeBlocks;
                }

                if (primeRemaining > 0)
                {
                    primeRemaining--;
                    // Anchor the steady-state schedule to the moment priming ends.
                    if (primeRemaining == 0) nextDeadlineTicks = clock.ElapsedTicks;
                    continue;
                }

                nextDeadlineTicks += periodTicks;
                long remainingTicks = nextDeadlineTicks - clock.ElapsedTicks;
                if (remainingTicks <= 0)
                {
                    // Behind schedule (slow block / scheduler hiccup): skip the
                    // delay and catch up. Re-anchor only if we've fallen badly
                    // behind so a long stall can't trigger a runaway burst.
                    if (-remainingTicks > periodTicks * 8) nextDeadlineTicks = clock.ElapsedTicks;
                    continue;
                }
                int delayMs = (int)(remainingTicks * 1000 / System.Diagnostics.Stopwatch.Frequency);
                if (delayMs > 0 && ct.WaitHandle.WaitOne(delayMs)) return;
                // Sub-millisecond remainder is left unspun; the deadline accounting
                // folds it into the next iteration.
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "tx.tune driver tick failed");
                pacing = false;
                if (ct.WaitHandle.WaitOne(PollIdle)) return;
            }
        }
    }
}
