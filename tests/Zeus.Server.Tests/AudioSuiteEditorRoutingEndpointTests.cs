// SPDX-License-Identifier: GPL-2.0-or-later
//
// HTTP-level regression tests for the editor-routing fix (Bug 1): the editor
// open routes must fall through to the in-process AudioPluginBridge, which is
// the ONLY host on macOS for AU/VST3 plugins (and the in-process host for RX
// VST3 elsewhere). Two failures were live-reproduced before the fix:
//   * POST /api/rx-audio-suite/plugins/{id}/editor  -> 404 "No such plugin"
//     even though the bridge hosts the plugin (the RX route never consulted it).
//   * POST /api/tx-audio-suite/plugins/{id}/editor  -> 409 "Download VST Engine"
//     for a bridge-hosted in-process plugin (the engine guard ran before the
//     bridge fallback).
//
// These tests seed the running app's singleton AudioPluginBridge with a
// VstHostAudioPlugin whose native side is a no-op (NoopVstBridge) and whose
// handle is forced non-zero, so IsNativelyLoaded -> true makes the bridge host
// the id WITHOUT any real native P/Invoke — deterministic and safe on every
// platform/CI. The Windows-preservation counterpart (an id the bridge does NOT
// host still gets the engine guard) lives in
// AudioToolsPlatformAffordanceEndpointTests.

using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Audio;
using Zeus.Plugins.Contracts.Extensions;
using Zeus.Plugins.Host.Audio;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class AudioSuiteEditorRoutingEndpointTests
{
    // Bug 1 (RX): an in-process-hosted plugin in the RX chain must route to the
    // bridge and open in-process — NOT dead-end at 404 "No such plugin".
    [Fact]
    public async Task RxEditorOpen_ForBridgeHostedPlugin_RoutesToBridge_NotNotFound()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();
        const string id = "com.openhpsdr.zeus.au.rx.fake";
        SeedNativelyLoadedPlugin(factory, id, "rx.post-demod", rx: true);

        var res = await client.PostAsJsonAsync(
            $"/api/rx-audio-suite/plugins/{id}/editor", new { });

        // The route fell through to the bridge (which hosts the plugin), so it
        // opens in-process rather than 404-ing as "no such plugin".
        Assert.NotEqual(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var json = await res.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.NotNull(json);
        Assert.True(json!.RootElement.GetProperty("open").GetBoolean());
    }

    // Bug 1 (TX): a bridge-hosted in-process plugin must skip the "install the
    // VST engine" guard (the engine is inactive and the default mode is Native)
    // and open via the bridge — NOT be blocked by the 409 engine guard.
    [Theory]
    [InlineData("/api/tx-audio-suite/plugins/{0}/editor")]
    [InlineData("/api/audio-suite/plugins/{0}/editor")] // generic alias, same guard
    public async Task TxEditorOpen_ForBridgeHostedPlugin_SkipsEngineGuard(string urlTemplate)
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();
        const string id = "com.openhpsdr.zeus.au.tx.fake";
        SeedNativelyLoadedPlugin(factory, id, "tx.post-leveler", rx: false);

        var res = await client.PostAsJsonAsync(
            string.Format(urlTemplate, id), new { });

        // HostsPlugin(id) is true, so VstEngineEditorGuard is skipped and the
        // open proceeds through the bridge — never the curated 409 download hint.
        Assert.NotEqual(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var json = await res.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.NotNull(json);
        Assert.True(json!.RootElement.GetProperty("open").GetBoolean());
    }

    // -- harness ---------------------------------------------------------

    // Inject a no-op-native, force-loaded VST host plugin straight into the
    // running app's singleton bridge map so the endpoint resolves it as a
    // natively-loaded, in-process-hosted plugin without touching a real dylib.
    private static void SeedNativelyLoadedPlugin(
        Factory factory, string id, string slot, bool rx)
    {
        var bridge = factory.Services.GetRequiredService<AudioPluginBridge>();
        var plugin = new VstHostAudioPlugin(
            new NoopVstBridge(),
            new AudioBlock { Vst3Path = "Fake.vst3", Slot = slot },
            Path.GetTempPath(),
            "Fake AU");
        SetHandle(plugin, 1); // IsNativelyLoaded => true, so HostsPlugin => true

        var fieldName = rx ? "_rxIdToPlugin" : "_idToPlugin";
        var map = PrivateField<Dictionary<string, IAudioPlugin>>(bridge, fieldName);
        map[id] = plugin;
    }

    private static void SetHandle(VstHostAudioPlugin plugin, nint handle)
    {
        var field = typeof(VstHostAudioPlugin).GetField(
            "_handle", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(plugin, handle);
    }

    private static T PrivateField<T>(object target, string name)
    {
        var field = target.GetType().GetField(
            name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<T>(field!.GetValue(target));
    }

    private sealed class NoopVstBridge : IVstBridgeNative
    {
        public int Init(int abi) => VstBridgeStatus.Ok;

        public int LoadVst3(string path, int channels, int sampleRate, int blockSize, out nint handle)
        {
            handle = 0;
            return VstBridgeStatus.Ok;
        }

        public int Process(nint handle, ReadOnlySpan<float> input, Span<float> output, int frames)
        {
            input.CopyTo(output);
            return VstBridgeStatus.Ok;
        }

        public int SetParameter(nint handle, uint paramId, double normalized) => VstBridgeStatus.Ok;
        public int Unload(nint handle) => VstBridgeStatus.Ok;
        public int Shutdown() => VstBridgeStatus.Ok;
        public int EditorOpen(nint handle, string title) => VstBridgeStatus.Ok;
        public int EditorClose(nint handle) => VstBridgeStatus.Ok;
        public bool EditorIsOpen(nint handle) => false;
    }

    private sealed class Factory : IsolatedPrefsFactory
    {
    }
}
