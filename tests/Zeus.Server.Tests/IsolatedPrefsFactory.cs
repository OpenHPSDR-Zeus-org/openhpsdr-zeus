// SPDX-License-Identifier: GPL-2.0-or-later
//
// Shared test-only base factory that isolates each endpoint test's prefs DB.

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Zeus.Server.Tests;

// Every endpoint test gets a fresh, unique zeus-prefs.db so persisted board/
// variant/override state cannot bleed across tests. The suite is serial
// (AssemblyAttributes.cs), so mutating the process-global ZEUS_PREFS_PATH here
// is safe: each `using var factory` is built and disposed within one test.
//
// The host is NOT built in the ctor — it builds lazily on the first
// CreateClient()/Services access, at which point the stores read
// PrefsDbPath.Get() and pick up the unique path set below. On Dispose the
// previous value is restored and the temp DB (plus its -log sidecar) deleted.
public abstract class IsolatedPrefsFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"zeus-prefs-test-{Guid.NewGuid():N}.db");
    private readonly string? _previousPrefsPath;

    protected IsolatedPrefsFactory()
    {
        _previousPrefsPath = Environment.GetEnvironmentVariable("ZEUS_PREFS_PATH");
        Environment.SetEnvironmentVariable("ZEUS_PREFS_PATH", _dbPath);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");
        builder.ConfigureServices(services => services.RemoveAll<IHostedService>());
        ConfigureExtra(builder); // hook for factories that add/replace services
    }

    // Override in factories that need extra service wiring (AGC/MicGain/Ps/etc.).
    protected virtual void ConfigureExtra(IWebHostBuilder builder) { }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Environment.SetEnvironmentVariable("ZEUS_PREFS_PATH", _previousPrefsPath);
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (File.Exists(_dbPath + "-log")) File.Delete(_dbPath + "-log"); } catch { }
    }
}
