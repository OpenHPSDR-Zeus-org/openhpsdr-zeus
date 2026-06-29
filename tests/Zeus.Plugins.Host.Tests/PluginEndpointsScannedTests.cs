// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Plugins.Host;

namespace Zeus.Plugins.Host.Tests;

/// <summary>
/// The Settings ▸ Plugins list shows only Zeus plugin-repo plugins. Operator-
/// scanned VST3 / Audio Unit effects (registered into the Audio Suite rack via
/// the directory / AU scanners) carry the reserved id namespaces and must be
/// flagged <c>Scanned</c> so the frontend filters them out — while native Zeus
/// audio plugins (the <c>samples.*</c> chain) stay in the list.
/// </summary>
public sealed class PluginEndpointsScannedTests
{
    [Theory]
    // Scanned VST3 — TX + RX namespaces.
    [InlineData("com.openhpsdr.zeus.vst.tdrnova", true)]
    [InlineData("com.openhpsdr.zeus.rxvst.rnnoise", true)]
    // Scanned Audio Units — TX + RX namespaces.
    [InlineData("com.openhpsdr.zeus.au.fabfilterproq3", true)]
    [InlineData("com.openhpsdr.zeus.rxau.clear", true)]
    // Native Zeus audio plugins shipped via the plugin repo — NOT scanned.
    [InlineData("com.openhpsdr.zeus.samples.eq", false)]
    [InlineData("com.openhpsdr.zeus.samples.amplifier", false)]
    // Ordinary repo plugins / arbitrary ids — NOT scanned.
    [InlineData("com.example.cooltool", false)]
    [InlineData("com.openhpsdr.zeus.rf2k", false)]
    [InlineData("", false)]
    public void IsScannedAudioPlugin_classifies_by_id_namespace(string id, bool expected)
    {
        Assert.Equal(expected, PluginEndpoints.IsScannedAudioPlugin(id));
    }
}
