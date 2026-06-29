// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Zeus.Server.Uninstall;

// Orchestrates the "Reset & Uninstall Zeus" flow: build a safe preview, stream a
// one-click backup, and (on a valid confirm token) launch the detached wipe and
// exit. Owns a single one-shot confirmation nonce so the destructive POST can't
// be replayed or fired without the operator having seen the preview.
public sealed class UninstallService
{
    private readonly ILogger<UninstallService> _log;
    private readonly LogService _logService;
    private readonly object _gate = new();
    private string? _nonce;

    public UninstallService(ILogger<UninstallService> log, LogService logService)
    {
        _log = log;
        _logService = logService;
    }

    public sealed record PreviewDto(
        IReadOnlyList<string> Paths,
        IReadOnlyList<string> Warnings,
        IReadOnlyList<string> AbortReasons,
        bool CanProceed,
        bool BinaryRemovalSupported,
        string ConfirmToken);

    public PreviewDto Preview(bool removeBinary)
    {
        var manifest = BuildManifest(removeBinary, out var binarySupported);
        var token = NewNonce();
        return new PreviewDto(
            manifest.Paths.Select(p => p.Path).ToList(),
            manifest.Warnings,
            manifest.AbortReasons,
            manifest.Ok,
            binarySupported,
            token);
    }

    // Build a single backup archive (prefs DB + every profile + logbook + an ADIF
    // export of the log) the operator downloads to ~/Downloads before wiping. The
    // browser download default IS the Downloads root, which the wipe never touches.
    public async Task<(byte[] Bytes, string FileName)> BuildBackupAsync(CancellationToken ct)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var dataDir = PrefsDbPath.DataDir;
            // All *.db in DataDir and DataDir/profiles, read shared so the live DB
            // can be copied while open.
            foreach (var (abs, entryName) in EnumerateBackupDbFiles(dataDir))
            {
                try
                {
                    var entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
                    await using var es = entry.Open();
                    await using var fs = new FileStream(abs, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    await fs.CopyToAsync(es, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "uninstall.backup skip {File}", abs);
                }
            }

            // Human-readable ADIF export of the QSO log.
            try
            {
                var adif = await _logService.ExportToAdifAsync(null, ct).ConfigureAwait(false);
                var entry = zip.CreateEntry("zeus-logbook.adi", CompressionLevel.Fastest);
                await using var es = entry.Open();
                await es.WriteAsync(Encoding.UTF8.GetBytes(adif), ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "uninstall.backup adif failed");
            }
        }
        return (ms.ToArray(), "zeus-backup.zip");
    }

    public sealed record ExecuteResult(bool Started, string? Error, IReadOnlyList<string>? AbortReasons);

    public ExecuteResult Execute(string? token, bool removeBinary)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(_nonce) || token != _nonce)
                return new ExecuteResult(false, "Invalid or expired confirmation token. Reopen the uninstall dialog.", null);
            _nonce = null; // one-shot
        }

        var manifest = BuildManifest(removeBinary, out _);
        if (!manifest.Ok)
        {
            _log.LogError("uninstall.aborted reasons={Reasons}", string.Join(" | ", manifest.AbortReasons));
            return new ExecuteResult(false, "Uninstall safety checks failed; nothing was deleted.", manifest.AbortReasons);
        }

        _log.LogWarning("uninstall.execute paths={Count} binary={Binary} — launching detached wipe and exiting.",
            manifest.Paths.Count, manifest.RemoveBinary);

        try
        {
            UninstallExecutor.Launch(manifest, dryRun: false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "uninstall.launch failed");
            return new ExecuteResult(false, $"Failed to start the uninstaller: {ex.Message}", null);
        }

        // Same clean-teardown the restart/quit paths use: let the HTTP response
        // flush, then exit so the detached helper can delete the now-unlocked files.
        _ = Task.Run(async () =>
        {
            await Task.Delay(400).ConfigureAwait(false);
            Environment.Exit(0);
        });
        return new ExecuteResult(true, null, null);
    }

    // Builds and validates the manifest, then fills the Windows install-type-aware
    // binary-removal command. binarySupported reports whether binary removal is
    // actually achievable on this install (false → data-only fallback).
    private UninstallManifest BuildManifest(bool removeBinary, out bool binarySupported)
    {
        var env = new SystemUninstallEnv();
        var manifest = UninstallManifestBuilder.Build(env, removeBinary);
        binarySupported = false;

        if (!manifest.Ok || !removeBinary)
            return manifest;

        if (env.Os == OsKind.Windows)
        {
            var (cmd, refusedReason) = WindowsInstallInfo.ResolveUninstaller();
            if (cmd is not null)
            {
                binarySupported = true;
                return manifest with { WindowsUninstallerCommand = cmd };
            }
            // Per-machine / Program Files / not found → data-only fallback.
            var warnings = manifest.Warnings.ToList();
            warnings.Add(refusedReason ?? "The Zeus binary could not be removed automatically; finish in Settings → Apps → OpenHPSDR-Zeus.");
            return manifest with { RemoveBinary = false, Warnings = warnings };
        }

        // macOS/Linux binary removal is encoded as path entries by the builder.
        binarySupported = manifest.RemoveBinary;
        return manifest;
    }

    private static IEnumerable<(string Abs, string EntryName)> EnumerateBackupDbFiles(string dataDir)
    {
        if (!Directory.Exists(dataDir)) yield break;
        foreach (var f in SafeEnumerate(dataDir, "*.db"))
            yield return (f, Path.GetFileName(f));
        var profiles = Path.Combine(dataDir, "profiles");
        if (Directory.Exists(profiles))
            foreach (var f in SafeEnumerate(profiles, "*.db"))
                yield return (f, "profiles/" + Path.GetFileName(f));
    }

    private static IEnumerable<string> SafeEnumerate(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly); }
        catch { return []; }
    }

    private string NewNonce()
    {
        var n = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        lock (_gate) { _nonce = n; }
        return n;
    }
}
