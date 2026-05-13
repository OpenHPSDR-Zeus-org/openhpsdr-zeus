// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

namespace Zeus.Server;

// Central database-path resolver for all zeus-prefs.db stores.
// When ZEUS_PREFS_PATH is set, all stores share that path instead of the
// platform default — useful for dev (/run fresh gives a throw-away DB),
// CI, or running two Zeus instances side-by-side without colliding prefs.
public static class PrefsDbPath
{
    public static string Get() =>
        Environment.GetEnvironmentVariable("ZEUS_PREFS_PATH")
        ?? DefaultPath();

    private static string DefaultPath()
    {
        var appDataDir = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);
        return Path.Combine(appDataDir, "Zeus", "zeus-prefs.db");
    }
}
