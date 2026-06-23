// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Per-install settings for the ANAN G2 / G2-Ultra hardware front-panel bridge:
// the master enable, an explicit serial device (COM port / device path), and
// the baud override. These layer OVER the G2FrontPanel config section in
// G2FrontPanelService — a stored value wins, an unset one falls back to config
// then to auto-detect. Surfaced in Radio Settings so an operator on a host
// where the panel isn't a Linux by-id symlink (e.g. Windows COMx) can point the
// bridge at the right port without editing appsettings.
//
// Defaults ON with no device override: byte-for-byte the historical behaviour
// (auto-detect the g2-front symlink, idle when absent). Single-row LiteDB
// collection ("g2_panel_settings") sharing zeus-prefs.db, mirroring
// PttSettingsStore. Insert/Update (NOT Upsert with Id=0) avoids the LiteDB
// Id=0-always-inserts bug (PR #387).

using LiteDB;

namespace Zeus.Server.FrontPanel;

public sealed class G2PanelSettingsStore : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<G2PanelSettingsEntry> _rows;
    private readonly ILogger<G2PanelSettingsStore> _log;
    private readonly object _sync = new();

    // Fired on any write so the front-panel service re-resolves + reconnects.
    public event Action? Changed;

    public G2PanelSettingsStore(ILogger<G2PanelSettingsStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _rows = _db.GetCollection<G2PanelSettingsEntry>("g2_panel_settings");

        _log.LogInformation("G2PanelSettingsStore initialized at {Path}", dbPath);
    }

    /// <summary>Current settings. A missing row (fresh install / pre-feature DB)
    /// returns the defaults: Enabled=true, no device override (auto-detect),
    /// Baud=0 (auto) — identical to the historical config-only behaviour.</summary>
    public G2PanelSettingsEntry Get()
    {
        lock (_sync)
            return _rows.FindAll().FirstOrDefault() ?? new G2PanelSettingsEntry();
    }

    /// <summary>Replace the stored settings. <paramref name="devicePath"/> empty/
    /// null = auto-detect; <paramref name="baud"/> 0 = auto. Insert-then-Update
    /// (matching PttSettingsStore) avoids the LiteDB Id=0 upsert bug (PR #387).</summary>
    public void Set(bool enabled, string? devicePath, int baud)
    {
        var normPath = string.IsNullOrWhiteSpace(devicePath) ? null : devicePath.Trim();
        var normBaud = baud > 0 ? baud : 0;
        lock (_sync)
        {
            var existing = _rows.FindAll().FirstOrDefault();
            var nowUtc = DateTime.UtcNow;
            if (existing is null)
            {
                _rows.Insert(new G2PanelSettingsEntry
                {
                    Enabled = enabled,
                    DevicePath = normPath,
                    Baud = normBaud,
                    UpdatedUtc = nowUtc,
                });
            }
            else
            {
                existing.Enabled = enabled;
                existing.DevicePath = normPath;
                existing.Baud = normBaud;
                existing.UpdatedUtc = nowUtc;
                _rows.Update(existing);
            }
        }
        Changed?.Invoke();
    }

    public void Dispose() => _db.Dispose();
}

public sealed class G2PanelSettingsEntry
{
    public int Id { get; set; }
    // Master enable. Default true — matches the historical config default, so a
    // fresh install keeps auto-detecting the panel.
    public bool Enabled { get; set; } = true;
    // Explicit serial device (COM5 / /dev/ttyACM0). Null = auto-detect.
    public string? DevicePath { get; set; }
    // Baud override. 0 = auto (config, then per-symlink default, then 9600).
    public int Baud { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
