// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Zeus.Server;

public interface IWindowsFirewallService
{
    WindowsFirewallStatus GetStatus();
    Task<WindowsFirewallApplyResult> ApplyAllowRuleAsync(CancellationToken ct = default);
}

public sealed record WindowsFirewallStatus(
    bool Supported,
    bool CanApply,
    string RuleName,
    string? ProgramPath,
    string Message);

public sealed record WindowsFirewallApplyResult(
    bool Supported,
    bool Applied,
    bool ElevationAttempted,
    bool ElevationCanceled,
    string RuleName,
    string? ProgramPath,
    string Message);

public sealed class WindowsFirewallService : IWindowsFirewallService
{
    public const string RuleName = "OpenHPSDR Zeus (HPSDR receive)";

    private readonly ILogger<WindowsFirewallService> _log;
    private readonly IWindowsFirewallCommandRunner _runner;
    private readonly Func<bool> _isWindows;
    private readonly Func<string?> _processPath;

    public WindowsFirewallService(ILogger<WindowsFirewallService> log)
        : this(
            log,
            new ProcessWindowsFirewallCommandRunner(),
            OperatingSystem.IsWindows,
            () => Environment.ProcessPath)
    {
    }

    internal WindowsFirewallService(
        ILogger<WindowsFirewallService> log,
        IWindowsFirewallCommandRunner runner,
        Func<bool> isWindows,
        Func<string?> processPath)
    {
        _log = log;
        _runner = runner;
        _isWindows = isWindows;
        _processPath = processPath;
    }

    public WindowsFirewallStatus GetStatus()
    {
        if (!_isWindows())
        {
            return new(
                Supported: false,
                CanApply: false,
                RuleName,
                ProgramPath: null,
                Message: "Windows Firewall rule management is only available on Windows.");
        }

        var programPath = ResolveProgramPath();
        if (string.IsNullOrWhiteSpace(programPath))
        {
            return new(
                Supported: true,
                CanApply: false,
                RuleName,
                ProgramPath: null,
                Message: "Could not resolve the Zeus executable path.");
        }

        return new(
            Supported: true,
            CanApply: true,
            RuleName,
            ProgramPath: programPath,
            Message: "Ready to add the Zeus inbound allow rule.");
    }

    public async Task<WindowsFirewallApplyResult> ApplyAllowRuleAsync(CancellationToken ct = default)
    {
        var status = GetStatus();
        if (!status.Supported || !status.CanApply || string.IsNullOrWhiteSpace(status.ProgramPath))
        {
            return new(
                Supported: status.Supported,
                Applied: false,
                ElevationAttempted: false,
                ElevationCanceled: false,
                RuleName,
                ProgramPath: status.ProgramPath,
                Message: status.Message);
        }

        var direct = await _runner.ApplyAsync(RuleName, status.ProgramPath, elevated: false, ct);
        if (direct.ExitCode == 0)
        {
            _log.LogInformation("windows.firewall.rule applied path={ProgramPath} elevated=false", status.ProgramPath);
            return new(
                Supported: true,
                Applied: true,
                ElevationAttempted: false,
                ElevationCanceled: false,
                RuleName,
                ProgramPath: status.ProgramPath,
                Message: "Windows Firewall rule applied.");
        }

        _log.LogInformation(
            "windows.firewall.rule direct apply failed exit={ExitCode}; requesting elevation",
            direct.ExitCode);

        var elevated = await _runner.ApplyAsync(RuleName, status.ProgramPath, elevated: true, ct);
        if (elevated.ExitCode == 0)
        {
            _log.LogInformation("windows.firewall.rule applied path={ProgramPath} elevated=true", status.ProgramPath);
            return new(
                Supported: true,
                Applied: true,
                ElevationAttempted: true,
                ElevationCanceled: false,
                RuleName,
                ProgramPath: status.ProgramPath,
                Message: "Windows Firewall rule applied.");
        }

        if (elevated.Canceled)
        {
            return new(
                Supported: true,
                Applied: false,
                ElevationAttempted: true,
                ElevationCanceled: true,
                RuleName,
                ProgramPath: status.ProgramPath,
                Message: "Windows administrator approval was cancelled.");
        }

        _log.LogWarning(
            "windows.firewall.rule elevated apply failed exit={ExitCode} output={Output}",
            elevated.ExitCode,
            elevated.Output);
        return new(
            Supported: true,
            Applied: false,
            ElevationAttempted: true,
            ElevationCanceled: false,
            RuleName,
            ProgramPath: status.ProgramPath,
            Message: "Windows did not accept the firewall rule. Try running Zeus as administrator, then apply it again.");
    }

    private string? ResolveProgramPath()
    {
        var path = _processPath()?.Trim();
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }
}

internal interface IWindowsFirewallCommandRunner
{
    Task<FirewallCommandResult> ApplyAsync(
        string ruleName,
        string programPath,
        bool elevated,
        CancellationToken ct);
}

internal sealed record FirewallCommandResult(int ExitCode, string Output, bool Canceled = false);

internal sealed class ProcessWindowsFirewallCommandRunner : IWindowsFirewallCommandRunner
{
    private const int ErrorCancelled = 1223;

    public async Task<FirewallCommandResult> ApplyAsync(
        string ruleName,
        string programPath,
        bool elevated,
        CancellationToken ct)
    {
        return elevated
            ? await RunElevatedAsync(ruleName, programPath, ct)
            : await RunDirectAsync(ruleName, programPath, ct);
    }

    private static async Task<FirewallCommandResult> RunDirectAsync(
        string ruleName,
        string programPath,
        CancellationToken ct)
    {
        var delete = await RunNetshAsync(
            [
                "advfirewall",
                "firewall",
                "delete",
                "rule",
                "name=" + ruleName,
            ],
            ct);

        var add = await RunNetshAsync(
            [
                "advfirewall",
                "firewall",
                "add",
                "rule",
                "name=" + ruleName,
                "dir=in",
                "action=allow",
                "program=" + programPath,
                "enable=yes",
            ],
            ct);

        var output = string.Join(
            Environment.NewLine,
            new[] { delete.Output, add.Output }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return add with { Output = output };
    }

    private static async Task<FirewallCommandResult> RunElevatedAsync(
        string ruleName,
        string programPath,
        CancellationToken ct)
    {
        var script = BuildElevatedPowerShellScript(ruleName, programPath);
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo(ResolveTool("powershell.exe"))
        {
            Arguments = "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + encoded,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return new(-1, "Failed to start elevated PowerShell.");

            await process.WaitForExitAsync(ct);
            return new(process.ExitCode, "");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new(ErrorCancelled, ex.Message, Canceled: true);
        }
    }

    internal static string BuildElevatedPowerShellScript(string ruleName, string programPath)
    {
        var netsh = ResolveTool("netsh.exe");
        return string.Join(
            Environment.NewLine,
            [
                "$ErrorActionPreference = 'SilentlyContinue'",
                "$netsh = " + PowerShellLiteral(netsh),
                "$rule = " + PowerShellLiteral(ruleName),
                "$program = " + PowerShellLiteral(programPath),
                "& $netsh advfirewall firewall delete rule (\"name=\" + $rule) | Out-Null",
                "$ErrorActionPreference = 'Stop'",
                "& $netsh advfirewall firewall add rule (\"name=\" + $rule) dir=in action=allow (\"program=\" + $program) enable=yes",
                "exit $LASTEXITCODE",
            ]);
    }

    private static async Task<FirewallCommandResult> RunNetshAsync(
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ResolveTool("netsh.exe"))
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) output.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) output.AppendLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(ct);
        return new(process.ExitCode, output.ToString().Trim());
    }

    private static string ResolveTool(string fileName)
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!string.IsNullOrWhiteSpace(system))
        {
            var candidate = Path.Combine(system, fileName);
            if (File.Exists(candidate)) return candidate;
        }

        return fileName;
    }

    private static string PowerShellLiteral(string value) => "'" + value.Replace("'", "''") + "'";
}
