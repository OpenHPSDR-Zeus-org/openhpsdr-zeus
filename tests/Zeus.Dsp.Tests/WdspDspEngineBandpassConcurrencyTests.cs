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
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using Zeus.Contracts;
using Zeus.Dsp;
using Zeus.Dsp.Wdsp;

namespace Zeus.Dsp.Tests;

// Regression guard for fix/filter-drag-double-free. Dragging the RX filter
// edge very fast floods POST /api/filter; the minimal-API handler runs on
// ASP.NET thread-pool workers, so two filter updates land on two threads and
// both rebuild an FFTW plan via SetRXABandpassFreqs / RXANBPSetFreqs /
// SetRXASNBAOutputBandwidth (and the TX leg via SetTXABandpassFreqs). The
// bundled libfftw3 is built WITHOUT --enable-threads, so concurrent plan
// create/destroy double-frees. On macOS libmalloc aborts with
// POINTER_BEING_FREED_WAS_NOT_ALLOCATED; on Linux/Windows it corrupts silently.
//
// WdspDspEngine._fftwPlannerGate serializes every plan-mutating bandpass call.
// This test pounds those entry points from many threads at once. With the gate
// in place it completes cleanly; REMOVE the gate and on the macOS bench this
// aborts with SIGABRT — that hard abort is the regression sentinel (it cannot
// be caught as a managed exception, so the test process dies, which is exactly
// the failure mode the bug filed).
//
// Requires the real native WDSP + libfftw3 — the synthetic engine never
// touches FFTW. Gated like every other WDSP-backed test so CI without the
// dylib stays green and this runs on the macOS bench where the crash repros.
[Collection("Wdsp")]
public class WdspDspEngineBandpassConcurrencyTests
{
    private static bool WdspAvailable()
    {
        try { return WdspNativeLoader.TryProbe(); }
        catch { return false; }
    }

    [SkippableFact]
    public void ConcurrentFilterAndModeChanges_DoNotDoubleFreeFftwPlans()
    {
        Skip.IfNot(WdspAvailable(), "libwdsp not available — builder has not dropped the .dylib yet");

        const int SampleRate = 192_000;
        const int Width = 2048;
        const int IterationsPerThread = 2_000;
        const int RxFilterThreads = 8;

        using var engine = new WdspDspEngine();
        int rx = engine.OpenChannel(SampleRate, Width);
        try
        {
            engine.OpenTxChannel();
            engine.SetMode(rx, RxMode.USB);

            using var start = new ManualResetEventSlim(false);
            var threads = new List<Thread>();
            Exception? firstError = null;
            var errorLock = new object();

            void Guard(Action body)
            {
                start.Wait();
                try { body(); }
                catch (Exception ex)
                {
                    lock (errorLock) { firstError ??= ex; }
                }
            }

            // RX filter flood — the confirmed crash entry point. Each tick
            // rebuilds three FFTW plans (bp1 / nbp0 / snba) with a rapidly
            // varying passband, exactly like a fast edge drag.
            for (int t = 0; t < RxFilterThreads; t++)
            {
                int seed = t;
                threads.Add(new Thread(() => Guard(() =>
                {
                    var rng = new Random(seed);
                    for (int i = 0; i < IterationsPerThread; i++)
                    {
                        int lo = 50 + rng.Next(0, 400);
                        int hi = lo + 200 + rng.Next(0, 2600);
                        engine.SetFilter(rx, lo, hi);
                    }
                })));
            }

            // TX filter flood — same plan-rebuild path on the TXA channel,
            // sharing the one process-wide FFTW planner with the RX threads.
            threads.Add(new Thread(() => Guard(() =>
            {
                var rng = new Random(1001);
                for (int i = 0; i < IterationsPerThread; i++)
                {
                    int lo = 50 + rng.Next(0, 400);
                    int hi = lo + 200 + rng.Next(0, 2600);
                    engine.SetTxFilter(lo, hi);
                }
            })));

            // Mode flipper — drives the SetMode → ApplyBandpassForMode entry
            // (the other reachable create/destroy path during a drag).
            threads.Add(new Thread(() => Guard(() =>
            {
                var modes = new[] { RxMode.USB, RxMode.LSB, RxMode.AM, RxMode.CWU };
                for (int i = 0; i < IterationsPerThread; i++)
                    engine.SetMode(rx, modes[i % modes.Length]);
            })));

            foreach (var th in threads) th.Start();
            start.Set();
            foreach (var th in threads)
                Assert.True(th.Join(TimeSpan.FromSeconds(60)), "a bandpass-hammer thread hung");

            Assert.Null(firstError);
        }
        finally
        {
            engine.CloseChannel(rx);
        }
    }
}
