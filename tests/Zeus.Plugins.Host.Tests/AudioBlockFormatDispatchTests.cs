using System.Text.Json;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Audio;
using Zeus.Plugins.Host.Audio;

namespace Zeus.Plugins.Host.Tests;

/// <summary>
/// Freeze + dispatch regression coverage for the additive
/// <see cref="AudioBlock.Format"/> field. Proves a VST3 manifest is
/// unaffected by the AU addition: Format defaults to "vst3" when omitted,
/// and a default manifest still loads via a VST3 path (filesystem identity),
/// not an AU registry triple.
/// </summary>
// Sets the process-global NativeLoadEnabledOverride static in its ctor —
// serialise with the other load-gate tests so it can't clobber their override.
[Collection("LoadSensitive")]
public class AudioBlockFormatDispatchTests : IDisposable
{
    public AudioBlockFormatDispatchTests() => VstHostAudioPlugin.NativeLoadEnabledOverride = true;
    public void Dispose() => VstHostAudioPlugin.NativeLoadEnabledOverride = null;

    [Fact]
    public void LegacyManifest_OmittingFormat_DefaultsToVst3()
    {
        // An existing on-disk manifest with no "format" key must deserialise
        // to Format == "vst3" — full back-compat, no manifest reshaping.
        const string json = """
        {
          "vst3Path": "vst3/Fake.vst3",
          "slot": "tx.post-leveler",
          "channels": 1,
          "sampleRate": 48000
        }
        """;
        var audio = JsonSerializer.Deserialize<AudioBlock>(json);
        Assert.NotNull(audio);
        Assert.Equal("vst3", audio!.Format);
        Assert.Null(audio.AuComponentId);
        Assert.Equal("vst3/Fake.vst3", audio.Vst3Path);
    }

    [Fact]
    public void AuManifest_RoundTrips_FormatAndComponentId()
    {
        const string json = """
        {
          "format": "au",
          "auComponentId": "aufx:lpas:appl",
          "slot": "tx.post-leveler",
          "channels": 1,
          "sampleRate": 48000
        }
        """;
        var audio = JsonSerializer.Deserialize<AudioBlock>(json);
        Assert.NotNull(audio);
        Assert.Equal("au", audio!.Format);
        Assert.Equal("aufx:lpas:appl", audio.AuComponentId);
        Assert.Null(audio.Vst3Path);
    }

    [Fact]
    public async Task DefaultFormatManifest_LoadsViaVst3Path_NotAuIdentity()
    {
        // A default (Format == "vst3") manifest must take the VST3 path: the
        // load identity is the resolved filesystem path, and the missing-file
        // guard fires (unlike AU, which skips it). This is the regression that
        // proves the VST3 dispatch is unchanged by the AU addition.
        var bridge = new FakeBridge();
        var defaultManifest = new AudioBlock
        {
            // Format intentionally not set → defaults to "vst3".
            Vst3Path = "vst3/Fake.vst3",
            Slot = "tx.post-leveler",
            Channels = 1,
            SampleRate = 48000,
        };
        Assert.Equal("vst3", defaultManifest.Format);

        var pluginDir = Path.GetTempPath();
        var vst3Abs = Path.Combine(pluginDir, "vst3", "Fake.vst3");
        Directory.CreateDirectory(Path.GetDirectoryName(vst3Abs)!);
        File.WriteAllText(vst3Abs, "stub");
        try
        {
            var plugin = new VstHostAudioPlugin(bridge, defaultManifest, pluginDir, "FakeFx");
            await plugin.InitializeAudioAsync(new StubHost(), default);

            // The loaded identity is the resolved absolute VST3 path, NOT a
            // bare AU triple — proves the VST3 branch was taken.
            Assert.Equal(Path.GetFullPath(vst3Abs), Path.GetFullPath(bridge.LastLoadPath!));
        }
        finally { File.Delete(vst3Abs); }
    }

    private sealed class FakeBridge : IVstBridgeNative
    {
        public string? LastLoadPath;
        public int Init(int abi) => VstBridgeStatus.Ok;
        public int LoadVst3(string path, int channels, int sampleRate, int blockSize, out nint handle)
        { LastLoadPath = path; handle = 0xABCD; return VstBridgeStatus.Ok; }
        public int Process(nint handle, ReadOnlySpan<float> input, Span<float> output, int frames)
        { input.CopyTo(output); return VstBridgeStatus.Ok; }
        public int SetParameter(nint handle, uint paramId, double normalized) => VstBridgeStatus.Ok;
        public int Unload(nint handle) => VstBridgeStatus.Ok;
        public int Shutdown() => VstBridgeStatus.Ok;
        public int EditorOpen(nint handle, string title) => VstBridgeStatus.NotImplemented;
        public int EditorClose(nint handle) => VstBridgeStatus.Ok;
        public bool EditorIsOpen(nint handle) => false;
    }

    private sealed class StubHost : IAudioHost
    {
        public int CurrentSampleRate => 48000;
        public int CurrentChannels => 1;
        public int CurrentBlockSize => 1024;
        public string Slot => "tx.post-leveler";
    }
}
