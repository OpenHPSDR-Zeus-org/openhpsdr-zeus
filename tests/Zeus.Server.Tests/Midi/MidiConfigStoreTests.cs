// SPDX-License-Identifier: GPL-2.0-or-later
//
// Round-trip tests for MidiConfigStore (issue #18). Uses a unique temp prefs DB
// per test through the SharedLiteDatabase shared lease, so it is Windows-safe
// (no second exclusive LiteDB handle — the lesson behind the FT8 store fix).

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server.Midi;

namespace Zeus.Server.Tests.Midi;

public sealed class MidiConfigStoreTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-midi-store-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private MidiConfigStore Build() => new(NullLogger<MidiConfigStore>.Instance, _dbPath);

    [Fact]
    public void FreshDb_DefaultsToDisabledAndEmptyBindings()
    {
        using var store = Build();
        Assert.False(store.GetEnabled());
        var doc = store.GetBindings();
        Assert.Empty(doc.Mappings);
        Assert.Empty(doc.StreamDeckMappings);
    }

    [Fact]
    public void Set_RoundTripsEnabledAndBindings()
    {
        var doc = new MidiBindingsDoc(
            MidiBindingsDoc.CurrentVersion,
            new[]
            {
                new MidiMappingDto("DJ2GO2", "cc:0:7", MidiControlType.KnobOrSlider,
                    ZeusMidiCommand.SetAfGain, 0, 127, false),
                new MidiMappingDto("DJ2GO2", "note:0:60", MidiControlType.Button,
                    ZeusMidiCommand.MoxOnOff, 0, 127, true),
            },
            new[]
            {
                new StreamDeckMappingDto("ABC123", 0, ZeusMidiCommand.Band40m),
            });

        using (var store = Build())
            store.Set(enabled: true, bindings: doc);

        // Re-open (new lease) — proves it persisted to disk, not just in-memory.
        using var reopened = Build();
        Assert.True(reopened.GetEnabled());
        var got = reopened.GetBindings();
        Assert.Equal(2, got.Mappings.Count);
        Assert.Equal(ZeusMidiCommand.SetAfGain, got.Mappings[0].Command);
        Assert.Equal("cc:0:7", got.Mappings[0].ControlId);
        Assert.True(got.Mappings[1].Toggle);
        Assert.Single(got.StreamDeckMappings);
        Assert.Equal(ZeusMidiCommand.Band40m, got.StreamDeckMappings[0].Command);
    }

    [Fact]
    public void Set_OverwritesSingleRow()
    {
        using var store = Build();
        store.Set(true, MidiBindingsDoc.Empty);
        store.Set(false, new MidiBindingsDoc(
            MidiBindingsDoc.CurrentVersion,
            new[] { new MidiMappingDto("X", "cc:0:1", MidiControlType.Wheel, ZeusMidiCommand.ZoomSliderInc) },
            Array.Empty<StreamDeckMappingDto>()));

        Assert.False(store.GetEnabled());
        Assert.Single(store.GetBindings().Mappings);
    }
}
