// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using LiteDB;

namespace Zeus.Server;

// Persists the desktop (Photino) main-window geometry — width, height, and
// maximized state — so the frame reopens at the size the operator left it
// instead of snapping back to the hard-coded initial size on every restart.
//
// Lives in the same zeus-prefs.db as the other UI prefs (BottomPin,
// PanWfSplit, …). Window position is deliberately NOT persisted: restoring a
// saved Left/Top is unsafe across monitor-layout changes (unplugged external
// display, resolution change) and can drop the frame off-screen. The window
// re-centres on open instead. Maximized state is cheap and safe, so it rides
// along; the normal (non-maximized) size is tracked separately so un-maximizing
// a restored-maximized window lands at a sensible size.
//
// Desktop-only consumer (OpenhpsdrZeus RunDesktop). Registered unconditionally
// like the other stores; service / headless modes simply never read it.
public sealed class WindowGeometryStore : IDisposable
{
    // Matches the InitialWidth/InitialHeight defaults in Program.RunDesktop so a
    // fresh install (no saved doc) opens at the same size it always has.
    public const int DefaultWidth = 1680;
    public const int DefaultHeight = 1050;

    // Floor matches the SetMinWidth/SetMinHeight pinned on the Photino frame, so
    // a persisted value can never reopen the window below its usable minimum.
    public const int MinWidth = 1280;
    public const int MinHeight = 800;

    private readonly LiteDatabase _db;
    private readonly ILiteCollection<WindowGeometryEntry> _docs;
    private readonly ILogger<WindowGeometryStore> _log;
    private readonly object _sync = new();

    public WindowGeometryStore(ILogger<WindowGeometryStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _docs = _db.GetCollection<WindowGeometryEntry>("window_geometry");

        _log.LogInformation("WindowGeometryStore initialized at {Path}", dbPath);
    }

    public WindowGeometry Get()
    {
        lock (_sync)
        {
            var e = _docs.FindAll().FirstOrDefault();
            if (e is null)
                // Fresh install: open maximized (full screen) on every platform.
                // The G2's built-in panel and other small/embedded displays expect
                // a full-screen client (matching deskhpsdr), and the desktop hosts
                // benefit from the same default. The frame still carries its
                // titlebar because RunDesktop seats a normal restored size first
                // and relaxes the min size to fit small panels before maximizing —
                // see PlaceWindowOnVisibleWorkArea. Once the operator
                // resizes/un-maximizes, the saved doc below is authoritative.
                return new WindowGeometry(DefaultWidth, DefaultHeight, Maximized: true);
            return new WindowGeometry(
                ClampWidth(e.Width),
                ClampHeight(e.Height),
                e.Maximized);
        }
    }

    public void Save(int width, int height, bool maximized)
    {
        var w = ClampWidth(width);
        var h = ClampHeight(height);
        lock (_sync)
        {
            var e = _docs.FindAll().FirstOrDefault() ?? new WindowGeometryEntry();
            e.Width = w;
            e.Height = h;
            e.Maximized = maximized;
            e.UpdatedUtc = DateTime.UtcNow;
            if (e.Id == 0) _docs.Insert(e);
            else _docs.Update(e);
        }
    }

    private static int ClampWidth(int v) => v < MinWidth ? DefaultWidth : v;
    private static int ClampHeight(int v) => v < MinHeight ? DefaultHeight : v;

    public void Dispose() => _db.Dispose();
}

// Normal-size geometry plus maximized flag. Plain Hosting-layer record — not a
// Zeus.Contracts wire type, since this never crosses the SignalR / REST boundary.
public readonly record struct WindowGeometry(int Width, int Height, bool Maximized);

public sealed class WindowGeometryEntry
{
    public int Id { get; set; }
    public int Width { get; set; } = WindowGeometryStore.DefaultWidth;
    public int Height { get; set; } = WindowGeometryStore.DefaultHeight;
    public bool Maximized { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
