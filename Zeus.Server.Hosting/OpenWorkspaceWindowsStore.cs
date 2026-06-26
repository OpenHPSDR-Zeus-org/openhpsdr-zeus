// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using LiteDB;

namespace Zeus.Server;

// Persists the set of DETACHED workspace windows (extra Photino frames an
// operator pops off the dock by dragging a layout tab away) so they reopen on
// the next desktop launch instead of being forgotten on shutdown.
//
// Shares the same zeus-prefs.db as WindowGeometryStore and the other UI prefs.
// The persisted set is the windows that were OPEN at shutdown: the desktop
// shell replaces the whole set once, from its live in-memory window list, when
// the main window closes (see Program.RunDesktop). On the next launch the SPA
// fetches the set via GET /api/ui/workspace-windows and reopens each one
// through the same path a manual drag-off uses.
//
// Desktop-only consumer; registered unconditionally like WindowGeometryStore so
// the endpoint always resolves. Service / headless modes simply never write to
// it, and a web client never calls the restore path (it is gated on the Photino
// shell bridge), so the set stays empty there.
public sealed class OpenWorkspaceWindowsStore : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<OpenWorkspaceWindowEntry> _docs;
    private readonly ILogger<OpenWorkspaceWindowsStore> _log;
    private readonly object _sync = new();

    public OpenWorkspaceWindowsStore(
        ILogger<OpenWorkspaceWindowsStore> log,
        string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _docs = _db.GetCollection<OpenWorkspaceWindowEntry>("open_workspace_windows");
        _docs.EnsureIndex(x => x.LayoutId, unique: true);

        _log.LogInformation("OpenWorkspaceWindowsStore initialized at {Path}", dbPath);
    }

    /// <summary>Every persisted detached-window record, in insertion order.</summary>
    public IReadOnlyList<OpenWorkspaceWindowDto> GetAll()
    {
        lock (_sync)
        {
            return _docs.FindAll()
                .Where(e => !string.IsNullOrWhiteSpace(e.LayoutId))
                .Select(e => new OpenWorkspaceWindowDto(e.LayoutId, e.Title ?? string.Empty))
                .ToList();
        }
    }

    /// <summary>
    /// Replace the persisted set with the windows currently open. Called once at
    /// shutdown from the desktop shell's live window list, so the next launch
    /// reopens exactly what was on screen. Duplicate layout ids collapse to one
    /// (a layout can only meaningfully be restored to a single window) and blank
    /// ids are dropped.
    /// </summary>
    public void Replace(IEnumerable<OpenWorkspaceWindowDto> windows)
    {
        lock (_sync)
        {
            _docs.DeleteAll();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var w in windows)
            {
                if (string.IsNullOrWhiteSpace(w.LayoutId)) continue;
                if (!seen.Add(w.LayoutId)) continue;
                _docs.Insert(new OpenWorkspaceWindowEntry
                {
                    LayoutId = w.LayoutId,
                    Title = w.Title ?? string.Empty,
                    UpdatedUtc = DateTime.UtcNow,
                });
            }
        }
    }

    public void Dispose() => _db.Dispose();
}

// Returned verbatim from GET /api/ui/workspace-windows (camelCased by the
// minimal-API JSON serializer → { layoutId, title }). Not a Zeus.Contracts wire
// type — it never crosses the SignalR boundary, only the local REST surface the
// desktop shell reads at startup.
public readonly record struct OpenWorkspaceWindowDto(string LayoutId, string Title);

public sealed class OpenWorkspaceWindowEntry
{
    public int Id { get; set; }
    public string LayoutId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }
}
