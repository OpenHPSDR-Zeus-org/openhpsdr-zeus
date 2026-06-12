// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Tests for VoyeurStore (zeus-la5) — the "save and delete these logs" surface:
// session/segment persistence, pin-to-save, delete (records + audio dir), and
// the traversal guard on session audio paths.
//
// Note: VoyeurStore writes its LiteDB + audio under the OS LocalAppData/Zeus
// and Downloads/zeus-voyeur. These tests exercise the public API end-to-end on
// the real store and clean up the sessions they create, so they don't depend
// on a particular environment but do touch disk (consistent with the other
// LiteDB-backed store tests in this suite).

using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Zeus.Server.Voyeur;

namespace Zeus.Server.Tests;

public class VoyeurStoreTests
{
    private static VoyeurStore NewStore() => new(NullLogger<VoyeurStore>.Instance);

    [Fact]
    public void CreateListGetDelete_RoundTrips()
    {
        var store = NewStore();
        var created = store.CreateSession(7_255_000, "LSB", "40m", keepAudio: false);
        try
        {
            Assert.Contains(store.ListSessions(), s => s.Id == created.Id);

            var detail = store.GetSession(created.Id);
            Assert.NotNull(detail);
            Assert.Equal(7_255_000, detail!.Session.FreqHz);
            Assert.Equal("LSB", detail.Session.Mode);
            Assert.Equal("40m", detail.Session.Band);
            Assert.Empty(detail.Segments);
        }
        finally
        {
            Assert.True(store.Delete(created.Id));
            Assert.Null(store.GetSession(created.Id));
            // Idempotent delete.
            Assert.False(store.Delete(created.Id));
            store.Dispose();
        }
    }

    [Fact]
    public void AddSegment_UpdatesSessionCountsAndIsReturned()
    {
        var store = NewStore();
        var session = store.CreateSession(14_200_000, "USB", "20m", keepAudio: false);
        try
        {
            store.AddSegment(new VoyeurSegmentDocument
            {
                SessionId = session.Id,
                StartedUtc = System.DateTime.UtcNow,
                DurationMs = 4200,
                PeakDbfs = -6.5f,
            }, capturedSeconds: 4.2, droppedSamples: 0);

            var detail = store.GetSession(session.Id);
            Assert.NotNull(detail);
            Assert.Equal(1, detail!.Session.SegmentCount);
            Assert.Equal(4.2, detail.Session.CapturedSeconds, 3);
            Assert.Single(detail.Segments);
            Assert.Equal(4200, detail.Segments[0].DurationMs);
        }
        finally
        {
            store.Delete(session.Id);
            store.Dispose();
        }
    }

    [Fact]
    public void Update_RenamesAndPins()
    {
        var store = NewStore();
        var session = store.CreateSession(7_200_000, "LSB", "40m", keepAudio: false);
        try
        {
            var updated = store.Update(session.Id, label: "Saturday eCARS", pinned: true);
            Assert.NotNull(updated);
            Assert.Equal("Saturday eCARS", updated!.Label);
            Assert.True(updated.Pinned);

            // Null fields leave the existing value untouched.
            var again = store.Update(session.Id, label: null, pinned: null);
            Assert.Equal("Saturday eCARS", again!.Label);
            Assert.True(again.Pinned);
        }
        finally
        {
            store.Delete(session.Id);
            store.Dispose();
        }
    }

    [Fact]
    public void Delete_RemovesSegmentsAndAudioDir()
    {
        var store = NewStore();
        var session = store.CreateSession(7_255_000, "LSB", "40m", keepAudio: true);
        var audioDir = store.SessionAudioDir(session.Id);
        File.WriteAllText(Path.Combine(audioDir, "over-test.wav"), "x");
        store.AddSegment(new VoyeurSegmentDocument
        {
            SessionId = session.Id,
            StartedUtc = System.DateTime.UtcNow,
            DurationMs = 1000,
            AudioFile = "over-test.wav",
        }, 1.0, 0);

        Assert.True(Directory.Exists(audioDir));
        Assert.True(store.Delete(session.Id));
        Assert.False(Directory.Exists(audioDir), "audio dir should be removed on delete");
        Assert.Null(store.GetSession(session.Id));
        store.Dispose();
    }

    [Fact]
    public void SessionAudioDir_NeutralizesTraversal_StaysUnderRoot()
    {
        var store = NewStore();
        try
        {
            // A crafted id with path separators is sanitized to safe characters,
            // so the resolved dir can never escape the Voyeur root.
            var dir = store.SessionAudioDir("../../etc/passwd");
            var rootFull = Path.GetFullPath(store.AudioRoot);
            Assert.StartsWith(rootFull, Path.GetFullPath(dir));
            Assert.DoesNotContain("..", Path.GetRelativePath(rootFull, dir));
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        finally
        {
            store.Dispose();
        }
    }
}
