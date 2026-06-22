// SPDX-License-Identifier: GPL-2.0-or-later
//
// CapabilitiesService — single source of truth for the /api/capabilities
// endpoint. Captures host-mode, platform / architecture, and per-feature
// availability once at construction and serves the same snapshot for the
// lifetime of the process.
//
// Probe-once-at-startup is deliberate. The frontend caches the response
// anyway. Feature-gate fields will be reintroduced as the new plugin
// system lands; the FeatureMatrix is kept as an empty record so callers
// can rely on a stable JSON shape.

using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Http;

namespace Zeus.Server;

public sealed class CapabilitiesService
{
    private readonly CapabilitiesSnapshot _snapshot;
    private readonly bool _shareOverLan;

    public CapabilitiesService(ZeusHostOptions options)
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";

        var platform = DetectPlatform();
        var architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

        var httpsPort = options.UseHttpsLanCert
            ? (options.HttpsPort > 0 ? options.HttpsPort : LanCertificate.GetHttpsPort())
            : 0;
        var lanHttpsUrls = httpsPort > 0
            ? LanCertificate.GetLanIps()
                .Select(ip => $"https://{ip}:{httpsPort}")
                .ToArray()
            : Array.Empty<string>();

        _snapshot = new CapabilitiesSnapshot(
            Host: options.HostMode == ZeusHostMode.Desktop ? "desktop" : "server",
            Platform: platform,
            Architecture: architecture,
            Version: version,
            LanHttpsUrls: lanHttpsUrls,
            Features: new FeatureMatrix(Vst: VstSupported(), VstEditor: VstEditorSupported()));

        _shareOverLan = options.HostMode == ZeusHostMode.Desktop && options.ShareOverLan;
    }

    public CapabilitiesSnapshot Snapshot() => _snapshot;

    /// <summary>
    /// Per-request snapshot. When the host is desktop + ShareOverLan, the
    /// captured "desktop" host string is overridden to "server" for non-loopback
    /// requests so a LAN browser enables its WS audio decoder + mic uplink, while
    /// the Photino webview (loopback) keeps "desktop" and the native miniaudio
    /// path. Without this distinction Photino would double-play (miniaudio AND
    /// the browser audio worklet) or LAN browsers would be silent.
    /// </summary>
    public CapabilitiesSnapshot Snapshot(HttpContext ctx)
    {
        if (!_shareOverLan) return _snapshot;

        var local = ctx.Connection.LocalIpAddress;
        var isLoopback = local is not null && IPAddress.IsLoopback(local);
        if (isLoopback) return _snapshot;

        return _snapshot with { Host = "server" };
    }

    private static string DetectPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "linux";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "darwin";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "windows";
        return "unknown";
    }

    // In-process VST3 hosting (libzeus-vst-bridge) ships for Windows, Linux and
    // macOS — the realtime chain is platform-agnostic, so VST audio is available
    // wherever the native bridge loads. The frontend uses this to expose the
    // Audio Suite VST affordances instead of treating VST as Windows-only.
    private static bool VstSupported()
        => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        || RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
        || RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    // Plugin editors are native GUI windows: always available on Windows/macOS;
    // on Linux only when a display server is reachable (the bridge opens an X11
    // window — headless has nowhere to show it).
    private static bool VstEditorSupported()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return true;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return true;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
                || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
        return false;
    }
}

// JSON shape returned by /api/capabilities. Property names land lower-case
// on the wire via the default minimal-API camel-case policy, matching the
// rest of the Zeus REST surface.

public sealed record CapabilitiesSnapshot(
    string Host,
    string Platform,
    string Architecture,
    string Version,
    IReadOnlyList<string> LanHttpsUrls,
    FeatureMatrix Features);

public sealed record FeatureMatrix(bool Vst = false, bool VstEditor = false);
