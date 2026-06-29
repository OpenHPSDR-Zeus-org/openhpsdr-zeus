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
// RepoUpdateService — reports whether the running install is behind the latest
// PRODUCTION build published to the Zeus download domain
// (downloads.openhpsdrzeus.com). The domain manifest is the single source of
// truth: it is produced only from `main` by .github/workflows/publish-downloads.yml,
// so the in-app updater never points operators at develop / nightly / dev code.
//
// This drives Settings -> Updates (/api/system/update). The service NEVER pulls,
// rebuilds, or restarts: it tells the operator which production build is newest
// and surfaces the platform-matched installer / DMG / AppImage / tarball download
// URL (with its published SHA-256). Replacing the running binary is left to the
// installer / DMG / AppImage flow. Developers running from a git checkout still
// update their source manually with scripts/update.* — that path is intentionally
// not exposed in the UI.

using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>Download-domain update helper backing the Settings -> Updates panel.
/// Registered as a singleton in <c>ZeusHost</c>.</summary>
public sealed partial class RepoUpdateService
{
    // The production download manifest. Published from `main` only (see
    // publish-downloads.yml + tools/update-download-manifest.mjs). `latest.json`
    // is the newest version entry; overridable for tests / staging via env.
    internal const string DefaultManifestUrl = "https://downloads.openhpsdrzeus.com/latest.json";

    // Operator-facing landing page used when no platform asset is present in the
    // manifest (e.g. an arch we don't yet build) — "Update now" then opens this.
    internal const string DownloadsPageUrl = "https://openhpsdrzeus.com/download";

    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILogger<RepoUpdateService> _log;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string? _repoRoot;

    public RepoUpdateService(ILogger<RepoUpdateService> log, IHttpClientFactory httpClientFactory)
    {
        _log = log;
        _httpClientFactory = httpClientFactory;
        _repoRoot = ResolveRepoRoot();
        if (_repoRoot is null)
            _log.LogInformation("RepoUpdateService: packaged install; checking {Url}", ManifestUrl());
        else
            _log.LogInformation("RepoUpdateService: source checkout at {Root}; checking {Url}", _repoRoot, ManifestUrl());
    }

    /// <summary>Report the install's status versus the latest production build on
    /// the download domain. When <paramref name="fetch"/> is true the manifest is
    /// fetched (network); a failed fetch is non-fatal and surfaces as an
    /// <see cref="RepoUpdateStatus.Error"/> note with the last locally-known
    /// version still populated.</summary>
    public async Task<RepoUpdateStatus> GetStatusAsync(bool fetch, CancellationToken ct)
    {
        var local = await BuildLocalStatusAsync(ct).ConfigureAwait(false);
        if (!fetch)
            return local;
        return await AddDownloadStatusAsync(local, ct).ConfigureAwait(false);
    }

    /// <summary>Assemble the local half of the status: installed version, runtime
    /// platform/arch, and (for source checkouts) the branch/sha/dirty diagnostics.
    /// No network and no git fetch — the update decision comes from the domain
    /// manifest in <see cref="AddDownloadStatusAsync"/>.</summary>
    private async Task<RepoUpdateStatus> BuildLocalStatusAsync(CancellationToken ct)
    {
        var isGitRepo = _repoRoot is not null;
        string? branch = null;
        string? shortSha = null;
        string? subject = null;
        var dirty = false;

        if (isGitRepo)
        {
            // Best-effort diagnostics only. git not being installed must never
            // break the (network-driven) update check, so failures are swallowed.
            try
            {
                branch = NullIfEmpty((await GitAsync(ct, 10_000, "rev-parse", "--abbrev-ref", "HEAD")).Out);
                shortSha = NullIfEmpty((await GitAsync(ct, 10_000, "rev-parse", "--short", "HEAD")).Out);
                subject = NullIfEmpty((await GitAsync(ct, 10_000, "log", "-1", "--format=%s")).Out);
                dirty = !string.IsNullOrWhiteSpace((await GitAsync(ct, 10_000, "status", "--porcelain")).Out);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "git diagnostics unavailable");
            }
        }

        return new RepoUpdateStatus(
            IsGitRepo: isGitRepo,
            Branch: branch,
            CurrentSha: null,
            CurrentShortSha: shortSha,
            CurrentSubject: subject,
            UpstreamRef: null,
            Behind: 0,
            Ahead: 0,
            Dirty: dirty,
            CanFastForward: false,
            LatestRemoteSha: null,
            LatestRemoteSubject: null,
            RemoteUrl: null,
            CheckedUtc: null,
            Error: null)
        {
            InstalledVersion = InstalledVersion(),
            RuntimePlatform = RuntimePlatform(),
            RuntimeArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            UpdateAvailable = false,
            UpdateAction = "none",
        };
    }

    /// <summary>Fetch the production download manifest and layer the
    /// update decision + platform asset onto <paramref name="status"/>.</summary>
    private async Task<RepoUpdateStatus> AddDownloadStatusAsync(
        RepoUpdateStatus status,
        CancellationToken ct)
    {
        var checkedUtc = DateTime.UtcNow.ToString("o");
        try
        {
            using var response = await _httpClientFactory.CreateClient("ZeusUpdates")
                .GetAsync(ManifestUrl(), ct)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return status with
                {
                    CheckedUtc = checkedUtc,
                    Error = MergeNotes(status.Error, "No published Zeus download was found."),
                };
            }

            response.EnsureSuccessStatusCode();
            var manifest = await response.Content
                .ReadFromJsonAsync<ZeusDownloadManifest>(ManifestJsonOptions, ct)
                .ConfigureAwait(false);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version))
            {
                return status with
                {
                    CheckedUtc = checkedUtc,
                    Error = MergeNotes(status.Error, "The download manifest was empty."),
                };
            }

            var latestVersion = manifest.Version.Trim();
            var asset = SelectDownloadAsset(
                manifest.Assets,
                RuntimePlatform(),
                RuntimeInformation.OSArchitecture,
                IsRunningFromAppImage(),
                IsServerMode());
            var updateAvailable = IsManifestNewer(status.InstalledVersion, latestVersion);
            var updateAction = updateAvailable
                ? asset?.Url is { Length: > 0 }
                    ? "download"
                    : "openRelease"
                : "none";

            return status with
            {
                CheckedUtc = checkedUtc,
                UpdateAvailable = updateAvailable,
                UpdateAction = updateAction,
                LatestVersion = latestVersion,
                ReleaseTag = NullIfEmpty(manifest.Source?.Commit ?? string.Empty),
                ReleaseName = $"Zeus {latestVersion}",
                ReleaseUrl = DownloadsPageUrl,
                ReleasePublishedUtc = manifest.PublishedAt,
                ReleaseAssetName = asset?.Filename,
                ReleaseDownloadUrl = asset?.Url,
                ReleaseAssetSizeBytes = asset?.Size,
                ReleaseAssetDigest = FormatDigest(asset?.Sha256),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "download manifest check failed");
            return status with
            {
                CheckedUtc = checkedUtc,
                Error = MergeNotes(status.Error, $"update check failed (offline?): {Trunc(ex.Message)}"),
            };
        }
    }

    // ----- git process helper (diagnostics only) -----

    private async Task<(bool Ok, string Out, string Err)> GitAsync(
        CancellationToken ct, int timeoutMs, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _repoRoot!,
        };
        // Scope every invocation to the repo root so the CWD (the exe's bin dir)
        // doesn't matter, and quoting of a path with spaces is handled by
        // ArgumentList rather than manual escaping.
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(_repoRoot!);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        using var p = new Process { StartInfo = psi };
        p.Start();
        // Read both pipes concurrently (avoids the classic full-buffer deadlock)
        // and bind them to the timeout token so a process that exits but never
        // closes its pipes can't hang the read past the timeout.
        var outTask = p.StandardOutput.ReadToEndAsync(cts.Token);
        var errTask = p.StandardError.ReadToEndAsync(cts.Token);

        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
            // Observe the abandoned reads so they don't surface as unobserved
            // task exceptions once the killed process's pipes close.
            try { await Task.WhenAll(outTask, errTask).ConfigureAwait(false); } catch { /* ignore */ }
            return (false, string.Empty, "git timed out");
        }

        var stdout = (await outTask.ConfigureAwait(false)).Trim();
        var stderr = (await errTask.ConfigureAwait(false)).Trim();
        return (p.ExitCode == 0, stdout, stderr);
    }

    // ----- helpers -----

    private static string ManifestUrl()
    {
        var env = Environment.GetEnvironmentVariable("ZEUS_UPDATE_MANIFEST_URL");
        return string.IsNullOrWhiteSpace(env) ? DefaultManifestUrl : env.Trim();
    }

    private static string? NullIfEmpty(string s)
        => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string Trunc(string s, int max = 300)
        => s.Length <= max ? s : s[..max] + "…";

    private static string MergeNotes(string? first, string second)
        => string.IsNullOrWhiteSpace(first) ? second : $"{first}; {second}";

    private static string? FormatDigest(string? sha256)
    {
        if (string.IsNullOrWhiteSpace(sha256)) return null;
        var hex = sha256.Trim();
        return hex.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? hex : $"sha256:{hex}";
    }

    private static string InstalledVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var attr = assembly.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
            .FirstOrDefault() as AssemblyInformationalVersionAttribute;
        if (string.IsNullOrWhiteSpace(attr?.InformationalVersion))
            return "unknown";
        // Strip SourceLink's "+<commit-sha>" build-metadata so the version we
        // compare against the manifest is the clean SemVer-ish string.
        var v = attr.InformationalVersion;
        var plus = v.IndexOf('+');
        return plus >= 0 ? v[..plus] : v;
    }

    /// <summary>True when <paramref name="latest"/> is a strictly newer numeric
    /// MAJOR.MINOR.PATCH than <paramref name="installed"/>. Suffix/build metadata
    /// is ignored — use <see cref="IsManifestNewer"/> for the build-level decision
    /// that also accounts for same-prefix rolling builds.</summary>
    internal static bool IsReleaseNewer(string? latest, string? installed)
    {
        return TryParseReleaseVersion(latest, out var latestVersion)
            && TryParseReleaseVersion(installed, out var installedVersion)
            && latestVersion.CompareTo(installedVersion) > 0;
    }

    /// <summary>Decide whether the manifest's latest build is newer than what's
    /// installed. Production `main` builds share a single numeric VersionPrefix
    /// and differ only by the <c>main.YYYYMMDD.&lt;sha&gt;</c> suffix, so a plain
    /// numeric compare can't see a new build — when the numeric prefixes match we
    /// fall back to a string difference (the domain only ever rolls forward).
    /// A strictly-greater installed prefix (a local build ahead of the published
    /// line) is never treated as an available update.</summary>
    internal static bool IsManifestNewer(string? installed, string? latest)
    {
        if (string.IsNullOrWhiteSpace(latest)) return false;
        var latestTrim = latest.Trim();
        var installedTrim = installed?.Trim() ?? string.Empty;
        if (string.Equals(installedTrim, latestTrim, StringComparison.OrdinalIgnoreCase))
            return false;

        var gotInstalled = TryParseReleaseVersion(installedTrim, out var vi);
        var gotLatest = TryParseReleaseVersion(latestTrim, out var vl);
        if (gotInstalled && gotLatest)
        {
            var cmp = vl.CompareTo(vi);
            if (cmp != 0) return cmp > 0;
            // Same numeric prefix, different build string → newer rolling build.
            return true;
        }

        // One side couldn't be parsed (e.g. installed "unknown"): any difference
        // means the manifest offers something concrete to move to.
        return !string.Equals(installedTrim, latestTrim, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseReleaseVersion(string? value, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(value)) return false;
        var m = VersionPattern().Match(value);
        if (!m.Success) return false;
        version = new Version(
            int.Parse(m.Groups["major"].Value),
            int.Parse(m.Groups["minor"].Value),
            int.Parse(m.Groups["patch"].Value));
        return true;
    }

    [GeneratedRegex(@"[vV]?(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)")]
    private static partial Regex VersionPattern();

    /// <summary>Pick the manifest asset matching this platform + architecture.
    /// Linux prefers the matching-mode AppImage when the running process is itself
    /// an AppImage, otherwise the tarball.</summary>
    internal static ZeusDownloadAsset? SelectDownloadAsset(
        IReadOnlyList<ZeusDownloadAsset>? assets,
        string platform,
        Architecture architecture,
        bool runningFromAppImage,
        bool serverMode)
    {
        if (assets is null || assets.Count == 0) return null;

        var arch = architecture == Architecture.Arm64 ? "arm64" : "x64";
        bool Matches(ZeusDownloadAsset a, string kind)
            => string.Equals(a.Platform, platform, StringComparison.OrdinalIgnoreCase)
               && string.Equals(a.Arch, arch, StringComparison.OrdinalIgnoreCase)
               && string.Equals(a.Kind, kind, StringComparison.OrdinalIgnoreCase);

        if (string.Equals(platform, "windows", StringComparison.OrdinalIgnoreCase))
            return assets.FirstOrDefault(a => Matches(a, "installer"));

        if (string.Equals(platform, "macos", StringComparison.OrdinalIgnoreCase))
            return assets.FirstOrDefault(a => Matches(a, "dmg"));

        if (string.Equals(platform, "linux", StringComparison.OrdinalIgnoreCase))
        {
            if (runningFromAppImage)
            {
                var mode = serverMode ? "server" : "desktop";
                var appImage = assets.FirstOrDefault(a =>
                    Matches(a, "appimage")
                    && string.Equals(a.Mode, mode, StringComparison.OrdinalIgnoreCase));
                if (appImage is not null) return appImage;
            }

            return assets.FirstOrDefault(a => Matches(a, "tarball"));
        }

        return null;
    }

    private static string RuntimePlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "macos";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "linux";
        return "unknown";
    }

    private static bool IsRunningFromAppImage()
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APPIMAGE"));

    private static bool IsServerMode()
        => Environment.GetCommandLineArgs()
            .Any(a => string.Equals(a, "--server", StringComparison.OrdinalIgnoreCase));

    /// <summary>Walk up from the running binary's directory until a `.git` entry
    /// is found (directory for a normal clone, file for a worktree/submodule).</summary>
    private static string? ResolveRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var gitPath = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}

/// <summary>One version entry from the download domain (`latest.json` / the head
/// of `manifest.json`). Produced by tools/update-download-manifest.mjs.</summary>
internal sealed class ZeusDownloadManifest
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("channel")]
    public string? Channel { get; set; }

    [JsonPropertyName("publishedAt")]
    public string? PublishedAt { get; set; }

    [JsonPropertyName("source")]
    public ZeusDownloadSource? Source { get; set; }

    [JsonPropertyName("assets")]
    public List<ZeusDownloadAsset> Assets { get; set; } = [];
}

internal sealed class ZeusDownloadSource
{
    [JsonPropertyName("branch")]
    public string? Branch { get; set; }

    [JsonPropertyName("commit")]
    public string? Commit { get; set; }

    [JsonPropertyName("runUrl")]
    public string? RunUrl { get; set; }
}

internal sealed class ZeusDownloadAsset
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

    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }
}
