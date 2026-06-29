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

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class CredentialStoreTests : IDisposable
{
    private readonly string _testDbDir;
    private readonly string _testDbPath;
    private readonly CredentialStore _store;

    public CredentialStoreTests()
    {
        // Per-fixture isolated DB file so parallel tests can't collide on the
        // real %LOCALAPPDATA%\Zeus\zeus-prefs.db. The store now takes an explicit
        // DB-file override (credentials live in the plaintext config DB).
        _testDbDir = Path.Combine(Path.GetTempPath(), $"zeus-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDbDir);
        _testDbPath = Path.Combine(_testDbDir, "zeus-prefs.db");

        _store = new CredentialStore(NullLogger<CredentialStore>.Instance, _testDbPath);
    }

    public void Dispose()
    {
        _store.Dispose();

        // Clean up test directory
        if (Directory.Exists(_testDbDir))
        {
            try
            {
                Directory.Delete(_testDbDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Fact]
    public async Task SetAsync_StoresCredentials()
    {
        // Arrange
        const string service = "test-service";
        const string username = "testuser";
        const string password = "testpass123";

        // Act
        await _store.SetAsync(service, username, password);

        // Assert
        var retrieved = await _store.GetAsync(service);
        Assert.NotNull(retrieved);
        Assert.Equal(service, retrieved.Service);
        Assert.Equal(username, retrieved.Username);
        Assert.Equal(password, retrieved.Password);
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenServiceNotFound()
    {
        // Act
        var result = await _store.GetAsync("nonexistent");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task DeleteAsync_RemovesCredentials()
    {
        // Arrange
        const string service = "test-service";
        await _store.SetAsync(service, "user", "pass");

        // Act
        await _store.DeleteAsync(service);

        // Assert
        var result = await _store.GetAsync(service);
        Assert.Null(result);
    }

    [Fact]
    public async Task SetAsync_UpdatesExistingCredentials()
    {
        // Arrange
        const string service = "test-service";
        await _store.SetAsync(service, "olduser", "oldpass");

        // Act
        await _store.SetAsync(service, "newuser", "newpass");

        // Assert
        var retrieved = await _store.GetAsync(service);
        Assert.NotNull(retrieved);
        Assert.Equal("newuser", retrieved.Username);
        Assert.Equal("newpass", retrieved.Password);
    }

    [Fact]
    public async Task Credentials_PersistAcrossReopen()
    {
        // Credentials now live in the plaintext config DB (zeus-prefs.db); the
        // encrypted zeus.db is retired. This guards the round-trip: a credential
        // written by one store instance is readable by a fresh one against the
        // same file.
        const string service = "persist-test";
        const string password = "SENSITIVE_PASSWORD_12345";

        await _store.SetAsync(service, "user", password);
        _store.Dispose();

        Assert.True(File.Exists(_testDbPath));

        using var store2 = new CredentialStore(NullLogger<CredentialStore>.Instance, _testDbPath);
        var retrieved = await store2.GetAsync(service);
        Assert.NotNull(retrieved);
        Assert.Equal(password, retrieved.Password);
    }
}
