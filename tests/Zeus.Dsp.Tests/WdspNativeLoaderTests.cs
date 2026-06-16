using System.Runtime.InteropServices;
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
        bool nr5Peak = WdspDspEngine.Nr5SpnrPeakDiagnosticsAvailable;
        bool nr5Agc = WdspDspEngine.Nr5SpnrAgcDiagnosticsAvailable;
        bool nr5Memory = WdspDspEngine.Nr5SpnrMemoryDiagnosticsAvailable;

        if (!loadable)
        {
            Assert.False(post2);
            Assert.False(nr4);
            Assert.False(nr5);
            Assert.False(nr5Advanced);
            Assert.False(nr5Deep);
            Assert.False(nr5Peak);
            Assert.False(nr5Agc);
            Assert.False(nr5Memory);
        }
    }

    [SkippableFact]
    public void WinX64RuntimeArtifact_ExportsNr4AndNr5()
    {
        Skip.IfNot(
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
            RuntimeInformation.ProcessArchitecture == Architecture.X64,
            "Packaged win-x64 WDSP artifact assertion only runs on Windows x64.");

        Assert.True(WdspDspEngine.NativeLibraryLoadable, "win-x64 wdsp.dll should load from Zeus.Dsp/runtimes/win-x64/native.");
        Assert.True(WdspDspEngine.Nr4SbnrAvailable, "win-x64 wdsp.dll should export NR4/SBNR symbols.");
        Assert.True(WdspDspEngine.Nr5SpnrAvailable, "win-x64 wdsp.dll should export NR5/SPNR symbols.");
        Assert.True(WdspDspEngine.Nr5SpnrAdvancedDiagnosticsAvailable, "win-x64 wdsp.dll should export NR5 advanced diagnostics.");
        Assert.True(WdspDspEngine.Nr5SpnrDeepDiagnosticsAvailable, "win-x64 wdsp.dll should export NR5 deep diagnostics.");
        Assert.True(WdspDspEngine.Nr5SpnrPeakDiagnosticsAvailable, "win-x64 wdsp.dll should export NR5 peak diagnostics.");
        Assert.True(WdspDspEngine.Nr5SpnrAgcDiagnosticsAvailable, "win-x64 wdsp.dll should export NR5 AGC recovery diagnostics.");
        Assert.True(WdspDspEngine.Nr5SpnrMemoryDiagnosticsAvailable, "win-x64 wdsp.dll should export NR5 weak-signal memory diagnostics.");
    }
}
