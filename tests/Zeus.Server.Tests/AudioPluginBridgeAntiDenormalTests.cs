using Zeus.Server;

namespace Zeus.Server.Tests;

/// <summary>
/// Unit coverage for <see cref="AudioPluginBridge.ApplyAntiDenormalFloor"/>, the
/// anti-denormal input conditioning that feeds the out-of-process VST engine
/// (zeus-umt6). The contract is: audible-level samples pass through bit-identical,
/// exact-zero / denormal-range inputs are lifted out of the float32 denormal
/// range, the perturbation is sub-audible, and the result is always finite.
/// </summary>
public class AudioPluginBridgeAntiDenormalTests
{
    // ~-300 dBFS — far below the -96 dBFS 16-bit wire floor, so it can never be
    // heard, yet ~23 orders of magnitude above the float32 smallest-normal.
    private const float MaxPerturbation = 1e-15f;
    private const float SmallestNormal = 1.17549435e-38f; // float.Epsilon is the smallest *denormal*, not this.

    [Fact]
    public void Silence_Is_Lifted_Out_Of_Denormal_Range_And_Alternates_Sign()
    {
        var src = new float[8]; // all exact zero
        var dst = new float[8];

        AudioPluginBridge.ApplyAntiDenormalFloor(src, dst);

        for (int i = 0; i < dst.Length; i++)
        {
            Assert.True(float.IsFinite(dst[i]));
            // Out of the denormal range (|x| >= smallest normal) so downstream
            // IIR/gate state can't rot to a denormal.
            Assert.True(MathF.Abs(dst[i]) >= SmallestNormal, $"index {i} still denormal: {dst[i]}");
            // Alternating sign: even +, odd -.
            Assert.Equal(i % 2 == 0 ? MaxPerturbation : -MaxPerturbation, dst[i]);
        }
    }

    [Fact]
    public void AudibleLevel_Samples_Are_BitIdentical()
    {
        // Any real-level sample: the 1e-15 floor is below float32 epsilon
        // relative to these magnitudes, so the add rounds away to a no-op.
        var src = new[] { 0.5f, -0.3f, 1.0f, -1.0f, 0.001f, -0.001f, 0.123456f, -0.999f };
        var dst = new float[src.Length];

        AudioPluginBridge.ApplyAntiDenormalFloor(src, dst);

        for (int i = 0; i < src.Length; i++)
            Assert.Equal(src[i], dst[i]); // exact float equality
    }

    [Fact]
    public void Denormal_Inputs_Are_Floored_To_Normal_Range()
    {
        // float.Epsilon is the smallest positive denormal; a handful of
        // denormal-range values that an un-conditioned engine could latch on.
        var src = new[] { float.Epsilon, -float.Epsilon, 1e-40f, -1e-42f, 1e-30f, -1e-30f, 0f, 0f };
        var dst = new float[src.Length];

        AudioPluginBridge.ApplyAntiDenormalFloor(src, dst);

        foreach (var v in dst)
        {
            Assert.True(float.IsFinite(v));
            Assert.True(MathF.Abs(v) >= SmallestNormal, $"value still denormal: {v}");
        }
    }

    [Fact]
    public void Perturbation_Is_SubAudible_For_Arbitrary_Signal()
    {
        var src = new float[256];
        for (int i = 0; i < src.Length; i++)
            src[i] = MathF.Sin(i * 0.17f) * 0.8f;
        var dst = new float[src.Length];

        AudioPluginBridge.ApplyAntiDenormalFloor(src, dst);

        for (int i = 0; i < src.Length; i++)
        {
            Assert.True(float.IsFinite(dst[i]));
            Assert.True(MathF.Abs(dst[i] - src[i]) <= MaxPerturbation,
                $"index {i} moved more than the anti-denormal floor");
        }
    }
}
