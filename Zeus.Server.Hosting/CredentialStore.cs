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

using System.Security.Cryptography;
using System.Text;
using LiteDB;

namespace Zeus.Server;

public sealed class CredentialStore : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<StoredCredential> _credentials;
    private readonly ILogger<CredentialStore> _log;
    private readonly string? _dataDirectoryOverride;

    public CredentialStore(ILogger<CredentialStore> log)
        : this(log, dataDirectoryOverride: null)
    {
    }

    // Test-friendly ctor: lets callers point the store at an isolated directory
    // so they don't share %LOCALAPPDATA%\Zeus\zeus.db with other tests on the
    // same machine. Production DI uses the parameterless overload above.
    public CredentialStore(ILogger<CredentialStore> log, string? dataDirectoryOverride)
    {
        _log = log;
        _dataDirectoryOverride = dataDirectoryOverride;
        var dbPath = GetDatabasePath();
        var dbPassword = GetOrCreateDatabasePassword();

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            _log.LogInformation("Created credential store directory: {Dir}", dir);
        }

        var connectionString = $"Filename={dbPath};Password={dbPassword};Connection=shared";
        _db = new LiteDatabase(connectionString);
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

    private string GetDatabasePath()
    {
        return Path.Combine(GetZeusDir(), "zeus.db");
    }

    private string GetZeusDir()
    {
        if (!string.IsNullOrEmpty(_dataDirectoryOverride))
        {
            return _dataDirectoryOverride;
        }

        var appDataDir = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);
        return Path.Combine(appDataDir, "Zeus");
    }

    private string GetOrCreateDatabasePassword()
    {
        var zeusDir = GetZeusDir();
        var keyPath = Path.Combine(zeusDir, ".dbkey");

        if (File.Exists(keyPath))
        {
            try
            {
                return File.ReadAllText(keyPath);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to read existing database key; generating new one");
            }
        }

        // Generate a new random key
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        try
        {
            if (!Directory.Exists(zeusDir))
            {
                Directory.CreateDirectory(zeusDir);
            }

            File.WriteAllText(keyPath, key);

            // Set file permissions to 0600 on Unix-like systems
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Failed to set Unix file permissions on database key");
                }
            }

            _log.LogInformation("Created new database key at {Path}", keyPath);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to persist database key; using ephemeral key");
        }

        return key;
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
