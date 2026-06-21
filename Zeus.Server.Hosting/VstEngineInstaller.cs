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
// out-of-process VST engine (upstream KlayaR/VSTHost, run headless as
// `VSTHostEngine.exe --zeus-bridge`) is deliberately NOT vendored or bundled in
// the Zeus installer: VSTHost links JUCE (GPLv3), and keeping the binary as a
// user-fetched, separate process is what isolates that license from Zeus's
// own distribution (see docs/designs/vst-out-of-process-engine.md §3/§7).
//
// Instead Zeus ships this downloader: it fetches the LATEST upstream release,
// extracts VSTHostEngine.exe from the portable zip, and stages it at the
// Zeus-managed path (%LOCALAPPDATA%\Zeus\vst-engine\VSTHostEngine.exe) that
// VstEngineProcess.FindEngineExe() resolves. A new operator on a fresh PC can
// then enable VST mode and click "Get VST Engine" instead of hunting a GitHub
// release by hand. Windows-only — the engine is a Windows binary.

using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Zeus.Plugins.Host.Audio;

namespace Zeus.Server;

/// <summary>Backs the in-app "Get VST Engine" action: downloads the upstream
/// VSTHost engine and stages it at the Zeus-managed path. Registered as a
/// singleton in <c>ZeusHost</c>; the engine binary is fetched, never bundled.</summary>
public sealed class VstEngineInstaller
{
    // Upstream release feed. The latest release's portable zip carries the
    // engine; overridable for staging/tests. The operator never sees this
    // (the product name in the UI is "VST Engine", not "VSTHost").
    internal const string DefaultReleaseApiUrl =
        "https://api.github.com/repos/KlayaR/VSTHost/releases/latest";

    public enum Phase { Idle, Downloading, Extracting, Staging, Done, Failed }

    /// <summary>Coarse install progress for the polling frontend.</summary>
    public sealed record Status(Phase Phase, int Percent, string? Message, string? Version)
    {
        public bool InProgress =>
            Phase is Phase.Downloading or Phase.Extracting or Phase.Staging;
    }

    private readonly ILogger<VstEngineInstaller> _log;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly object _lock = new();
    private Status _status = new(Phase.Idle, 0, null, null);
    private Task? _running;

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

    /// <summary>Kick off a background install if one isn't already running and the
    /// engine isn't already present. Returns the (possibly unchanged) status
    /// immediately — the frontend polls <see cref="Current"/> for progress.</summary>
    public Status Start(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_running is { IsCompleted: false })
                return _status; // already running

            if (EngineInstalled)
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

            _status = new Status(Phase.Downloading, 0, "Contacting release server…", null);
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
        string? tempZip = null;
        string? tempDir = null;
        try
        {
            var managedExe = VstEngineController.ManagedEnginePath()
                ?? throw new InvalidOperationException("No managed engine path on this platform.");
            var managedDir = Path.GetDirectoryName(managedExe)!;

            // 1) Resolve the latest release + its zip asset.
            SetStatus(Phase.Downloading, 2, "Looking up the latest VST engine release…");
            var http = _httpClientFactory.CreateClient("ZeusVstEngine");
            var release = await http.GetFromJsonAsync<GithubRelease>(ReleaseApiUrl(), ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Empty release response.");
            var version = string.IsNullOrWhiteSpace(release.TagName) ? null : release.TagName.Trim();
            SetStatus(Phase.Downloading, 4, $"Found VST engine {version ?? "(latest)"}…", version);

            var asset = SelectZipAsset(release.Assets)
                ?? throw new InvalidOperationException("No downloadable engine archive in the latest release.");

            // 2) Download the zip with coarse progress (2..70%).
            tempZip = Path.Combine(Path.GetTempPath(), $"zeus-vst-engine-{Guid.NewGuid():N}.zip");
            await DownloadAsync(http, asset.DownloadUrl!, tempZip, asset.Size, ct).ConfigureAwait(false);

            // 3) Extract (70..85%).
            SetStatus(Phase.Extracting, 72, "Extracting the VST engine…");
            tempDir = Path.Combine(Path.GetTempPath(), $"zeus-vst-engine-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            ZipFile.ExtractToDirectory(tempZip, tempDir, overwriteFiles: true);

            // 4) Stage the engine (+ its sibling DLLs) at the managed path (85..100%).
            SetStatus(Phase.Staging, 88, "Installing the VST engine…");
            var stagedExe = StageFromExtractedDir(tempDir, managedDir);

            SetStatus(Phase.Done, 100, "VST engine installed. Enable VST mode to use it.", version);
            _log.LogInformation("VST engine {Version} staged at {Path}", version ?? "(latest)", stagedExe);
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

    /// <summary>Find <c>VSTHostEngine.exe</c> anywhere in the extracted tree and
    /// copy its containing folder (the exe plus its sibling DLLs / resources)
    /// into <paramref name="managedDir"/> flat at the top level. Returns the
    /// staged exe path. Throws if the archive carries no engine binary (e.g. a
    /// VSTHost build without the Zeus bridge engine).</summary>
    internal static string StageFromExtractedDir(string extractedRoot, string managedDir)
    {
        var sourceExe = Directory
            .EnumerateFiles(extractedRoot, "VSTHostEngine.exe", SearchOption.AllDirectories)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                "The downloaded archive does not contain VSTHostEngine.exe — this VSTHost "
                + "release may not include the Zeus bridge engine yet.");

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

    /// <summary>Pick the engine archive from a release's assets: prefer a portable
    /// <c>.zip</c>, else any <c>.zip</c>. The setup.exe / .msi assets are the
    /// Tauri desktop app, which Zeus does not use.</summary>
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

    // ----- GitHub release JSON (only the fields we read) -----

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
