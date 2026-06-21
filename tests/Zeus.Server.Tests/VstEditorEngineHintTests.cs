// SPDX-License-Identifier: GPL-2.0-or-later
//
// When the operator has selected the out-of-process VST route but no engine is
// routing, opening a plugin editor must point them at the actual fix (install
// the engine via "Download VST Engine") — NOT the in-process
// "set ZEUS_ENABLE_VST_LOAD=1" hint, which is irrelevant to VST mode and sends
// a new operator down the wrong path. Native mode keeps the in-process hint.

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
    public void NativeMode_NeverGuards(bool engineActive, bool engineInstalled)
    {
        // Native is the in-process editor path; the VST-engine guard must never
        // hijack it regardless of engine presence.
        Assert.Null(VstEditorHint.EngineUnavailableMessage(
            AudioProcessingMode.Native, engineActive, engineInstalled));
    }
}
