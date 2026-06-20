using Zeus.Plugins.Contracts.Audio;
using Zeus.Plugins.Host.Audio;

namespace Zeus.Plugins.Host.Tests;

/// <summary>
/// Realtime-contract regression: AudioChain.Process should not
/// allocate on the steady-state hot path. We don't enforce
/// zero-allocation across the whole call here (the chain's slot
/// array allocates lazily on construction), but we verify that
/// running the same block N times in a row allocates nothing.
///
/// We measure THREAD-LOCAL allocation (GC.GetAllocatedBytesForCurrentThread)
/// rather than the process-wide GC collection count. GC.CollectionCount(0)
/// is process-wide: any unrelated thread or runtime background activity can
/// bump it even when Process() allocates nothing, producing false reds on
/// CI (observed on macOS: Expected 33, Actual 34). The thread-local
/// allocation delta is unaffected by other threads and directly asserts the
/// real no-alloc invariant.
///
/// We assert on the PER-BLOCK average rather than total bytes. Tiered JIT
/// can promote Process() to tier-1 / perform on-stack replacement while the
/// loop is already running, charging a fixed one-shot allocation to this
/// thread that is independent of iteration count (observed on Windows CI:
/// ~5760 bytes total, i.e. <1 byte/block). A genuine per-call leak — a stray
/// array, boxing, or closure — allocates at least a byte every iteration, so
/// it survives integer division and trips the assert; the JIT artifact does
/// not.
/// </summary>
[Collection("LoadSensitive")]
public class AudioChainProcessNoAllocTests
{
    private const int Iterations = 10_000;

    [Fact]
    public void Process_OverManyBlocks_DoesNotAllocate()
    {
        var chain = new AudioChain();
        var input  = new float[256];
        var output = new float[256];
        var ctx = new AudioBlockContext(48000, 1, 256, 0, false);
        for (int i = 0; i < input.Length; i++) input[i] = (float)i / 256f;

        // Window 1 — warm up AND absorb tiered-JIT recompilation. Tiered
        // compilation can promote Process() to tier-1 / perform on-stack
        // replacement WHILE the loop is already running, charging a one-shot
        // allocation to this thread that a short fixed warm-up races on a
        // loaded runner (the intermittent macOS red, where the blip exceeded
        // even a per-block threshold). Running a FULL window first drives the
        // path fully into tier-1, so the measured window below is the true
        // steady-state hot path. The [Collection("LoadSensitive")] isolation
        // keeps sibling tests from perturbing this further.
        for (int i = 0; i < Iterations; i++) chain.Process(input, output, ctx);

        // Window 2 — fully warmed + tiered: measure thread-local bytes across
        // the steady-state hot loop. Immune to GCs triggered by other threads.
        var startBytes = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < Iterations; i++)
        {
            chain.Process(input, output, ctx);
        }

        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - startBytes;

        // Per-block average: 0 for the steady-state hot path; a real per-call
        // leak rounds up to >= 1, a fixed tiered-JIT cost rounds down to 0.
        var bytesPerBlock = allocatedBytes / Iterations;
        Assert.Equal(0, bytesPerBlock);
    }
}
