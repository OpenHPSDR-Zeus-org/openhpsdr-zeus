// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Zeus.Server.Tests;

public sealed class WindowsFirewallServiceTests
{
    [Fact]
    public void GetStatus_NonWindows_ReportsUnsupported()
    {
        var runner = new FakeRunner();
        var svc = new WindowsFirewallService(
            NullLogger<WindowsFirewallService>.Instance,
            runner,
            isWindows: () => false,
            processPath: () => "C:\\Zeus\\OpenhpsdrZeus.exe");

        var status = svc.GetStatus();

        Assert.False(status.Supported);
        Assert.False(status.CanApply);
        Assert.Null(status.ProgramPath);
        Assert.Contains("Windows", status.Message);
    }

    [Fact]
    public async Task ApplyAllowRule_DirectSuccess_DoesNotRequestElevation()
    {
        var runner = new FakeRunner { DirectExitCode = 0 };
        var svc = NewWindowsService(runner);

        var result = await svc.ApplyAllowRuleAsync();

        Assert.True(result.Applied);
        Assert.False(result.ElevationAttempted);
        Assert.Equal(1, runner.DirectCalls);
        Assert.Equal(0, runner.ElevatedCalls);
        Assert.Equal("C:\\Zeus\\OpenhpsdrZeus.exe", runner.LastProgramPath);
        Assert.Equal(WindowsFirewallService.RuleName, runner.LastRuleName);
    }

    [Fact]
    public async Task ApplyAllowRule_DirectFailure_FallsBackToElevation()
    {
        var runner = new FakeRunner { DirectExitCode = 5, ElevatedExitCode = 0 };
        var svc = NewWindowsService(runner);

        var result = await svc.ApplyAllowRuleAsync();

        Assert.True(result.Applied);
        Assert.True(result.ElevationAttempted);
        Assert.False(result.ElevationCanceled);
        Assert.Equal(1, runner.DirectCalls);
        Assert.Equal(1, runner.ElevatedCalls);
    }

    [Fact]
    public async Task ApplyAllowRule_CanceledElevation_ReportsCancellation()
    {
        var runner = new FakeRunner
        {
            DirectExitCode = 5,
            ElevatedExitCode = 1223,
            ElevatedCanceled = true,
        };
        var svc = NewWindowsService(runner);

        var result = await svc.ApplyAllowRuleAsync();

        Assert.False(result.Applied);
        Assert.True(result.ElevationAttempted);
        Assert.True(result.ElevationCanceled);
        Assert.Contains("cancel", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ElevatedScript_UsesAdvFirewallProgramAllowRule()
    {
        var script = ProcessWindowsFirewallCommandRunner.BuildElevatedPowerShellScript(
            "Rule 'Name'",
            "C:\\Program Files\\Zeus\\OpenhpsdrZeus.exe");

        Assert.Contains("advfirewall firewall add rule", script);
        Assert.Contains("dir=in", script);
        Assert.Contains("action=allow", script);
        Assert.Contains("enable=yes", script);
        Assert.Contains("'Rule ''Name'''", script);
        Assert.Contains("'C:\\Program Files\\Zeus\\OpenhpsdrZeus.exe'", script);
    }

    private static WindowsFirewallService NewWindowsService(FakeRunner runner) =>
        new(
            NullLogger<WindowsFirewallService>.Instance,
            runner,
            isWindows: () => true,
            processPath: () => "C:\\Zeus\\OpenhpsdrZeus.exe");

    private sealed class FakeRunner : IWindowsFirewallCommandRunner
    {
        public int DirectExitCode { get; init; }
        public int ElevatedExitCode { get; init; }
        public bool ElevatedCanceled { get; init; }
        public int DirectCalls { get; private set; }
        public int ElevatedCalls { get; private set; }
        public string? LastRuleName { get; private set; }
        public string? LastProgramPath { get; private set; }

        public Task<FirewallCommandResult> ApplyAsync(
            string ruleName,
            string programPath,
            bool elevated,
            CancellationToken ct)
        {
            LastRuleName = ruleName;
            LastProgramPath = programPath;
            if (elevated)
            {
                ElevatedCalls++;
                return Task.FromResult(new FirewallCommandResult(
                    ElevatedExitCode,
                    "",
                    Canceled: ElevatedCanceled));
            }

            DirectCalls++;
            return Task.FromResult(new FirewallCommandResult(DirectExitCode, ""));
        }
    }
}

public sealed class WindowsFirewallEndpointTests
    : IClassFixture<WindowsFirewallEndpointTests.Factory>
{
    private readonly Factory _factory;
    public WindowsFirewallEndpointTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task StatusEndpointReportsLocalApplyAffordance()
    {
        using var client = _factory.CreateClient();

        using var json = await client.GetFromJsonAsync<JsonDocument>("/api/system/windows-firewall");

        Assert.NotNull(json);
        var root = json!.RootElement;
        Assert.True(root.GetProperty("supported").GetBoolean());
        Assert.True(root.GetProperty("canApply").GetBoolean());
        Assert.True(root.GetProperty("localRequest").GetBoolean());
        Assert.Equal(WindowsFirewallService.RuleName, root.GetProperty("ruleName").GetString());
    }

    [Fact]
    public async Task ApplyEndpointDelegatesToFirewallService()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsync("/api/system/windows-firewall/allow", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await response.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.NotNull(json);
        Assert.True(json!.RootElement.GetProperty("applied").GetBoolean());
        Assert.True(_factory.Firewall.ApplyCalled);
    }

    public sealed class Factory : IsolatedPrefsFactory
    {
        public FakeFirewallService Firewall { get; } = new();

        protected override void ConfigureExtra(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IWindowsFirewallService>();
                services.AddSingleton<IWindowsFirewallService>(Firewall);
            });
        }
    }

    public sealed class FakeFirewallService : IWindowsFirewallService
    {
        public bool ApplyCalled { get; private set; }

        public WindowsFirewallStatus GetStatus() => new(
            Supported: true,
            CanApply: true,
            RuleName: WindowsFirewallService.RuleName,
            ProgramPath: "C:\\Zeus\\OpenhpsdrZeus.exe",
            Message: "Ready");

        public Task<WindowsFirewallApplyResult> ApplyAllowRuleAsync(CancellationToken ct = default)
        {
            ApplyCalled = true;
            return Task.FromResult(new WindowsFirewallApplyResult(
                Supported: true,
                Applied: true,
                ElevationAttempted: false,
                ElevationCanceled: false,
                RuleName: WindowsFirewallService.RuleName,
                ProgramPath: "C:\\Zeus\\OpenhpsdrZeus.exe",
                Message: "Windows Firewall rule applied."));
        }
    }
}
