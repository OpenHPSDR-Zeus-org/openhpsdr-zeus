// SPDX-License-Identifier: GPL-2.0-or-later
//
// Opening a plugin editor when no host will load it must point the operator at
// the actual fix (install/route the out-of-process engine via "Download VST
// Engine" + VST mode) — NOT the in-process "set ZEUS_ENABLE_VST_LOAD=1" hint, a
// developer-only escape hatch that sends a new operator down the wrong (and
// unsafe) path. This applies to VST mode with no routing engine AND to Native
// mode when the in-process bridge won't host the plugin (TX native load is
// opt-in). Native mode WITH in-process load available keeps the normal path.

using Zeus.Server;

namespace Zeus.Server.Tests;

public class VstEditorEngineHintTests
{
    [Fact]
    public void VstMode_EngineMissing_PointsAtDownloadNotEnvVar()
    {
        var msg = VstEditorHint.EngineUnavailableMessage(
            AudioProcessingMode.Vst, engineActive: false, engineInstalled: false);

        Assert.NotNull(msg);
        Assert.Contains("Download VST Engine", msg);
        Assert.DoesNotContain("ZEUS_ENABLE_VST_LOAD", msg);
    }

    [Fact]
    public void VstMode_EngineInstalledButIdle_SaysWaitNotDownload()
    {
        var msg = VstEditorHint.EngineUnavailableMessage(
            AudioProcessingMode.Vst, engineActive: false, engineInstalled: true);

        Assert.NotNull(msg);
        Assert.Contains("routing yet", msg);
        Assert.DoesNotContain("Download VST Engine", msg);
        Assert.DoesNotContain("ZEUS_ENABLE_VST_LOAD", msg);
    }

    [Fact]
    public void VstMode_EngineActive_NoGuard()
    {
        // Engine is routing — the editor open should be attempted normally.
        Assert.Null(VstEditorHint.EngineUnavailableMessage(
            AudioProcessingMode.Vst, engineActive: true, engineInstalled: true));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NativeMode_WithInProcessLoad_NeverGuards(bool engineActive, bool engineInstalled)
    {
        // Native + the in-process bridge able to host the plugin (RX VSTs, or TX
        // with ZEUS_ENABLE_VST_LOAD=1): the in-process editor path is correct, so
        // the guard must never hijack it regardless of engine presence.
        Assert.Null(VstEditorHint.EngineUnavailableMessage(
            AudioProcessingMode.Native, engineActive, engineInstalled, nativeLoadEnabled: true));
    }

    [Fact]
    public void NativeMode_TxLoadGatedOff_EngineMissing_PointsAtDownloadAndVstMode()
    {
        // Fresh PC, Native mode (default), TX native load gated off (safe default):
        // the in-process editor can't open. Guide to the engine + VST mode, never
        // the dangerous dev hatch.
        var msg = VstEditorHint.EngineUnavailableMessage(
            AudioProcessingMode.Native, engineActive: false, engineInstalled: false,
            nativeLoadEnabled: false);

        Assert.NotNull(msg);
        Assert.Contains("Download VST Engine", msg);
        Assert.Contains("VST", msg);
        Assert.DoesNotContain("ZEUS_ENABLE_VST_LOAD", msg);
    }

    [Fact]
    public void NativeMode_TxLoadGatedOff_EngineInstalled_SaysSwitchToVstMode()
    {
        // Engine already downloaded but operator still in Native mode: tell them to
        // switch processing mode to VST, not to re-download.
        var msg = VstEditorHint.EngineUnavailableMessage(
            AudioProcessingMode.Native, engineActive: false, engineInstalled: true,
            nativeLoadEnabled: false);

        Assert.NotNull(msg);
        Assert.Contains("VST", msg);
        Assert.DoesNotContain("Download VST Engine", msg);
        Assert.DoesNotContain("ZEUS_ENABLE_VST_LOAD", msg);
    }
}
