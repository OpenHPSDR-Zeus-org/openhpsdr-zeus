// SPDX-License-Identifier: GPL-2.0-or-later
//
// Covers the platform-aware Audio Tools affordance surface:
//   - the engine-install GET DTO's additive platform flags
//     (engineSupported / inProcessHostSupported / auSupported), which the
//     frontend reads to pick the Windows (download engine) vs macOS/Linux
//     (in-process AU/VST3 scan) path; and
//   - the AU scan endpoints (TX / RX / generic), which register installed
//     Audio Units in-process and are a no-op off macOS.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Zeus.Server.Tests;

public class AudioToolsPlatformAffordanceEndpointTests
    : IClassFixture<AudioToolsPlatformAffordanceEndpointTests.Factory>
{
    private readonly Factory _factory;
    public AudioToolsPlatformAffordanceEndpointTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task EngineInstallDtoCarriesPlatformFlags()
    {
        using var client = _factory.CreateClient();

        using var json = await client.GetFromJsonAsync<JsonDocument>(
            "/api/tx-audio-suite/vst-engine/install");

        Assert.NotNull(json);
        var root = json!.RootElement;

        // In-process hosting (native VST3 bridge, plus AU on macOS) is always
        // available — the additive flag the GUI uses to know it never *needs*
        // the engine download.
        Assert.True(root.GetProperty("inProcessHostSupported").GetBoolean());

        // The out-of-process engine is Windows-only by design; AU is macOS-only.
        Assert.Equal(OperatingSystem.IsWindows(), root.GetProperty("engineSupported").GetBoolean());
        Assert.Equal(OperatingSystem.IsMacOS(), root.GetProperty("auSupported").GetBoolean());

        // The engine-download path and the AU path are never both offered on the
        // same OS (Windows = engine, macOS = AU, Linux = neither/in-process VST3).
        Assert.False(
            root.GetProperty("engineSupported").GetBoolean()
            && root.GetProperty("auSupported").GetBoolean());
    }

    [Fact]
    public async Task GenericInstallDtoAliasCarriesPlatformFlags()
    {
        using var client = _factory.CreateClient();

        using var json = await client.GetFromJsonAsync<JsonDocument>(
            "/api/audio-suite/vst-engine/install");

        Assert.NotNull(json);
        var root = json!.RootElement;
        Assert.True(root.GetProperty("inProcessHostSupported").GetBoolean());
        Assert.Equal(OperatingSystem.IsWindows(), root.GetProperty("engineSupported").GetBoolean());
        Assert.Equal(OperatingSystem.IsMacOS(), root.GetProperty("auSupported").GetBoolean());
    }

    [Theory]
    [InlineData("/api/tx-audio-suite/scan-au")]
    [InlineData("/api/rx-audio-suite/scan-au")]
    [InlineData("/api/audio-suite/scan-au")]
    public async Task ScanAuEndpointsRespondAndReportPlatformSupport(string url)
    {
        using var client = _factory.CreateClient();

        // No body is required — AUs come from the OS registry, not a folder.
        var res = await client.PostAsJsonAsync(url, new { });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using var json = await res.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.NotNull(json);
        var root = json!.RootElement;

        // supported mirrors the OS; off macOS the scan is a no-op and registers
        // nothing.
        Assert.Equal(OperatingSystem.IsMacOS(), root.GetProperty("supported").GetBoolean());
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Empty(root.GetProperty("registered").EnumerateArray());
            Assert.Empty(root.GetProperty("skipped").EnumerateArray());
            Assert.Empty(root.GetProperty("errors").EnumerateArray());
        }
    }

    [Fact]
    public async Task ScanAuRejectsUnknownRoute()
    {
        using var client = _factory.CreateClient();

        var res = await client.PostAsJsonAsync(
            "/api/tx-audio-suite/scan-au", new { route = "sideways" });

        // Off macOS the scanner short-circuits before validating the route, so
        // a bad route is only surfaced on macOS where the scan actually runs.
        if (OperatingSystem.IsMacOS())
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        else
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    // Windows-parity regression for the editor-guard skip (Bug 1): the TX and
    // generic POST editor routes skip the "install the VST engine" guard only
    // when the in-process bridge natively hosts the plugin
    // (AudioPluginBridge.HostsPlugin). For an id the bridge does NOT host —
    // which is every engine-routed plugin on Windows, and any unknown id — the
    // guard must still fire and return the curated 409 "Download VST Engine"
    // message, exactly as before the macOS in-process fallback was added.
    [Theory]
    [InlineData("/api/tx-audio-suite/plugins/com.example.unhosted/editor")]
    [InlineData("/api/audio-suite/plugins/com.example.unhosted/editor")]
    public async Task EditorOpen_ForPluginTheBridgeDoesNotHost_StillReturnsEngineGuard(string url)
    {
        using var client = _factory.CreateClient();

        var res = await client.PostAsJsonAsync(url, new { });

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        using var json = await res.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.NotNull(json);
        var error = json!.RootElement.GetProperty("error").GetString();
        Assert.NotNull(error);
        Assert.Contains("VST engine", error, StringComparison.OrdinalIgnoreCase);
    }

    public sealed class Factory : IsolatedPrefsFactory
    {
    }
}
