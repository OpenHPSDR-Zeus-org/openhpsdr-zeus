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
/// </summary>
public class AudioChainProcessNoAllocTests
{
    [Fact]
    public void Process_OverManyBlocks_DoesNotAllocate()
    {
        var chain = new AudioChain();
        var input  = new float[256];
        var output = new float[256];
        var ctx = new AudioBlockContext(48000, 1, 256, 0, false);
        for (int i = 0; i < input.Length; i++) input[i] = (float)i / 256f;

        // Warm-up: prime any one-shot init that AudioChain does on
        // first call.
        for (int i = 0; i < 16; i++) chain.Process(input, output, ctx);

        // Measure thread-local bytes allocated across the hot loop on this
        // same thread. Immune to GCs triggered by other threads.
        var startBytes = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 10_000; i++)
        {
            chain.Process(input, output, ctx);
        }

        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - startBytes;
        Assert.Equal(0, allocatedBytes);
    }
}
