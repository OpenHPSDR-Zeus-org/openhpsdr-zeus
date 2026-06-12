// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.

using System.IO.Compression;
using Zeus.Server.Voyeur;

namespace Zeus.Server.Tests;

/// <summary>
/// Covers VoyeurInstallService.ExtractEngine — the new engine-bundle unzip path
/// (zeus-la5 Phase 3C). The engines are button-downloaded as a per-RID zip and
/// extracted into the engine's bin/ dir; these tests pin the behaviour that
/// matters: files land flattened in the target, a malicious "zip-slip" entry
/// cannot escape the target, and on Unix the extracted files are executable.
/// </summary>
public sealed class VoyeurEngineExtractTests
{
    private static string NewTempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "zeus-voyeur-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Extract_FlattensLeadingFolder_AndDropsFilesInBinDir()
    {
        var work = NewTempDir();
        try
        {
            var zipPath = Path.Combine(work, "whisper-osx-arm64.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                // Archivers commonly wrap everything in a top-level folder.
                AddEntry(zip, "whisper-osx-arm64/whisper-cli", "binary");
                AddEntry(zip, "whisper-osx-arm64/libfoo.dylib", "lib");
            }

            var binDir = Path.Combine(work, "bin");
            VoyeurInstallService.ExtractEngine(zipPath, binDir);

            Assert.True(File.Exists(Path.Combine(binDir, "whisper-cli")));
            Assert.True(File.Exists(Path.Combine(binDir, "libfoo.dylib")));
            // The wrapping folder must NOT survive.
            Assert.False(Directory.Exists(Path.Combine(binDir, "whisper-osx-arm64")));
        }
        finally { Directory.Delete(work, true); }
    }

    [Fact]
    public void Extract_BlocksZipSlip_OutsideBinDir()
    {
        var work = NewTempDir();
        try
        {
            var zipPath = Path.Combine(work, "evil.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                AddEntry(zip, "whisper-cli", "ok");
                // Attempt to write a sibling of binDir via path traversal.
                AddEntry(zip, "../escaped.txt", "pwned");
            }

            var binDir = Path.Combine(work, "bin");
            VoyeurInstallService.ExtractEngine(zipPath, binDir);

            Assert.True(File.Exists(Path.Combine(binDir, "whisper-cli")));
            // The traversal entry must never have escaped binDir.
            Assert.False(File.Exists(Path.Combine(work, "escaped.txt")));
        }
        finally { Directory.Delete(work, true); }
    }

    [Fact]
    public void Extract_OverwritesPriorCopy()
    {
        var work = NewTempDir();
        try
        {
            var binDir = Path.Combine(work, "bin");
            Directory.CreateDirectory(binDir);
            File.WriteAllText(Path.Combine(binDir, "whisper-cli"), "OLD");

            var zipPath = Path.Combine(work, "whisper.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                AddEntry(zip, "whisper-cli", "NEW");

            VoyeurInstallService.ExtractEngine(zipPath, binDir);

            Assert.Equal("NEW", File.ReadAllText(Path.Combine(binDir, "whisper-cli")));
        }
        finally { Directory.Delete(work, true); }
    }

    [Fact]
    public void Extract_SetsExecutableBit_OnUnix()
    {
        if (OperatingSystem.IsWindows()) return; // no Unix file mode on Windows

        var work = NewTempDir();
        try
        {
            var zipPath = Path.Combine(work, "whisper.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                AddEntry(zip, "whisper-cli", "binary");

            var binDir = Path.Combine(work, "bin");
            VoyeurInstallService.ExtractEngine(zipPath, binDir);

            var mode = File.GetUnixFileMode(Path.Combine(binDir, "whisper-cli"));
            Assert.True(mode.HasFlag(UnixFileMode.UserExecute));
        }
        finally { Directory.Delete(work, true); }
    }

    private static void AddEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var s = entry.Open();
        using var w = new StreamWriter(s);
        w.Write(content);
    }
}
