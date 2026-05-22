using Zeus.Plugins.Contracts.Audio;
using Zeus.Plugins.Contracts.Extensions;
using Zeus.Plugins.Host.Audio;

namespace Zeus.Plugins.Host.Tests;

public class AudioChainTests
{
    private static AudioBlockContext Ctx(int frames = 4, int ch = 1)
        => new(sampleRate: 48000, channels: ch, frames: frames, sampleTime: 0, mox: false);

    [Fact]
    public void Empty_Chain_With_Master_On_PassesThrough()
    {
        var chain = new AudioChain();
        Span<float> input  = stackalloc float[] { 1, 2, 3, 4 };
        Span<float> output = stackalloc float[4];

        chain.Process(input, output, Ctx());

        Assert.Equal<float>(input.ToArray(), output.ToArray());
    }

    [Fact]
    public void MasterBypassed_AlwaysPassesThrough()
    {
        var chain = new AudioChain { MasterBypassed = true };
        chain.SetSlot(0, new AddPlugin(10f));

        Span<float> input  = stackalloc float[] { 1, 2, 3, 4 };
        Span<float> output = stackalloc float[4];

        chain.Process(input, output, Ctx());
        Assert.Equal<float>(new float[] { 1, 2, 3, 4 }, output.ToArray());
    }

    [Fact]
    public void SingleSlot_Applies_Once()
    {
        var chain = new AudioChain();
        chain.SetSlot(0, new AddPlugin(10f));

        Span<float> input  = stackalloc float[] { 1, 2, 3, 4 };
        Span<float> output = stackalloc float[4];

        chain.Process(input, output, Ctx());

        Assert.Equal<float>(new float[] { 11, 12, 13, 14 }, output.ToArray());
    }

    [Fact]
    public void TwoSlots_Compose_Left_To_Right()
    {
        var chain = new AudioChain();
        chain.SetSlot(0, new AddPlugin(10f));
        chain.SetSlot(1, new MultiplyPlugin(2f));

        Span<float> input  = stackalloc float[] { 1, 2, 3, 4 };
        Span<float> output = stackalloc float[4];

        chain.Process(input, output, Ctx());

        // (x + 10) * 2
        Assert.Equal<float>(new float[] { 22, 24, 26, 28 }, output.ToArray());
    }

    [Fact]
    public void ThreeSlots_With_Middle_Bypassed_Skips_Cleanly()
    {
        var chain = new AudioChain();
        chain.SetSlot(0, new AddPlugin(10f));
        chain.SetSlot(1, new MultiplyPlugin(100f));
        chain.SetSlot(2, new AddPlugin(1f));
        chain.SetSlotBypass(1, true);

        Span<float> input  = stackalloc float[] { 1, 2 };
        Span<float> output = stackalloc float[2];
        chain.Process(input, output, Ctx(frames: 2));

        // (x + 10) [bypass *100] + 1 = x + 11
        Assert.Equal<float>(new float[] { 12, 13 }, output.ToArray());
    }

    [Fact]
    public void Empty_Slots_Between_Active_Are_Skipped()
    {
        var chain = new AudioChain();
        chain.SetSlot(0, new AddPlugin(1f));
        // slots 1..5 empty
        chain.SetSlot(6, new MultiplyPlugin(2f));

        Span<float> input  = stackalloc float[] { 1 };
        Span<float> output = stackalloc float[1];

        chain.Process(input, output, Ctx(frames: 1));
        Assert.Equal(4f, output[0]); // (1+1) * 2
    }

    [Fact]
    public void SlotCount_IsEightByContract()
    {
        Assert.Equal(8, new AudioChain().SlotCount);
        Assert.Equal(8, AudioChain.MaxSlots);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(8)]
    [InlineData(99)]
    public void OutOfRange_SlotIndex_Throws(int idx)
    {
        var chain = new AudioChain();
        Assert.Throws<ArgumentOutOfRangeException>(() => chain.SetSlot(idx, new AddPlugin(0f)));
    }

    [Fact]
    public void SetSlot_Replaces_Previous_Plugin()
    {
        var chain = new AudioChain();
        var p1 = new AddPlugin(1f);
        var p2 = new AddPlugin(2f);
        chain.SetSlot(0, p1);
        chain.SetSlot(0, p2);
        Assert.Same(p2, chain.GetSlot(0));
    }

    [Fact]
    public void ClearSlot_ResetsBypassFlag()
    {
        var chain = new AudioChain();
        chain.SetSlot(0, new AddPlugin(1f));
        chain.SetSlotBypass(0, true);
        chain.ClearSlot(0);
        Assert.False(chain.IsSlotBypassed(0));
    }

    // -- Caller-supplied-scratch overload -----------------------------

    [Fact]
    public void Scratch_Overload_Matches_Field_Backed_Process()
    {
        // Bit-identity check: running the same input through both
        // overloads (field-backed and caller-supplied scratch) must
        // produce the same output. This is the contract that lets
        // AudioPluginBridge.ProcessLivePreview tap the chain with its
        // own scratch span without diverging from the WDSP TX path.
        var chainA = new AudioChain();
        var chainB = new AudioChain();
        chainA.SetSlot(0, new AddPlugin(10f));
        chainA.SetSlot(1, new MultiplyPlugin(3f));
        chainA.SetSlot(2, new AddPlugin(-2f));
        chainB.SetSlot(0, new AddPlugin(10f));
        chainB.SetSlot(1, new MultiplyPlugin(3f));
        chainB.SetSlot(2, new AddPlugin(-2f));

        Span<float> input = stackalloc float[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        Span<float> outA = stackalloc float[8];
        Span<float> outB = stackalloc float[8];
        Span<float> scratchB = stackalloc float[8];

        chainA.Process(input, outA, Ctx(frames: 8));
        chainB.Process(input, outB, scratchB, Ctx(frames: 8));

        Assert.Equal<float>(outA.ToArray(), outB.ToArray());
    }

    [Fact]
    public void Scratch_Overload_MasterBypassed_PassesThrough()
    {
        var chain = new AudioChain { MasterBypassed = true };
        chain.SetSlot(0, new AddPlugin(10f));

        Span<float> input   = stackalloc float[] { 1, 2, 3, 4 };
        Span<float> output  = stackalloc float[4];
        Span<float> scratch = stackalloc float[4];

        chain.Process(input, output, scratch, Ctx());
        Assert.Equal<float>(new float[] { 1, 2, 3, 4 }, output.ToArray());
    }

    [Fact]
    public void Scratch_Overload_TooSmall_PassesThrough_Without_Throwing()
    {
        // Safety contract: if the caller hands in a scratch span smaller
        // than needed, the chain must NOT throw on the realtime path —
        // it falls back to a single input.CopyTo(output) so audio
        // keeps flowing. Throwing would kill the miniaudio worker
        // thread.
        var chain = new AudioChain();
        chain.SetSlot(0, new AddPlugin(99f)); // would be applied if scratch were big enough

        Span<float> input   = stackalloc float[] { 1, 2, 3, 4 };
        Span<float> output  = stackalloc float[4];
        Span<float> scratch = stackalloc float[2]; // intentionally too small

        chain.Process(input, output, scratch, Ctx());

        // Plugin was bypassed via the safety fallback; output mirrors input.
        Assert.Equal<float>(new float[] { 1, 2, 3, 4 }, output.ToArray());
    }

    [Fact]
    public void Scratch_Overload_OutputTooSmall_Throws()
    {
        // Output-too-small is a *programmer* error, not a runtime
        // condition the audio thread could survive — throw clearly.
        // This matches the field-backed overload's behaviour.
        var chain = new AudioChain();

        var input   = new float[] { 1, 2, 3, 4 };
        var output  = new float[2];
        var scratch = new float[4];

        Assert.Throws<ArgumentException>(() =>
        {
            chain.Process(input.AsSpan(), output.AsSpan(), scratch.AsSpan(), Ctx());
        });
    }

    [Fact]
    public void Scratch_Overload_TwoIndependent_Scratches_Produce_Same_Output()
    {
        // Future-proofing for concurrent callers: even if two threads
        // call Process on the same chain with separate scratches, the
        // resulting output must be deterministic given identical input
        // / plugin state. (Per-plugin internal state can still race,
        // but the scratch-pong logic itself must not corrupt output.)
        var chain = new AudioChain();
        chain.SetSlot(0, new AddPlugin(5f));
        chain.SetSlot(1, new MultiplyPlugin(2f));

        Span<float> input   = stackalloc float[] { 1, 2, 3, 4 };
        Span<float> out1    = stackalloc float[4];
        Span<float> out2    = stackalloc float[4];
        Span<float> scratch1 = stackalloc float[4];
        Span<float> scratch2 = stackalloc float[4];

        chain.Process(input, out1, scratch1, Ctx());
        chain.Process(input, out2, scratch2, Ctx());

        Assert.Equal<float>(out1.ToArray(), out2.ToArray());
        Assert.Equal<float>(new float[] { 12, 14, 16, 18 }, out1.ToArray()); // (x+5)*2
    }

    private sealed class AddPlugin : IAudioPlugin
    {
        private readonly float _bias;
        public AddPlugin(float bias) => _bias = bias;
        public string DisplayName => $"add+{_bias}";
        public AudioPluginRequirements Requirements => new(48000, 1, 256);
        public Task InitializeAudioAsync(IAudioHost host, CancellationToken ct) => Task.CompletedTask;
        public Task ShutdownAudioAsync(CancellationToken ct) => Task.CompletedTask;
        public void Process(ReadOnlySpan<float> input, Span<float> output, AudioBlockContext ctx)
        {
            for (int i = 0; i < input.Length; i++) output[i] = input[i] + _bias;
        }
    }

    private sealed class MultiplyPlugin : IAudioPlugin
    {
        private readonly float _k;
        public MultiplyPlugin(float k) => _k = k;
        public string DisplayName => $"mul*{_k}";
        public AudioPluginRequirements Requirements => new(48000, 1, 256);
        public Task InitializeAudioAsync(IAudioHost host, CancellationToken ct) => Task.CompletedTask;
        public Task ShutdownAudioAsync(CancellationToken ct) => Task.CompletedTask;
        public void Process(ReadOnlySpan<float> input, Span<float> output, AudioBlockContext ctx)
        {
            for (int i = 0; i < input.Length; i++) output[i] = input[i] * _k;
        }
    }
}
