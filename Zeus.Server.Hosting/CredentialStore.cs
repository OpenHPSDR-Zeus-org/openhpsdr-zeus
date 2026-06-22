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
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using LiteDB;

namespace Zeus.Server;

public sealed class CredentialStore : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<StoredCredential> _credentials;
    private readonly ILogger<CredentialStore> _log;

    public CredentialStore(ILogger<CredentialStore> log)
        : this(log, dbPathOverride: null)
    {
    }

    // Credentials live in the plaintext config DB (zeus-prefs.db) alongside the
    // other preference stores — resolved via PrefsDbPath.Get(). The legacy
    // encrypted zeus.db is folded in once at startup by LegacyZeusDbMigration;
    // its .dbkey sat in the same directory as the DB, so the old encryption
    // protected nothing against filesystem access — consolidating to plaintext
    // removes a fragile key-management path without weakening real protection.
    //
    // Test-friendly ctor: points the store at an explicit DB file so tests don't
    // collide on the shared %LOCALAPPDATA%\Zeus\zeus-prefs.db.
    public CredentialStore(ILogger<CredentialStore> log, string? dbPathOverride)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            _log.LogInformation("Created credential store directory: {Dir}", dir);
        }

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _credentials = _db.GetCollection<StoredCredential>("credentials");
        _credentials.EnsureIndex(x => x.Service, unique: true);

        _log.LogInformation("CredentialStore initialized at {Path}", dbPath);
    }

    public async Task<StoredCredential?> GetAsync(string service, CancellationToken ct = default)
    {
        return await Task.Run(() => _credentials.FindOne(x => x.Service == service), ct);
    }

    public async Task SetAsync(string service, string username, string password, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            // Find existing credential for this service
            var existing = _credentials.FindOne(x => x.Service == service);

            if (existing != null)
            {
                // Update existing
                existing.Username = username;
                existing.Password = password;
                existing.UpdatedUtc = DateTime.UtcNow;
                _credentials.Update(existing);
            }
            else
            {
                // Insert new
                var cred = new StoredCredential
                {
                    Service = service,
                    Username = username,
                    Password = password,
                    UpdatedUtc = DateTime.UtcNow
                };
                _credentials.Insert(cred);
            }
        }, ct);

        _log.LogInformation("Stored credentials for service={Service} username={User}", service, username);
    }

    public async Task DeleteAsync(string service, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            var deleted = _credentials.DeleteMany(x => x.Service == service);
            if (deleted > 0)
            {
                _log.LogInformation("Deleted credentials for service={Service}", service);
            }
        }, ct);
    }

    public void Dispose()
    {
        _db?.Dispose();
    }
}

public sealed class StoredCredential
{
    public int Id { get; set; }
    public string Service { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }
}
