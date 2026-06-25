// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// VstEngineInstaller — the in-app "Get VST Engine" provisioning flow. The
// out-of-process VST engine (a headless VSTHost build, run as
// `VSTHostEngine.exe --zeus-bridge`) is deliberately NOT vendored or bundled in
// the Zeus installer: VSTHost links JUCE (GPLv3), and keeping the binary as a
// user-fetched, separate process is what isolates that license from Zeus's
// own distribution (see docs/designs/vst-out-of-process-engine.md §3/§7).
//
// Source of truth is the Zeus download domain manifest
// (https://downloads.openhpsdrzeus.com/vst-engine/latest.json), published the
// same way as the app updater's latest.json. The manifest names the
// known-good, bridge-compatible engine + its SHA-256, so Zeus stages an engine
// that is verified to match the current bridge protocol — NOT whatever a
// floating upstream "latest" release happens to be (that path shipped a build
// that crash-loops against the current handshake; see RCA). The download is
// integrity-checked against the manifest's SHA-256 before it is staged.
//
// Repair: an existing engine that no longer matches the manifest hash (stale,
// corrupt, or the old crash-looping build) is replaced automatically — callers
// pass force:true, or the activation path requests a repair when the engine
// crash-loops. The legacy GitHub-release path remains available behind the
// ZEUS_VST_ENGINE_RELEASE_URL override for development/back-compat.

using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Zeus.Plugins.Host.Audio;

namespace Zeus.Server;

/// <summary>Backs the in-app "Get VST Engine" action: downloads the known-good
/// engine named by the Zeus download-domain manifest, verifies its SHA-256, and
/// stages it at the Zeus-managed path. Registered as a singleton in
/// <c>ZeusHost</c>; the engine binary is fetched, never bundled.</summary>
public sealed class VstEngineInstaller
{
    // Production engine manifest on the Zeus download domain (published the same
    // way as the app updater's latest.json). Overridable for staging/tests.
    internal const string DefaultManifestUrl =
        "https://downloads.openhpsdrzeus.com/vst-engine/latest.json";

    // Legacy upstream release feed. Only used when the ZEUS_VST_ENGINE_RELEASE_URL
    // override is set (development / back-compat). The default production path is
    // the manifest above, because the floating upstream "latest" has shipped an
    // engine build that crash-loops against the current bridge handshake.
    internal const string DefaultReleaseApiUrl =
        "https://api.github.com/repos/KlayaR/VSTHost/releases/latest";

    public enum Phase { Idle, Downloading, Verifying, Extracting, Staging, Done, Failed }

    /// <summary>Coarse install progress for the polling frontend.</summary>
    public sealed record Status(Phase Phase, int Percent, string? Message, string? Version)
    {
        public bool InProgress =>
            Phase is Phase.Downloading or Phase.Verifying or Phase.Extracting or Phase.Staging;
    }

    private readonly ILogger<VstEngineInstaller> _log;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly object _lock = new();
    private Status _status = new(Phase.Idle, 0, null, null);
    private Task? _running;

    // The SHA-256 (lowercase hex) the manifest last advertised for the staged
    // engine, captured on a successful install. Lets the activation path detect a
    // stale/mismatched engine and request a repair. Null until first install or
    // when only the legacy GitHub path ran (which carries no hash).
    private string? _installedSha256;

    public VstEngineInstaller(ILogger<VstEngineInstaller> log, IHttpClientFactory httpClientFactory)
    {
        _log = log;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>True when an engine is already resolvable (managed path, default
    /// install, or PATH) — the install affordance is unnecessary.</summary>
    public bool EngineInstalled => VstEngineController.FindEngineExe() is not null;

    /// <summary>Snapshot of the current install state for the status endpoint.</summary>
    public Status Current
    {
        get { lock (_lock) return _status; }
    }

    /// <summary>The SHA-256 the manifest advertised for the engine we last staged,
    /// or null if unknown (no manifest install yet, or the legacy path ran).</summary>
    public string? InstalledSha256
    {
        get { lock (_lock) return _installedSha256; }
    }

    /// <summary>Kick off a background install if one isn't already running. By
    /// default a no-op when an engine is already present; pass
    /// <paramref name="force"/> to re-provision regardless — used to repair a
    /// stale/corrupt/crash-looping engine by replacing it with the manifest's
    /// verified binary. Returns the (possibly unchanged) status immediately — the
    /// frontend polls <see cref="Current"/> for progress.</summary>
    public Status Start(bool force = false, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_running is { IsCompleted: false })
                return _status; // already running

            if (!force && EngineInstalled)
            {
                _status = new Status(Phase.Done, 100, "VST engine already installed.", null);
                return _status;
            }

            if (!OperatingSystem.IsWindows())
            {
                _status = new Status(Phase.Failed, 0,
                    "The VST engine is Windows-only; nothing to install on this platform.", null);
                return _status;
            }

            _status = new Status(Phase.Downloading, 0,
                force ? "Repairing the VST engine…" : "Contacting download server…", null);
            _running = Task.Run(() => RunAsync(ct), CancellationToken.None);
            return _status;
        }
    }

    private void SetStatus(Phase phase, int percent, string? message, string? version = null)
    {
        lock (_lock)
        {
            // Carry the resolved version forward once we have it.
            version ??= _status.Version;
            _status = new Status(phase, Math.Clamp(percent, 0, 100), message, version);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        string? tempFile = null;
        string? tempDir = null;
        try
        {
            var managedExe = VstEngineController.ManagedEnginePath()
                ?? throw new InvalidOperationException("No managed engine path on this platform.");
            var managedDir = Path.GetDirectoryName(managedExe)!;
            var http = _httpClientFactory.CreateClient("ZeusVstEngine");

            // Development / back-compat: an explicit upstream-release override goes
            // through the legacy GitHub path (no manifest, no advertised hash).
            if (HasReleaseOverride())
            {
                await RunLegacyReleaseAsync(http, managedDir, ct).ConfigureAwait(false);
                lock (_lock) _installedSha256 = null;
                return;
            }

            // 1) Resolve the known-good engine asset from the Zeus manifest.
            SetStatus(Phase.Downloading, 2, "Looking up the latest VST engine…");
            var manifest = await http.GetFromJsonAsync<EngineManifest>(ManifestUrl(), ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Empty engine manifest response.");
            var asset = SelectEngineAsset(manifest.Assets)
                ?? throw new InvalidOperationException(
                    "The engine manifest lists no Windows x64 engine asset.");
            if (string.IsNullOrWhiteSpace(asset.Url))
                throw new InvalidOperationException("The engine manifest asset has no download URL.");
            if (string.IsNullOrWhiteSpace(asset.Sha256))
                throw new InvalidOperationException(
                    "The engine manifest asset has no SHA-256 — refusing to stage an unverifiable engine.");

            var version = string.IsNullOrWhiteSpace(manifest.Version) ? null : manifest.Version.Trim();
            SetStatus(Phase.Downloading, 4, $"Found VST engine {version ?? "(latest)"}…", version);

            // 2) Download to a temp file with coarse progress (4..70%).
            var isZip = asset.Filename.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                        || (asset.Url ?? "").EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            tempFile = Path.Combine(Path.GetTempPath(), $"zeus-vst-engine-{Guid.NewGuid():N}{(isZip ? ".zip" : ".bin")}");
            await DownloadAsync(http, asset.Url!, tempFile, asset.Size, ct).ConfigureAwait(false);

            // 3) Integrity gate — the engine runs against real PA hardware paths;
            // never stage a binary whose bytes don't match the signed manifest.
            SetStatus(Phase.Verifying, 74, "Verifying the VST engine…", version);
            var actual = await Sha256HexAsync(tempFile, ct).ConfigureAwait(false);
            var expected = asset.Sha256!.Trim().ToLowerInvariant();
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"VST engine SHA-256 mismatch (expected {expected[..Math.Min(12, expected.Length)]}…, "
                    + $"got {actual[..Math.Min(12, actual.Length)]}…) — refusing to stage a corrupt download.");

            // 4) Stage at the managed path (75..100%): extract a zip, or place the
            // verified exe directly.
            string stagedExe;
            if (isZip)
            {
                SetStatus(Phase.Extracting, 80, "Extracting the VST engine…", version);
                tempDir = Path.Combine(Path.GetTempPath(), $"zeus-vst-engine-{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempDir);
                ZipFile.ExtractToDirectory(tempFile, tempDir, overwriteFiles: true);
                SetStatus(Phase.Staging, 90, "Installing the VST engine…", version);
                stagedExe = StageFromExtractedDir(tempDir, managedDir);
            }
            else
            {
                SetStatus(Phase.Staging, 90, "Installing the VST engine…", version);
                stagedExe = StageSingleExe(tempFile, managedDir);
            }

            lock (_lock) _installedSha256 = expected;
            SetStatus(Phase.Done, 100, "VST engine installed. Enable VST mode to use it.", version);
            _log.LogInformation("VST engine {Version} staged at {Path} (sha256 {Sha})",
                version ?? "(latest)", stagedExe, expected[..Math.Min(12, expected.Length)]);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            SetStatus(Phase.Failed, 0, "VST engine install was cancelled.");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "VST engine install failed");
            SetStatus(Phase.Failed, 0, $"VST engine install failed: {Trunc(ex.Message)}");
        }
        finally
        {
            TryDelete(tempFile);
            TryDeleteDir(tempDir);
        }
    }

    /// <summary>Legacy path: fetch the upstream GitHub release's portable zip and
    /// stage its <c>VSTHostEngine.exe</c>. Used only when ZEUS_VST_ENGINE_RELEASE_URL
    /// is set; carries no advertised hash, so no integrity gate.</summary>
    private async Task RunLegacyReleaseAsync(HttpClient http, string managedDir, CancellationToken ct)
    {
        string? tempZip = null;
        string? tempDir = null;
        try
        {
            SetStatus(Phase.Downloading, 2, "Looking up the VST engine release…");
            var release = await http.GetFromJsonAsync<GithubRelease>(ReleaseApiUrl(), ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Empty release response.");
            var version = string.IsNullOrWhiteSpace(release.TagName) ? null : release.TagName.Trim();
            SetStatus(Phase.Downloading, 4, $"Found VST engine {version ?? "(latest)"}…", version);

            var asset = SelectZipAsset(release.Assets)
                ?? throw new InvalidOperationException("No downloadable engine archive in the release.");
            tempZip = Path.Combine(Path.GetTempPath(), $"zeus-vst-engine-{Guid.NewGuid():N}.zip");
            await DownloadAsync(http, asset.DownloadUrl!, tempZip, asset.Size, ct).ConfigureAwait(false);

            SetStatus(Phase.Extracting, 72, "Extracting the VST engine…");
            tempDir = Path.Combine(Path.GetTempPath(), $"zeus-vst-engine-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            ZipFile.ExtractToDirectory(tempZip, tempDir, overwriteFiles: true);

            SetStatus(Phase.Staging, 88, "Installing the VST engine…");
            var stagedExe = StageFromExtractedDir(tempDir, managedDir);

            SetStatus(Phase.Done, 100, "VST engine installed. Enable VST mode to use it.", version);
            _log.LogInformation("VST engine {Version} staged at {Path} (legacy release path)",
                version ?? "(latest)", stagedExe);
        }
        finally
        {
            TryDelete(tempZip);
            TryDeleteDir(tempDir);
        }
    }

    private async Task DownloadAsync(HttpClient http, string url, string destPath, long knownSize, CancellationToken ct)
    {
        using var response = await http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? (knownSize > 0 ? knownSize : 0);
        await using var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            read += n;
            if (total > 0)
            {
                // Map download to the 4..70% band.
                var pct = 4 + (int)(read * 66 / total);
                SetStatus(Phase.Downloading, pct, "Downloading the VST engine…");
            }
        }
    }

    private static async Task<string> Sha256HexAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Stage a single verified engine exe into a clean managed dir.</summary>
    internal static string StageSingleExe(string sourceExe, string managedDir)
    {
        if (Directory.Exists(managedDir)) Directory.Delete(managedDir, recursive: true);
        Directory.CreateDirectory(managedDir);
        var stagedExe = Path.Combine(managedDir, "VSTHostEngine.exe");
        File.Copy(sourceExe, stagedExe, overwrite: true);
        if (!File.Exists(stagedExe))
            throw new InvalidOperationException("Staging completed but VSTHostEngine.exe is missing.");
        return stagedExe;
    }

    /// <summary>Find <c>VSTHostEngine.exe</c> anywhere in the extracted tree and
    /// copy its containing folder (the exe plus its sibling DLLs / resources)
    /// into <paramref name="managedDir"/> flat at the top level. Returns the
    /// staged exe path. Throws if the archive carries no engine binary.</summary>
    internal static string StageFromExtractedDir(string extractedRoot, string managedDir)
    {
        var sourceExe = Directory
            .EnumerateFiles(extractedRoot, "VSTHostEngine.exe", SearchOption.AllDirectories)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                "The downloaded archive does not contain VSTHostEngine.exe — this "
                + "release may not include the Zeus bridge engine.");

        var sourceDir = Path.GetDirectoryName(sourceExe)!;

        // Stage into a clean managed dir so a re-install never leaves stale files.
        if (Directory.Exists(managedDir)) Directory.Delete(managedDir, recursive: true);
        Directory.CreateDirectory(managedDir);

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, file);
            var dest = Path.Combine(managedDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }

        var stagedExe = Path.Combine(managedDir, "VSTHostEngine.exe");
        if (!File.Exists(stagedExe))
            throw new InvalidOperationException("Staging completed but VSTHostEngine.exe is missing.");
        return stagedExe;
    }

    /// <summary>Pick the Windows x64 engine asset from the manifest. Prefers an
    /// explicit windows/x64 platform tag; falls back to the first asset whose
    /// filename looks like the engine exe or a zip.</summary>
    internal static EngineAsset? SelectEngineAsset(IReadOnlyList<EngineAsset>? assets)
    {
        if (assets is null || assets.Count == 0) return null;

        static bool IsWindowsX64(EngineAsset a) =>
            (a.Platform is null || a.Platform.Equals("windows", StringComparison.OrdinalIgnoreCase)
                                || a.Platform.Equals("win", StringComparison.OrdinalIgnoreCase))
            && (a.Arch is null || a.Arch.Equals("x64", StringComparison.OrdinalIgnoreCase)
                               || a.Arch.Equals("amd64", StringComparison.OrdinalIgnoreCase));

        bool LooksLikeEngine(EngineAsset a) =>
            !string.IsNullOrWhiteSpace(a.Url)
            && (a.Filename.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || a.Filename.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

        return assets.FirstOrDefault(a => IsWindowsX64(a) && LooksLikeEngine(a))
               ?? assets.FirstOrDefault(LooksLikeEngine);
    }

    /// <summary>Pick the engine archive from a GitHub release's assets (legacy
    /// path): prefer a portable <c>.zip</c>, else any <c>.zip</c>.</summary>
    internal static GithubAsset? SelectZipAsset(IReadOnlyList<GithubAsset>? assets)
    {
        if (assets is null || assets.Count == 0) return null;
        bool IsZip(GithubAsset a) =>
            !string.IsNullOrWhiteSpace(a.DownloadUrl)
            && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

        return assets.FirstOrDefault(a => IsZip(a)
                   && a.Name.Contains("portable", StringComparison.OrdinalIgnoreCase))
               ?? assets.FirstOrDefault(IsZip);
    }

    private static bool HasReleaseOverride() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ZEUS_VST_ENGINE_RELEASE_URL"));

    private static string ManifestUrl()
    {
        var env = Environment.GetEnvironmentVariable("ZEUS_VST_ENGINE_MANIFEST_URL");
        return string.IsNullOrWhiteSpace(env) ? DefaultManifestUrl : env.Trim();
    }

    private static string ReleaseApiUrl()
    {
        var env = Environment.GetEnvironmentVariable("ZEUS_VST_ENGINE_RELEASE_URL");
        return string.IsNullOrWhiteSpace(env) ? DefaultReleaseApiUrl : env.Trim();
    }

    private static string Trunc(string s, int max = 200)
        => s.Length <= max ? s : s[..max] + "…";

    private static void TryDelete(string? path)
    {
        if (path is null) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private static void TryDeleteDir(string? path)
    {
        if (path is null) return;
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* best effort */ }
    }

    // ----- Zeus engine manifest JSON (only the fields we read) -----

    internal sealed class EngineManifest
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("publishedAt")]
        public string? PublishedAt { get; set; }

        [JsonPropertyName("assets")]
        public List<EngineAsset> Assets { get; set; } = [];
    }

    internal sealed class EngineAsset
    {
        [JsonPropertyName("filename")]
        public string Filename { get; set; } = string.Empty;

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("sha256")]
        public string? Sha256 { get; set; }

        [JsonPropertyName("platform")]
        public string? Platform { get; set; }

        [JsonPropertyName("arch")]
        public string? Arch { get; set; }
    }

    // ----- GitHub release JSON (legacy path; only the fields we read) -----

    internal sealed class GithubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("assets")]
        public List<GithubAsset> Assets { get; set; } = [];
    }

    internal sealed class GithubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string? DownloadUrl { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }
}
