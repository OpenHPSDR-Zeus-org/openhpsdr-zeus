using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Registry;

namespace Zeus.Plugins.Host.Registry;

/// <summary>
/// Implements bring-your-own-plugin: downloads a zip, verifies SHA256
/// if supplied, validates the embedded plugin.json, extracts into the
/// plugin root, and asks <see cref="PluginManager"/> to activate.
/// </summary>
public sealed class PluginInstaller
{
    private readonly HttpClient _http;
    private readonly IRegistryClient _registry;
    private readonly PluginManager _manager;
    private readonly string _pluginRoot;
    private readonly ILogger<PluginInstaller>? _log;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public PluginInstaller(
        HttpClient http,
        IRegistryClient registry,
        PluginManager manager,
        string pluginRoot,
        ILogger<PluginInstaller>? log = null)
    {
        _http = http;
        _registry = registry;
        _manager = manager;
        _pluginRoot = pluginRoot;
        _log = log;
    }

    /// <summary>Install from a registry id+version. The catalog is consulted
    /// for the downloadUrl + expected sha256.</summary>
    public async Task<InstalledPlugin> InstallFromRegistryAsync(
        string id, string version, CancellationToken ct)
    {
        var catalog = await _registry.FetchAsync(ct).ConfigureAwait(false);
        var entry = catalog.Plugins.FirstOrDefault(p => p.Id == id)
            ?? throw new PluginInstallException($"plugin '{id}' not in registry");
        var ver = entry.Versions.FirstOrDefault(v => v.Version == version)
            ?? throw new PluginInstallException($"version '{version}' of '{id}' not in registry");

        return await InstallFromUrlAsync(ver.DownloadUrl, ver.Sha256, ct).ConfigureAwait(false);
    }

    /// <summary>Install from an arbitrary HTTPS URL. <paramref name="expectedSha256"/>
    /// is verified if supplied; pass null to skip (not recommended).</summary>
    public async Task<InstalledPlugin> InstallFromUrlAsync(
        string url, string? expectedSha256, CancellationToken ct)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new PluginInstallException($"refusing non-HTTPS download URL: {url}");

        var tempZip = Path.GetTempFileName();
        try
        {
            await DownloadAsync(url, tempZip, ct).ConfigureAwait(false);
            if (expectedSha256 is { Length: > 0 })
                VerifySha256(tempZip, expectedSha256);
            return await InstallFromZipFileAsync(tempZip, ct).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(tempZip); } catch { /* ignore */ }
        }
    }

    /// <summary>Install from a local zip on disk (BYOP "Install from file…").</summary>
    public async Task<InstalledPlugin> InstallFromZipFileAsync(string zipPath, CancellationToken ct)
    {
        if (!File.Exists(zipPath))
            throw new PluginInstallException($"zip file not found: {zipPath}");

        // Pre-flight: extract the manifest before we touch the plugin root.
        PluginManifest manifest;
        using (var probe = ZipFile.OpenRead(zipPath))
        {
            var entry = probe.GetEntry("plugin.json")
                ?? throw new PluginInstallException("zip is missing plugin.json at the top level");
            using var s = entry.Open();
            manifest = await JsonSerializer.DeserializeAsync<PluginManifest>(s, JsonOpts, ct)
                .ConfigureAwait(false)
                ?? throw new PluginInstallException("plugin.json deserialised to null");
        }

        var errors = ManifestValidator.Validate(manifest);
        if (errors.Count > 0)
            throw new PluginInstallException(
                $"manifest invalid: {string.Join("; ", errors)}");

        if (!ManifestValidator.IsAbiCompatible(manifest, AbiVersion.Current, AbiVersion.SdkVersion))
            throw new PluginInstallException(
                $"plugin '{manifest.Id}' requires SDK abi={manifest.Sdk.Abi} minVersion={manifest.Sdk.MinVersion}; "
                + $"host is abi={AbiVersion.Current} version={AbiVersion.SdkVersion}");

        var destDir = Path.Combine(_pluginRoot, SafeDirName(manifest.Id));

        // Deactivate any existing copy of this plugin before overwriting.
        var existing = _manager.Find(manifest.Id);
        if (existing is not null)
        {
            await _manager.DeactivateAsync(manifest.Id, ct).ConfigureAwait(false);
            // Give ALC a beat to release the file lock; on Windows this
            // matters more than on Unix but harmless either way.
            await Task.Delay(50, ct).ConfigureAwait(false);
        }

        if (Directory.Exists(destDir))
        {
            try { Directory.Delete(destDir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new PluginInstallException(
                    $"cannot overwrite '{destDir}' — files may still be in use. Restart Zeus and retry.", ex);
            }
        }

        Directory.CreateDirectory(destDir);
        ExtractZipSafely(zipPath, destDir);

        var activated = await _manager.ActivateAsync(destDir, ct).ConfigureAwait(false);
        _log?.LogInformation("Installed plugin {Id} v{Version} -> {Dir}",
            manifest.Id, manifest.Version, destDir);

        return new InstalledPlugin(manifest, destDir, activated);
    }

    /// <summary>Uninstall a plugin by id: deactivate then remove its dir.</summary>
    public async Task UninstallAsync(string id, CancellationToken ct)
    {
        await _manager.DeactivateAsync(id, ct).ConfigureAwait(false);
        await Task.Delay(50, ct).ConfigureAwait(false);

        var dir = Path.Combine(_pluginRoot, SafeDirName(id));
        if (Directory.Exists(dir))
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Windows holds an open file handle on plugin DLLs after
                // ALC.Unload until GC reclaims the load context — both
                // IOException ("file in use") and UnauthorizedAccessException
                // ("access denied") surface depending on the open mode.
                // Either way the deactivation succeeded; the dir cleanup
                // needs an explicit GC + retry, or a Zeus restart.
                _log?.LogWarning(ex,
                    "Could not delete plugin dir {Dir} immediately; restart Zeus to finish removal.", dir);
                throw new PluginInstallException(
                    $"plugin '{id}' deactivated but its files could not be removed yet. Restart Zeus to complete.", ex);
            }
        }
    }

    private async Task DownloadAsync(string url, string dest, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = File.Create(dest);
        await src.CopyToAsync(dst, ct).ConfigureAwait(false);
    }

    private static void VerifySha256(string path, string expected)
    {
        using var stream = File.OpenRead(path);
        var bytes = SHA256.HashData(stream);
        var actual = Convert.ToHexString(bytes);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new PluginInstallException(
                $"sha256 mismatch — expected {expected.ToLowerInvariant()}, got {actual.ToLowerInvariant()}");
    }

    /// <summary>Extract while rejecting zip-slip and arbitrary writes outside the dest dir.</summary>
    private static void ExtractZipSafely(string zipPath, string destDir)
    {
        var fullDest = Path.GetFullPath(destDir) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith("/"))
            {
                // Pure directory
                var dir = Path.GetFullPath(Path.Combine(destDir, entry.FullName));
                if (!dir.StartsWith(fullDest, StringComparison.Ordinal))
                    throw new PluginInstallException($"zip entry escapes plugin dir: {entry.FullName}");
                Directory.CreateDirectory(dir);
                continue;
            }

            var fileDest = Path.GetFullPath(Path.Combine(destDir, entry.FullName));
            if (!fileDest.StartsWith(fullDest, StringComparison.Ordinal))
                throw new PluginInstallException($"zip entry escapes plugin dir: {entry.FullName}");

            var parent = Path.GetDirectoryName(fileDest);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            entry.ExtractToFile(fileDest, overwrite: true);
        }
    }

    /// <summary>Convert plugin id to a safe directory name. Reverse-DNS
    /// dots and hyphens stay; nothing else is permitted by the
    /// manifest's id pattern.</summary>
    internal static string SafeDirName(string pluginId)
    {
        Span<char> buf = stackalloc char[pluginId.Length];
        for (int i = 0; i < pluginId.Length; i++)
        {
            var c = pluginId[i];
            buf[i] = char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_' ? c : '_';
        }
        return new string(buf);
    }
}

public sealed record InstalledPlugin(
    PluginManifest Manifest,
    string Directory,
    ActivatedPlugin Activated);

public sealed class PluginInstallException : Exception
{
    public PluginInstallException(string message) : base(message) { }
    public PluginInstallException(string message, Exception inner) : base(message, inner) { }
}
