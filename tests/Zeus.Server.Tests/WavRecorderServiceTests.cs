// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

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
}
