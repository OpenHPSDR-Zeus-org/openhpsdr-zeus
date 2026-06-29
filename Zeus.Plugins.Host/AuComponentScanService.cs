// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Host.Audio;

namespace Zeus.Plugins.Host;

/// <summary>
/// macOS-only sibling of <see cref="VstDirectoryScanService"/>: enumerates
/// installed AUv2 effect ('aufx') Audio Units from the OS AudioComponent
/// registry (via the native AU bridge) and registers each as an installed
/// Zeus plugin so it appears in the Audio Suite chain. Unlike VST3 — which
/// scans an operator-chosen filesystem directory — Audio Units are resolved
/// from the system registry, so there is no directory argument.
///
/// Each discovered AU becomes a generated plugin package under the plugin
/// root, reusing the EXACT synthesised-package + VstHostStub.dll loader
/// trick the VST3 scanner uses; only the manifest differs (<c>format:"au"</c>
/// + <c>auComponentId</c> instead of <c>vst3Path</c>). The id-prefix scheme
/// is extended to <c>.au.*</c> / <c>.rxau.*</c> so AU plugins never collide
/// with scanned VST3 ids.
///
/// The whole service is a no-op off macOS: <see cref="ScanAsync"/> returns an
/// empty result without touching the native bridge, so nothing AU-shaped ever
/// runs on Windows/Linux.
/// </summary>
public sealed class AuComponentScanService
{
    private const string StubResourceName = "Zeus.Plugins.VstHostStub.dll";
    private const string StubAssemblyFile = "Zeus.Plugins.VstHostStub.dll";
    private const string IdPrefix = "com.openhpsdr.zeus.au.";
    private const string RxIdPrefix = "com.openhpsdr.zeus.rxau.";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly PluginManager _manager;
    private readonly string _pluginRoot;
    private readonly ILogger<AuComponentScanService> _log;
    private readonly Func<IReadOnlyList<AuBridgeNative.AuEffect>> _enumerate;

    public AuComponentScanService(
        PluginManager manager,
        string pluginRoot,
        ILogger<AuComponentScanService> log)
        : this(manager, pluginRoot, log, () => new AuBridgeNative().EnumerateEffects()) { }

    // Test seam — inject a fake enumerator so the scanner can be exercised
    // without the native dylib.
    internal AuComponentScanService(
        PluginManager manager,
        string pluginRoot,
        ILogger<AuComponentScanService> log,
        Func<IReadOnlyList<AuBridgeNative.AuEffect>> enumerate)
    {
        _manager = manager;
        _pluginRoot = pluginRoot;
        _log = log;
        _enumerate = enumerate;
    }

    public sealed record ScannedAu(string Id, string Name, string ComponentId);
    public sealed record ScanError(string ComponentId, string Message);
    public sealed record ScanResult(
        IReadOnlyList<ScannedAu> Registered,
        IReadOnlyList<ScannedAu> Skipped,
        IReadOnlyList<ScanError> Errors);

    /// <summary>
    /// Enumerate installed AU effects and register each. The optional
    /// <paramref name="route"/> ("auto" | "tx" | "rx" | "both") mirrors the
    /// VST3 scanner; defaults to TX insert. Returns an empty result off macOS.
    /// </summary>
    public async Task<ScanResult> ScanAsync(string? route, CancellationToken ct)
    {
        if (!OperatingSystem.IsMacOS())
            return new ScanResult([], [], []);

        var routes = ResolveRoutes(route);
        var root = _pluginRoot;
        Directory.CreateDirectory(root);

        var registered = new List<ScannedAu>();
        var skipped = new List<ScannedAu>();
        var errors = new List<ScanError>();
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        var activeIds = new HashSet<string>(
            _manager.Active.Select(p => p.Loaded.Manifest.Id), StringComparer.Ordinal);

        IReadOnlyList<AuBridgeNative.AuEffect> effects;
        try { effects = _enumerate(); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AU enumeration failed");
            return new ScanResult([], [], [new ScanError("", ex.Message)]);
        }

        foreach (var fx in effects)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(fx.Id)) continue;
            var name = string.IsNullOrWhiteSpace(fx.Name) ? fx.Id : fx.Name;

            foreach (var routeInfo in routes)
            {
                var routeName = routeInfo.DisplaySuffix is null ? name : $"{name} {routeInfo.DisplaySuffix}";
                var slot = routeInfo.Slot;
                var id = StableUniqueId(name, fx.Id, usedIds, routeInfo.IdPrefix);
                usedIds.Add(id);

                if (activeIds.Contains(id))
                {
                    skipped.Add(new ScannedAu(id, routeName, fx.Id));
                    continue;
                }

                try
                {
                    var pluginDir = Path.Combine(root, id);
                    Directory.CreateDirectory(pluginDir);
                    WriteStubAssembly(Path.Combine(pluginDir, StubAssemblyFile));
                    await File.WriteAllTextAsync(
                        Path.Combine(pluginDir, "plugin.json"),
                        BuildManifestJson(id, routeName, fx.Id, slot),
                        ct).ConfigureAwait(false);
                    await _manager.ActivateAsync(pluginDir, ct).ConfigureAwait(false);
                    registered.Add(new ScannedAu(id, routeName, fx.Id));
                    _log.LogInformation("Registered AU '{Name}' as {Id} (component={Component}, route={Route})",
                        routeName, id, fx.Id, slot);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Failed to register AU {Name} ({Component})", routeName, fx.Id);
                    errors.Add(new ScanError(fx.Id, ex.Message));
                }
            }
        }

        return new ScanResult(registered, skipped, errors);
    }

    public static bool IsTxPluginId(string pluginId) =>
        pluginId.StartsWith(IdPrefix, StringComparison.Ordinal);

    public static bool IsRxPluginId(string pluginId) =>
        pluginId.StartsWith(RxIdPrefix, StringComparison.Ordinal);

    private sealed record AuRouteRegistration(string Slot, string IdPrefix, string? DisplaySuffix);

    private static IReadOnlyList<AuRouteRegistration> ResolveRoutes(string? route)
    {
        var normalized = (route ?? "auto").Trim().ToLowerInvariant();
        return normalized switch
        {
            "auto" or "" or "tx" => [new AuRouteRegistration("tx.post-leveler", IdPrefix, null)],
            "rx" => [new AuRouteRegistration("rx.post-demod", RxIdPrefix, "RX")],
            "both" =>
            [
                new AuRouteRegistration("tx.post-leveler", IdPrefix, "TX"),
                new AuRouteRegistration("rx.post-demod", RxIdPrefix, "RX"),
            ],
            _ => throw new ArgumentException("route must be 'auto', 'tx', 'rx', or 'both'"),
        };
    }

    private static void WriteStubAssembly(string destPath)
    {
        using var res = typeof(AuComponentScanService).Assembly
            .GetManifestResourceStream(StubResourceName)
            ?? throw new InvalidOperationException(
                $"embedded stub resource '{StubResourceName}' not found");
        using var file = File.Create(destPath);
        res.CopyTo(file);
    }

    private static string BuildManifestJson(string id, string name, string auComponentId, string slot)
    {
        var manifest = new
        {
            schemaVersion = 1,
            id,
            name,
            version = "1.0.0",
            author = "Scanned AU",
            description = $"Audio Unit registered from the system component registry ({name}).",
            license = "Unknown",
            sdk = new { abi = 1, minVersion = "1.0.0" },
            entrypoint = new { assembly = StubAssemblyFile },
            audio = new
            {
                format = "au",
                auComponentId,
                slot,
                channels = 1,
                sampleRate = 48000,
            },
        };
        return JsonSerializer.Serialize(manifest, JsonOpts);
    }

    private static string StableUniqueId(string name, string componentId, HashSet<string> used, string idPrefix)
    {
        var slug = Slugify(name);
        if (slug.Length == 0) slug = "plugin";
        var baseId = idPrefix + slug;
        if (!used.Contains(baseId)) return baseId;

        var suffix = ShortHash(componentId.Length > 0 ? componentId : name);
        var candidate = $"{baseId}{suffix}";
        for (int n = 2; used.Contains(candidate); n++)
            candidate = $"{baseId}{suffix}{n}";
        return candidate;
    }

    private static string ShortHash(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        var sb = new StringBuilder(8);
        for (int i = 0; i < 4; i++) sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }

    private static string Slugify(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
        }
        return sb.ToString();
    }
}
