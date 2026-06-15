using Zeus.Dsp.Wdsp;

namespace Zeus.Dsp.Tests;

public sealed class WdspNativeLoaderTests
{
    [Fact]
    public void TryProbeExport_ReturnsFalseForMissingSymbol()
    {
        Assert.False(WdspNativeLoader.TryProbeExport("Zeus_DefinitelyMissingNativeExport"));
    }

    [Fact]
    public void NoiseReductionCapabilityProbes_DoNotThrow()
    {
        bool loadable = WdspDspEngine.NativeLibraryLoadable;
        bool post2 = WdspDspEngine.EmnrPost2Available;
        bool nr4 = WdspDspEngine.Nr4SbnrAvailable;

        if (!loadable)
        {
            Assert.False(post2);
            Assert.False(nr4);
        }
    }
}
