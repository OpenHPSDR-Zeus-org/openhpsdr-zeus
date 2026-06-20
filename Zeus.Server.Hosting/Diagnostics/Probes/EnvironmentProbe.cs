// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Zeus.Server.Diagnostics;

/// <summary>
/// Host-environment snapshot: Zeus version, OS / architecture / .NET runtime,
/// and process uptime. Needs no backend services, so it is always available
/// even in unit tests with an empty service provider.
/// </summary>
public sealed class EnvironmentProbe : IDiagnosticProbe
{
    public string Id => "environment";

    public DiagnosticSection Collect(DiagnosticContext ctx)
    {
        var items = new List<DiagnosticKeyValue>();
        try
        {
            items.Add(new("zeus.version", ZeusVersion()));
            items.Add(new("os.description", RuntimeInformation.OSDescription));
            items.Add(new("os.architecture", RuntimeInformation.OSArchitecture.ToString()));
            items.Add(new("process.architecture", RuntimeInformation.ProcessArchitecture.ToString()));
            items.Add(new("dotnet.version", RuntimeInformation.FrameworkDescription));

            try
            {
                // Process uptime is cheap to read; guard anyway in case the
                // platform restricts access to the current process metadata.
                var uptime = DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime();
                if (uptime < TimeSpan.Zero)
                    uptime = TimeSpan.Zero;
                items.Add(new("process.uptime", uptime.ToString(@"d\.hh\:mm\:ss")));
            }
            catch (Exception ex)
            {
                items.Add(new("process.uptime", $"unavailable ({ex.GetType().Name})"));
            }
        }
        catch (Exception ex)
        {
            items.Add(new("status", "error"));
            items.Add(new("error", ex.GetType().Name));
        }

        return new DiagnosticSection(Id, "Environment", items);
    }

    private static string ZeusVersion()
    {
        // Prefer the hosting assembly's informational version (the SemVer-ish
        // string baked in by Directory.Build.props); fall back gracefully.
        var asm = typeof(EnvironmentProbe).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
            return info!;
        return asm.GetName().Version?.ToString() ?? "unknown";
    }
}
