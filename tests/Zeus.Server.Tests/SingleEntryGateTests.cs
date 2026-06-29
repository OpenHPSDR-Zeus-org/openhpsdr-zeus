// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.

using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Zeus.Server;

namespace Zeus.Server.Tests;

/// <summary>
/// Behaviour and concurrent-correctness tests for <see cref="SingleEntryGate"/>,
/// the gate that keeps <c>DspPipelineService.Tick</c> single-threaded across the
/// RX-sink attach/detach window (issue #1167). The deterministic sequencing test
/// is the must-have; the stress test asserts mutual exclusion under contention.
/// </summary>
public class SingleEntryGateTests
{
    [Fact]
    public void TryEnter_Admits_Exactly_One_Until_Exit()
    {
        var gate = new SingleEntryGate();

        // Fresh gate → first caller is admitted.
        Assert.True(gate.TryEnter());

        // While held, a second TryEnter is rejected (no blocking).
        Assert.False(gate.TryEnter());
        Assert.False(gate.TryEnter());

        // After Exit, the gate is free again.
        gate.Exit();
        Assert.True(gate.TryEnter());

        // Exit is idempotent / safe to call repeatedly.
        gate.Exit();
        gate.Exit();
        Assert.True(gate.TryEnter());
        gate.Exit();
    }

    [Fact]
    public void Concurrent_Callers_Never_Overlap_In_Critical_Section()
    {
        var gate = new SingleEntryGate();

        const int threads = 8;
        const int iterations = 100_000;

        int sentinel = 0;        // plain int: only the holder may touch it
        long admitted = 0;       // total successful entries
        long rejected = 0;       // total rejected entries (contention proof)
        bool violation = false;  // set if two callers were ever inside at once

        Parallel.For(0, threads, _ =>
        {
            long localRejects = 0;
            for (int i = 0; i < iterations; i++)
            {
                if (!gate.TryEnter())
                {
                    localRejects++;
                    continue;
                }

                // Critical section. If the gate is correct, no other thread is
                // here, so the sentinel must read 0 on entry.
                if (Volatile.Read(ref sentinel) != 0)
                {
                    violation = true;
                }
                Volatile.Write(ref sentinel, 1);

                // A touch of work to widen the overlap window.
                Thread.SpinWait(4);

                Volatile.Write(ref sentinel, 0);
                Interlocked.Increment(ref admitted);
                gate.Exit();
            }
            Interlocked.Add(ref rejected, localRejects);
        });

        Assert.False(violation, "two callers were inside the gate at the same time");
        // Every thread did 'iterations' attempts; admitted + rejected must equal
        // the total, and under real contention some attempts must be rejected.
        Assert.Equal((long)threads * iterations, admitted + rejected);
        Assert.True(rejected > 0, "expected some rejected entries under contention");

        // Gate ends free.
        Assert.True(gate.TryEnter());
        gate.Exit();
    }
}
