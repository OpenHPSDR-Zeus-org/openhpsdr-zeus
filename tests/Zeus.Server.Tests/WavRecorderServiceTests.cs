// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Protocol1;
using Zeus.Server;
using Zeus.Server.Wav;

namespace Zeus.Server.Tests;

/// <summary>
/// Deterministic coverage for the tape-deck file management (<see cref="WavLibrary"/>),
/// the level meter (<see cref="WavMeter"/>), and the header-only WAV info read
/// (<see cref="WavFile.ReadInfo"/>). The audio / radio orchestration in
/// <see cref="WavRecorderService"/> (MOX keying, the playback pump) is covered
/// by review — it needs a live TxService / radio — but every file, listing,
/// CRUD, traversal-guard, and meter behaviour is exercised here against an
/// isolated temp root.
/// </summary>
public sealed class WavRecorderServiceTests
{
    // Each test gets a fresh, unique sandbox whose parent contains nothing of
    // ours, so the ctor's loose-file migration is a no-op unless the test seeds
    // it deliberately.
    private static (string Sandbox, string Root) NewSandbox()
    {
        string sandbox = Path.Combine(Path.GetTempPath(), "zeus-wavtests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        return (sandbox, Path.Combine(sandbox, WavLibrary.ManagedFolderName));
    }

    private static void WriteWav(string path, int sampleRate, int samples)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var w = new WavWriter(path, sampleRate);
        w.Append(new float[samples]);
    }

    [Fact]
    public void List_ReturnsRelPathFolderDurationAndSourceAcrossTree()
    {
        var (sandbox, root) = NewSandbox();
        try
        {
            var lib = new WavLibrary(root);
            // One at the root, one in a subfolder.
            WriteWav(Path.Combine(root, "zeus-rx-20260101-000000.wav"), 48_000, 48_000); // 1.0s
            WriteWav(Path.Combine(root, "DX", "zeus-tx-20260101-000100.wav"), 48_000, 24_000); // 0.5s
            // A non-prefixed wav must still be listed (root is wholly ours) and
            // reported as source "unknown".
            WriteWav(Path.Combine(root, "imported.wav"), 48_000, 96_000); // 2.0s

            var recs = lib.ListRecordings();
            Assert.Equal(3, recs.Count);

            var rx = recs.Single(r => r.FileName == "zeus-rx-20260101-000000.wav");
            Assert.Equal("zeus-rx-20260101-000000", rx.Name);
            Assert.Equal("zeus-rx-20260101-000000.wav", rx.RelPath);
            Assert.Equal("", rx.Folder);
            Assert.Equal("rx", rx.Source);
            Assert.Equal(1.0, rx.DurationSec, 1);

            var tx = recs.Single(r => r.FileName == "zeus-tx-20260101-000100.wav");
            Assert.Equal("DX/zeus-tx-20260101-000100.wav", tx.RelPath);
            Assert.Equal("DX", tx.Folder);
            Assert.Equal("tx", tx.Source);
            Assert.Equal(0.5, tx.DurationSec, 1);

            var other = recs.Single(r => r.FileName == "imported.wav");
            Assert.Equal("unknown", other.Source);
            Assert.Equal(2.0, other.DurationSec, 1);

            // Folder listing includes the subfolder, forward-slashed.
            Assert.Contains("DX", lib.ListFolders());
        }
        finally { Directory.Delete(sandbox, true); }
    }

    [Fact]
    public void ReadInfo_MatchesRateAndSampleCountAndReadAllSamples()
    {
        var (sandbox, root) = NewSandbox();
        try
        {
            string path = Path.Combine(root, "zeus-rx-info.wav");
            const int rate = 48_000;
            const int count = 12_345;
            WriteWav(path, rate, count);

            var (infoRate, infoCount) = WavFile.ReadInfo(path);
            Assert.Equal(rate, infoRate);
            Assert.Equal(count, infoCount);

            var (samples, allRate) = WavFile.ReadAllSamples(path);
            Assert.Equal(rate, allRate);
            Assert.Equal(count, samples.Length);
            Assert.Equal(samples.Length, infoCount);
        }
        finally { Directory.Delete(sandbox, true); }
    }

    [Fact]
    public void Rename_PreservesExtension_SanitizesIllegalChars_RefusesCollision()
    {
        var (sandbox, root) = NewSandbox();
        try
        {
            var lib = new WavLibrary(root);
            WriteWav(Path.Combine(root, "zeus-rx-a.wav"), 48_000, 100);
            WriteWav(Path.Combine(root, "zeus-rx-b.wav"), 48_000, 100);

            // Sanitisation: a path separator and a NUL (illegal on every OS) are
            // stripped; a caller-supplied ".wav" is not doubled.
            string rel = lib.RenameRecording("zeus-rx-a.wav", "Sub/My Clip\0.wav");
            Assert.Equal("My Clip.wav", rel);
            Assert.True(File.Exists(Path.Combine(root, "My Clip.wav")));
            Assert.False(File.Exists(Path.Combine(root, "zeus-rx-a.wav")));

            // Collision: renaming b onto the existing "My Clip" is refused.
            Assert.Throws<InvalidOperationException>(
                () => lib.RenameRecording("zeus-rx-b.wav", "My Clip"));
            Assert.True(File.Exists(Path.Combine(root, "zeus-rx-b.wav")));
        }
        finally { Directory.Delete(sandbox, true); }
    }

    [Fact]
    public void Move_RelocatesBetweenFolders()
    {
        var (sandbox, root) = NewSandbox();
        try
        {
            var lib = new WavLibrary(root);
            WriteWav(Path.Combine(root, "zeus-rx-x.wav"), 48_000, 100);

            string rel = lib.MoveRecording("zeus-rx-x.wav", "Contests/2026");
            Assert.Equal("Contests/2026/zeus-rx-x.wav", rel);
            Assert.True(File.Exists(Path.Combine(root, "Contests", "2026", "zeus-rx-x.wav")));
            Assert.False(File.Exists(Path.Combine(root, "zeus-rx-x.wav")));
        }
        finally { Directory.Delete(sandbox, true); }
    }

    [Fact]
    public void Folder_CreateAndDelete_DeleteRefusesNonWavContents()
    {
        var (sandbox, root) = NewSandbox();
        try
        {
            var lib = new WavLibrary(root);

            string folder = lib.CreateFolder("Nets/Weekly");
            Assert.Equal("Nets/Weekly", folder);
            Assert.True(Directory.Exists(Path.Combine(root, "Nets", "Weekly")));

            // A folder holding only wavs deletes recursively.
            WriteWav(Path.Combine(root, "Nets", "Weekly", "zeus-rx-net.wav"), 48_000, 100);
            string deleted = lib.DeleteFolder("Nets/Weekly");
            Assert.Equal("Nets/Weekly", deleted);
            Assert.False(Directory.Exists(Path.Combine(root, "Nets", "Weekly")));

            // A folder with a non-wav file is refused and left intact.
            string keep = Path.Combine(root, "Mixed");
            Directory.CreateDirectory(keep);
            WriteWav(Path.Combine(keep, "zeus-rx-keep.wav"), 48_000, 100);
            File.WriteAllText(Path.Combine(keep, "notes.txt"), "hello");
            Assert.Throws<InvalidOperationException>(() => lib.DeleteFolder("Mixed"));
            Assert.True(Directory.Exists(keep));
        }
        finally { Directory.Delete(sandbox, true); }
    }

    [Theory]
    [InlineData("../escape.wav")]
    [InlineData("DX/../../escape.wav")]
    [InlineData("/etc/passwd")]
    public void ResolveRel_RejectsTraversalAndAbsolute(string bad)
    {
        var (sandbox, root) = NewSandbox();
        try
        {
            var lib = new WavLibrary(root);
            Assert.Throws<ArgumentException>(() => lib.ResolveRel(bad));
        }
        finally { Directory.Delete(sandbox, true); }
    }

    [Fact]
    public void ResolveRel_AcceptsNestedRelativePathInsideRoot()
    {
        var (sandbox, root) = NewSandbox();
        try
        {
            var lib = new WavLibrary(root);
            string resolved = lib.ResolveRel("DX/sub/clip.wav");
            Assert.StartsWith(Path.GetFullPath(root), resolved);
        }
        finally { Directory.Delete(sandbox, true); }
    }

    [Fact]
    public void Meter_FullScaleBlock_PeaksAtOneAndLatchesClip()
    {
        var m = new WavMeter();
        var fullScale = new float[960];
        Array.Fill(fullScale, 1.0f);
        m.Update(fullScale);

        Assert.True(m.Peak >= 0.999, $"peak was {m.Peak}");
        Assert.True(m.Rms >= 0.999, $"rms was {m.Rms}");
        Assert.True(m.Clip);
        Assert.True(m.PeakDb >= -0.1, $"peakDb was {m.PeakDb}");
    }

    [Fact]
    public void Meter_HalfScaleBlock_NoClip_RmsHalf()
    {
        var m = new WavMeter();
        var half = new float[480];
        Array.Fill(half, 0.5f);
        m.Update(half);

        Assert.InRange(m.Peak, 0.49, 0.51);
        Assert.InRange(m.Rms, 0.49, 0.51);
        Assert.False(m.Clip);
    }

    [Fact]
    public void Meter_Reset_ReturnsToSilence()
    {
        var m = new WavMeter();
        var fullScale = new float[100];
        Array.Fill(fullScale, 1.0f);
        m.Update(fullScale);
        m.Reset();

        Assert.Equal(0, m.Peak);
        Assert.Equal(0, m.Rms);
        Assert.False(m.Clip);
        Assert.Equal(-100, m.PeakDb);
    }

    [Fact]
    public void Envelope_ProducesRequestedBucketsWithPerSlicePeaks()
    {
        // 1000 samples: first half silent, second half at 0.5. With 2 buckets the
        // first must be ~0 and the second ~0.5.
        var samples = new float[1000];
        for (int i = 500; i < 1000; i++) samples[i] = 0.5f;

        var env = WavFile.Envelope(samples, 2);
        Assert.Equal(2, env.Length);
        Assert.Equal(0f, env[0], 3);
        Assert.Equal(0.5f, env[1], 3);

        // Every value is a 0..1 magnitude.
        var many = WavFile.Envelope(samples, 400);
        Assert.Equal(400, many.Length);
        Assert.All(many, v => Assert.InRange(v, 0f, 1f));

        // Fewer samples than buckets ⇒ one |sample| per sample.
        var tiny = WavFile.Envelope(new float[] { -0.25f, 0.75f }, 400);
        Assert.Equal(2, tiny.Length);
        Assert.Equal(0.25f, tiny[0], 3);
        Assert.Equal(0.75f, tiny[1], 3);
    }

    [Fact]
    public void Migration_MovesLooseZeusWavFromParentIntoRoot()
    {
        var (sandbox, root) = NewSandbox();
        try
        {
            // Loose legacy file sitting directly in the parent ("Downloads").
            string loose = Path.Combine(sandbox, "zeus-rx-legacy.wav");
            WriteWav(loose, 48_000, 100);
            // A non-ours file in the parent must be left alone.
            string keep = Path.Combine(sandbox, "vacation.wav");
            WriteWav(keep, 48_000, 100);

            // Ctor runs the migration.
            _ = new WavLibrary(root);

            Assert.False(File.Exists(loose), "loose zeus file should have moved out of parent");
            Assert.True(File.Exists(Path.Combine(root, "zeus-rx-legacy.wav")), "should land in root");
            Assert.True(File.Exists(keep), "non-zeus parent file must be untouched");
        }
        finally { Directory.Delete(sandbox, true); }
    }

    [Fact]
    public void Migration_Disabled_LeavesLooseZeusWavInParent()
    {
        var (sandbox, root) = NewSandbox();
        try
        {
            // A loose legacy file sitting directly in the parent. With migration
            // off (the contract for an operator-chosen custom root) it must NOT
            // be scanned for or moved — we never touch an arbitrary user dir.
            string loose = Path.Combine(sandbox, "zeus-rx-legacy.wav");
            WriteWav(loose, 48_000, 100);

            _ = new WavLibrary(root, log: null, migrate: false);

            Assert.True(File.Exists(loose), "loose zeus file must stay put when migration is disabled");
            Assert.False(File.Exists(Path.Combine(root, "zeus-rx-legacy.wav")),
                "nothing should have been migrated into the root");
        }
        finally { Directory.Delete(sandbox, true); }
    }

    [Fact]
    public void SetRoot_RelocatesEffectiveRoot_ListReadsFromNewRoot()
    {
        var (sandbox, root) = NewSandbox();
        try
        {
            var lib = new WavLibrary(root, log: null, migrate: false);
            WriteWav(Path.Combine(root, "zeus-rx-old.wav"), 48_000, 100);
            Assert.Single(lib.ListRecordings());

            // Point the library at a fresh root with different content.
            string newRoot = Path.Combine(sandbox, "Relocated");
            lib.SetRoot(newRoot);
            Assert.Equal(newRoot, lib.Root);
            Assert.True(Directory.Exists(newRoot), "SetRoot creates the directory");

            // Listing now reads from the new root, not the old one.
            Assert.Empty(lib.ListRecordings());
            WriteWav(Path.Combine(newRoot, "zeus-rx-new.wav"), 48_000, 100);
            var recs = lib.ListRecordings();
            Assert.Single(recs);
            Assert.Equal("zeus-rx-new.wav", recs[0].FileName);
        }
        finally { Directory.Delete(sandbox, true); }
    }

    [Fact]
    public void SettingsStore_RoundTripsRoot_AndClearsToNull()
    {
        var (sandbox, _) = NewSandbox();
        try
        {
            string dbPath = Path.Combine(sandbox, "wav-settings.db");

            // Fresh store: nothing persisted yet.
            using (var store = new WavRecorderSettingsStore(NullLogger<WavRecorderSettingsStore>.Instance, dbPath))
            {
                Assert.Null(store.GetRoot());

                string chosen = Path.Combine(sandbox, "MyRecordings");
                store.SetRoot(chosen);
                Assert.Equal(chosen, store.GetRoot());
            }

            // Re-open: the value survives across store instances (LiteDB on disk).
            using (var reopened = new WavRecorderSettingsStore(NullLogger<WavRecorderSettingsStore>.Instance, dbPath))
            {
                Assert.Equal(Path.Combine(sandbox, "MyRecordings"), reopened.GetRoot());

                // Clearing with null resets to "use default".
                reopened.SetRoot(null);
                Assert.Null(reopened.GetRoot());

                // Whitespace is treated as null too.
                reopened.SetRoot("   ");
                Assert.Null(reopened.GetRoot());
            }
        }
        finally { Directory.Delete(sandbox, true); }
    }

    [Fact]
    public void BrowseDirectories_ListsSubdirsAndParent_SortedOrdinalIgnoreCase()
    {
        var (sandbox, _) = NewSandbox();
        try
        {
            // Build a small tree under the sandbox.
            Directory.CreateDirectory(Path.Combine(sandbox, "Beta"));
            Directory.CreateDirectory(Path.Combine(sandbox, "alpha"));
            Directory.CreateDirectory(Path.Combine(sandbox, "Gamma"));
            // A file at the top level must be ignored (dirs only).
            File.WriteAllText(Path.Combine(sandbox, "note.txt"), "x");

            var listing = WavLibrary.BrowseDirectories(sandbox);

            Assert.Equal(Path.GetFullPath(sandbox), listing.Path);
            Assert.Equal(Path.DirectorySeparatorChar.ToString(), listing.Separator);
            Assert.Equal(Directory.GetParent(sandbox)!.FullName, listing.Parent);

            // Subdirectories only, sorted ordinal-ignore-case (alpha, Beta, Gamma).
            Assert.Equal(new[] { "alpha", "Beta", "Gamma" }, listing.Dirs.Select(d => d.Name).ToArray());
            Assert.All(listing.Dirs, d => Assert.True(Directory.Exists(d.Path)));
        }
        finally { Directory.Delete(sandbox, true); }
    }

    [Fact]
    public void BrowseDirectories_NonexistentPath_Throws()
    {
        var (sandbox, _) = NewSandbox();
        try
        {
            string missing = Path.Combine(sandbox, "does-not-exist");
            Assert.Throws<DirectoryNotFoundException>(() => WavLibrary.BrowseDirectories(missing));
        }
        finally { Directory.Delete(sandbox, true); }
    }

    // ---- H2: migration must never clobber an existing destination -----------

    [Fact]
    public void Migration_DestinationCollision_LeavesLooseFileAndDoesNotOverwrite()
    {
        var (sandbox, root) = NewSandbox();
        try
        {
            // Pre-existing file already in the root, distinctive sample count.
            WriteWav(Path.Combine(root, "zeus-rx-x.wav"), 48_000, 999);
            // A loose legacy file of the SAME name sitting in the parent.
            WriteWav(Path.Combine(sandbox, "zeus-rx-x.wav"), 48_000, 100);

            _ = new WavLibrary(root); // ctor migration runs

            // Collision → loose file untouched, root file NOT overwritten.
            Assert.True(File.Exists(Path.Combine(sandbox, "zeus-rx-x.wav")),
                "loose file must remain when the destination already exists");
            Assert.Equal(100, WavFile.ReadInfo(Path.Combine(sandbox, "zeus-rx-x.wav")).SampleCount);
            Assert.Equal(999, WavFile.ReadInfo(Path.Combine(root, "zeus-rx-x.wav")).SampleCount);
        }
        finally { Directory.Delete(sandbox, true); }
    }

    // ---- H3: DeleteFolder catastrophic-delete guard -------------------------

    [Fact]
    public void DeleteFolder_RefusesRootAndUserContent_LeavesTreeIntact()
    {
        var (sandbox, root) = NewSandbox();
        try
        {
            var lib = new WavLibrary(root);
            WriteWav(Path.Combine(root, "zeus-rx-keep.wav"), 48_000, 100);

            // "." resolves to the root itself — must refuse; root survives.
            Assert.Throws<InvalidOperationException>(() => lib.DeleteFolder("."));
            Assert.True(Directory.Exists(root));
            Assert.True(File.Exists(Path.Combine(root, "zeus-rx-keep.wav")));

            // A subtree holding genuine user content is refused, left intact.
            WriteWav(Path.Combine(root, "Mixed", "zeus-rx.wav"), 48_000, 100);
            Directory.CreateDirectory(Path.Combine(root, "Mixed", "sub"));
            File.WriteAllText(Path.Combine(root, "Mixed", "sub", "notes.txt"), "keep me");
            Assert.Throws<InvalidOperationException>(() => lib.DeleteFolder("Mixed"));
            Assert.True(File.Exists(Path.Combine(root, "Mixed", "zeus-rx.wav")));
            Assert.True(File.Exists(Path.Combine(root, "Mixed", "sub", "notes.txt")));

            // An empty folder deletes cleanly.
            lib.CreateFolder("Empty");
            Assert.Equal("Empty", lib.DeleteFolder("Empty"));
            Assert.False(Directory.Exists(Path.Combine(root, "Empty")));
        }
        finally { Directory.Delete(sandbox, true); }
    }

    // ---- M2: OS sidecar files are ignorable, not blocking -------------------

    [Fact]
    public void DeleteFolder_IgnoresOsSidecars_StillRefusesRealContent()
    {
        var (sandbox, root) = NewSandbox();
        try
        {
            var lib = new WavLibrary(root);

            // wav + macOS/Windows sidecars → deletes successfully.
            WriteWav(Path.Combine(root, "Sidecar", "zeus-rx.wav"), 48_000, 100);
            File.WriteAllText(Path.Combine(root, "Sidecar", ".DS_Store"), "x");
            File.WriteAllText(Path.Combine(root, "Sidecar", "Thumbs.db"), "x");
            Assert.Equal("Sidecar", lib.DeleteFolder("Sidecar"));
            Assert.False(Directory.Exists(Path.Combine(root, "Sidecar")));

            // wav + a genuine user file → still refused.
            WriteWav(Path.Combine(root, "Real", "zeus-rx.wav"), 48_000, 100);
            File.WriteAllText(Path.Combine(root, "Real", "notes.txt"), "hello");
            Assert.Throws<InvalidOperationException>(() => lib.DeleteFolder("Real"));
            Assert.True(Directory.Exists(Path.Combine(root, "Real")));
        }
        finally { Directory.Delete(sandbox, true); }
    }

    // ---- M5: portable filename sanitization on every platform ---------------

    [Fact]
    public void Rename_StripsBackslashAndReservedChars_RoundTripsOnAllPlatforms()
    {
        var (sandbox, root) = NewSandbox();
        try
        {
            var lib = new WavLibrary(root);
            WriteWav(Path.Combine(root, "zeus-rx-a.wav"), 48_000, 100);

            // A backslash must never survive into the on-disk name (it would
            // desync from the forward-slash wire path). Result is a single
            // segment that round-trips through the path resolver.
            string rel = lib.RenameRecording("zeus-rx-a.wav", "a\\b");
            Assert.DoesNotContain('\\', rel);
            Assert.DoesNotContain('/', rel);
            Assert.True(File.Exists(lib.ResolveRel(rel)), "renamed file must resolve");
            // Round-trips through a subsequent Move resolve (no FileNotFound).
            string moved = lib.MoveRecording(rel, "Dest");
            Assert.True(File.Exists(lib.ResolveRel(moved)));

            // Every Windows-reserved char is stripped, on any platform.
            WriteWav(Path.Combine(root, "zeus-rx-b.wav"), 48_000, 100);
            string rel2 = lib.RenameRecording("zeus-rx-b.wav", "keep:me*?\"<>|ok");
            string name2 = Path.GetFileName(lib.ResolveRel(rel2));
            foreach (char c in new[] { ':', '*', '?', '"', '<', '>', '|', '\\', '/' })
                Assert.DoesNotContain(c, name2);
            Assert.EndsWith(".wav", name2);
            Assert.True(File.Exists(lib.ResolveRel(rel2)));
        }
        finally { Directory.Delete(sandbox, true); }
    }

    // ---- M5b: Windows reserved device names made portable on every platform -

    [Theory]
    [InlineData("NUL", "NUL_")]
    [InlineData("com1", "com1_")]
    public void Rename_WindowsReservedDeviceName_IsDisambiguatedAndRoundTrips(string input, string expectedStem)
    {
        var (sandbox, root) = NewSandbox();
        try
        {
            var lib = new WavLibrary(root);
            WriteWav(Path.Combine(root, "zeus-rx-a.wav"), 48_000, 100);

            // A reserved device name (unopenable on Windows) gets an underscore
            // suffix on ALL platforms so the on-disk name is portable.
            string rel = lib.RenameRecording("zeus-rx-a.wav", input);
            Assert.Equal(expectedStem + ".wav", Path.GetFileName(rel));
            // Round-trips through the path resolver (no FileNotFoundException).
            Assert.True(File.Exists(lib.ResolveRel(rel)));
        }
        finally { Directory.Delete(sandbox, true); }
    }

    // ---- M9: Move refuses to overwrite an existing destination --------------

    [Fact]
    public void Move_RefusesOverwrite_LeavesBothUnchanged()
    {
        var (sandbox, root) = NewSandbox();
        try
        {
            var lib = new WavLibrary(root);
            WriteWav(Path.Combine(root, "zeus-rx-x.wav"), 48_000, 100);
            WriteWav(Path.Combine(root, "Dest", "zeus-rx-x.wav"), 48_000, 200);

            Assert.Throws<InvalidOperationException>(
                () => lib.MoveRecording("zeus-rx-x.wav", "Dest"));

            Assert.Equal(100, WavFile.ReadInfo(Path.Combine(root, "zeus-rx-x.wav")).SampleCount);
            Assert.Equal(200, WavFile.ReadInfo(Path.Combine(root, "Dest", "zeus-rx-x.wav")).SampleCount);
        }
        finally { Directory.Delete(sandbox, true); }
    }

    // ---- M10: traversal guard covers backslash + rooted inputs --------------

    [Theory]
    [InlineData("..\\..\\escape.wav")]
    [InlineData("DX\\..\\..\\x.wav")]
    public void ResolveRel_RejectsBackslashTraversal_OnAllPlatforms(string bad)
    {
        var (sandbox, root) = NewSandbox();
        try
        {
            var lib = new WavLibrary(root);
            Assert.Throws<ArgumentException>(() => lib.ResolveRel(bad));
        }
        finally { Directory.Delete(sandbox, true); }
    }

    [Fact]
    public void ResolveRel_RejectsWindowsRootedPaths_OnWindows()
    {
        if (!OperatingSystem.IsWindows()) return; // drive/UNC roots are Windows-only
        var (sandbox, root) = NewSandbox();
        try
        {
            var lib = new WavLibrary(root);
            Assert.Throws<ArgumentException>(() => lib.ResolveRel("C:\\Windows"));
            Assert.Throws<ArgumentException>(() => lib.ResolveRel("\\\\server\\share\\x"));
        }
        finally { Directory.Delete(sandbox, true); }
    }

    // ---- L7: injectable clock drives decay + clip-latch deterministically ---

    [Fact]
    public void Meter_WithInjectedClock_DecaysAndClearsClipLatch()
    {
        long now = 1_000_000;
        var m = new WavMeter(() => now);

        var full = new float[480];
        Array.Fill(full, 1.0f);
        m.Update(full);                 // peak 1.0, clip latch lit until now+1000
        Assert.True(m.Peak >= 0.999);
        Assert.True(m.Clip);

        // 20 dB/sec ⇒ after 500 ms the linear peak is 10^(-0.5) ≈ 0.316.
        now += 500;
        m.Update(new float[480]);       // silent block at +500 ms
        Assert.InRange(m.Peak, 0.30, 0.33);

        // ClipHoldMs = 1000: still lit at +500, clears past +1000 from the clip.
        Assert.True(m.Clip);
        now += 600;                     // +1100 ms from the clipped block
        Assert.False(m.Clip);
    }

    // ---- L8: Envelope boundary conditions -----------------------------------

    [Fact]
    public void Envelope_EdgeCases()
    {
        // buckets clamps up to a minimum of 1.
        Assert.Single(WavFile.Envelope(new float[4], 0));

        // Empty input returns `buckets` zeros.
        var z = WavFile.Envelope(Array.Empty<float>(), 8);
        Assert.Equal(8, z.Length);
        Assert.All(z, v => Assert.Equal(0f, v));

        // Fewer samples than buckets ⇒ one |sample| per sample.
        Assert.Equal(8, WavFile.Envelope(new float[8], 10000).Length);

        // length == buckets ⇒ per-sample magnitudes.
        var per = WavFile.Envelope(new float[] { -0.1f, 0.2f, -0.3f, 0.4f }, 4);
        Assert.Equal(4, per.Length);
        Assert.Equal(0.1f, per[0], 3);
        Assert.Equal(0.3f, per[2], 3);
    }

    // ---- L10: a corrupt .wav is listed (duration 0), never drops the listing -

    [Fact]
    public void List_IncludesCorruptWav_WithZeroDuration()
    {
        var (sandbox, root) = NewSandbox();
        try
        {
            var lib = new WavLibrary(root);
            WriteWav(Path.Combine(root, "zeus-rx-good.wav"), 48_000, 48_000); // 1.0s
            File.WriteAllText(Path.Combine(root, "broken.wav"), "not a wav");

            var recs = lib.ListRecordings();
            Assert.Equal(2, recs.Count);
            Assert.Equal(1.0, recs.Single(r => r.FileName == "zeus-rx-good.wav").DurationSec, 1);
            Assert.Equal(0, recs.Single(r => r.FileName == "broken.wav").DurationSec);
        }
        finally { Directory.Delete(sandbox, true); }
    }

    // ---- M4: over-air playback keys MOX, returns to Idle, releases the key ---
    // Exercises the live WavRecorderService against a real TxService/RadioService
    // (FIX 3's normal key/release lifecycle). A deterministic forced-failure after
    // keying isn't unit-triggerable (Thread.Start can't be made to throw on cue),
    // so the unwind catch is covered by review; this proves we always release a
    // key we raised on the happy path and on clip end.

    [Fact]
    public void Play_OverAir_KeysMoxThenReleasesOnFinish()
    {
        var (sandbox, root) = NewSandbox();
        string dbPath = Path.Combine(sandbox, $"prefs-{Guid.NewGuid():N}.db");
        try
        {
            var loggerFactory = NullLoggerFactory.Instance;
            var dspStore = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, dbPath);
            var paStore = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, dbPath + ".pa");
            var radio = new RadioService(loggerFactory, dspStore, paStore);
            radio.MarkProtocol2Connected("127.0.0.1:1024", 48_000);
            var hub = new StreamingHub(new NullLogger<StreamingHub>());
            var pipeline = new DspPipelineService(radio, hub, Array.Empty<IRxAudioSink>(), loggerFactory);
            var tx = new TxService(radio, pipeline, hub, NullBandPlanService.Instance, new NullLogger<TxService>());
            var ring = new TxIqRing();
            using var ingest = new TxAudioIngest(ring, pipeline, tx, hub, new NullLogger<TxAudioIngest>());
            using var settings = new WavRecorderSettingsStore(
                NullLogger<WavRecorderSettingsStore>.Instance, dbPath + ".wav");
            using var wav = new WavRecorderService(
                pipeline, ingest, tx, radio, NullLogger<WavRecorderService>.Instance, settings,
                recordingsRootOverride: root);

            // A short clip (0.1 s @ 48 kHz) so the pump finishes quickly.
            string rel = "zeus-tx-air.wav";
            WriteWav(Path.Combine(root, rel), 48_000, 4_800);

            Assert.False(tx.IsMoxOn);
            wav.Play(rel, WavPlayDest.Air);
            Assert.True(tx.IsMoxOn, "over-air playback must key MOX when the rig is unkeyed");

            // Pump runs on its own thread; wait for it to finish and drop the key.
            var sw = Stopwatch.StartNew();
            while (wav.GetStatus().State != "idle" && sw.ElapsedMilliseconds < 5_000)
                Thread.Sleep(20);

            Assert.Equal("idle", wav.GetStatus().State);
            Assert.False(tx.IsMoxOn, "MOX must be released after over-air playback ends");
        }
        finally
        {
            try { Directory.Delete(sandbox, true); } catch { }
        }
    }
}
