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
        bool nr5 = WdspDspEngine.Nr5SpnrAvailable;
        bool nr5Advanced = WdspDspEngine.Nr5SpnrAdvancedDiagnosticsAvailable;
        bool nr5Deep = WdspDspEngine.Nr5SpnrDeepDiagnosticsAvailable;

        if (!loadable)
        {
            Assert.False(post2);
            Assert.False(nr4);
            Assert.False(nr5);
            Assert.False(nr5Advanced);
            Assert.False(nr5Deep);
        }
    }
}
