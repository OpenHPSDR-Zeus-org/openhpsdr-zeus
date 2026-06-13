// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Persistence coverage for the toolbar tuning-step / favorites bug — the
// tuning step and Mode/Band/Step favorite pins now survive a backend restart
// instead of resetting to 500 Hz every Photino desktop launch (per-launch
// random loopback port orphaned the old localStorage value). Verifies the
// fields round-trip through LiteDB, that a fresh row returns null so the
// frontend keeps its defaults, and that a partial save leaves untouched
// fields intact.

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Zeus.Server.Tests;

public class ToolbarSettingsPersistenceTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-toolbar-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private ToolbarSettingsStore BuildStore() =>
        new(NullLogger<ToolbarSettingsStore>.Instance, _dbPath);

    [Fact]
    public void FreshDb_ReturnsNullForAllFields()
    {
        using var store = BuildStore();
        var dto = store.Get();

        Assert.Null(dto.Mode);
        Assert.Null(dto.Band);
        Assert.Null(dto.Step);
        Assert.Null(dto.StepHz);
    }

    [Fact]
    public void Save_AllFields_PersistsAcrossReopen()
    {
        using (var store = BuildStore())
        {
            store.Save(
                mode: new[] { "USB", "LSB", "AM" },
                band: new[] { "80m", "40m", "20m" },
                step: new[] { "10", "100", "1000" },
                stepHz: 1000);
        }

        // Reopen to prove the values survived the LiteDB file round-trip.
        using var fresh = BuildStore();
        var dto = fresh.Get();

        Assert.Equal(new[] { "USB", "LSB", "AM" }, dto.Mode);
        Assert.Equal(new[] { "80m", "40m", "20m" }, dto.Band);
        Assert.Equal(new[] { "10", "100", "1000" }, dto.Step);
        Assert.Equal(1000, dto.StepHz);
    }

    [Fact]
    public void Save_NullFields_DoNotOverwriteExistingValues()
    {
        using (var store = BuildStore())
        {
            store.Save(
                mode: new[] { "USB", "LSB", "AM" },
                band: new[] { "80m", "40m", "20m" },
                step: new[] { "10", "100", "1000" },
                stepHz: 1000);
        }

        // Update only StepHz — favorites left as null (the common case: the
        // operator spins the step without touching the pins).
        using (var update = BuildStore())
        {
            update.Save(stepHz: 250);
        }

        using var check = BuildStore();
        var dto = check.Get();

        Assert.Equal(250, dto.StepHz);
        Assert.Equal(new[] { "USB", "LSB", "AM" }, dto.Mode);
        Assert.Equal(new[] { "80m", "40m", "20m" }, dto.Band);
        Assert.Equal(new[] { "10", "100", "1000" }, dto.Step);
    }

    [Fact]
    public void Save_UpdateStepHz_OverwritesExistingValue()
    {
        using var store = BuildStore();
        store.Save(stepHz: 500);
        store.Save(stepHz: 1000);

        var dto = store.Get();
        Assert.Equal(1000, dto.StepHz);
    }

    [Fact]
    public void Save_MalformedSlots_StoredAsNull()
    {
        using var store = BuildStore();
        // Wrong length and a blank entry — both rejected so the frontend
        // falls back to its built-in defaults rather than a broken picker.
        store.Save(mode: new[] { "USB", "LSB" }, band: new[] { "80m", "", "20m" }, stepHz: 500);

        var dto = store.Get();
        Assert.Null(dto.Mode);
        Assert.Null(dto.Band);
        Assert.Equal(500, dto.StepHz);
    }
}
