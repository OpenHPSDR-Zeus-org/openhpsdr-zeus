// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Zeus.Server.Uninstall;

// Launches the detached post-exit uninstall helper. The whole safety story of
// this file is: PATHS ARE DATA, NEVER CODE.
//
//  * The validated absolute paths are written to a temp manifest file (UTF-8,
//    NUL-delimited) that lives in the OS temp dir — a location the wipe never
//    touches — and are passed to the helper as the CONTENTS of that file.
//  * Dynamic values (parent PID, manifest path, marker path, registry keys,
//    the optional Windows uninstaller command) are passed through ENVIRONMENT
//    variables, never concatenated into the script text.
//  * The script bodies (PosixScript / WindowsScript) are FIXED CONSTANTS with no
//    path interpolation, so a folder name containing $(...), backticks, quotes,
//    or spaces can neither widen an rm -rf nor execute code (the defect the
//    audit flagged in the reused AppRestartService shell template).
//
// The helper waits (bounded) for the parent PID to die, RE-CHECKS liveness and
// ABORTS if the parent is still alive (so a stuck shutdown can never delete a
// still-locked DB), then deletes each path, removes the Windows WER registry
// key, optionally invokes the installer's own uninstaller, and writes a result
// marker. Survives the parent's Environment.Exit (reparented to init/services),
// exactly like the relaunch helper it is modelled on.
public sealed class UninstallExecutor
{
    public sealed record Plan(ProcessStartInfo StartInfo, string ManifestPath, byte[] ManifestBytes, string MarkerPath);

    // Env var names the fixed scripts read.
    internal const string EnvPid = "ZEUS_UN_PID";
    internal const string EnvManifest = "ZEUS_UN_MANIFEST";
    internal const string EnvMarker = "ZEUS_UN_MARKER";
    internal const string EnvDryRun = "ZEUS_UN_DRYRUN";
    internal const string EnvRegKeys = "ZEUS_UN_REGKEYS";
    internal const string EnvUninstaller = "ZEUS_UN_UNINSTALLER";

    // Build the launch plan WITHOUT touching the disk or starting anything, so it
    // can be unit-tested (assert the script is the fixed constant, no path leaks
    // into argv, paths only appear in the manifest bytes).
    public static Plan BuildPlan(UninstallManifest manifest, int parentPid, bool dryRun, string? tempDir = null)
    {
        if (!manifest.Ok)
            throw new InvalidOperationException("Refusing to build an uninstall plan from an aborted manifest.");

        tempDir ??= Path.GetTempPath();
        var stamp = Guid.NewGuid().ToString("N");
        var manifestPath = Path.Combine(tempDir, $"zeus-uninstall-{stamp}.lst");
        var markerPath = Path.Combine(tempDir, $"zeus-uninstall-{stamp}.log");

        // NUL-delimited absolute paths (file & dir alike — rm -rf / Remove-Item
        // -Recurse handle both). NUL can't appear in a path, so it's an unambiguous
        // separator immune to newlines/spaces in names.
        var sb = new StringBuilder();
        foreach (var p in manifest.Paths)
        {
            sb.Append(p.Path);
            sb.Append('\0');
        }
        var manifestBytes = Encoding.UTF8.GetBytes(sb.ToString());

        var regKeys = string.Join('\0', manifest.RegistryKeys.Select(k => $"{k.Hive}\\{k.SubKey}"));

        ProcessStartInfo psi;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            psi = new ProcessStartInfo("powershell.exe")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(WindowsScript);
            psi.Environment[EnvRegKeys] = regKeys;
            psi.Environment[EnvUninstaller] = manifest.WindowsUninstallerCommand ?? "";
        }
        else
        {
            psi = new ProcessStartInfo("/bin/sh")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(PosixScript);
        }

        psi.Environment[EnvPid] = parentPid.ToString();
        psi.Environment[EnvManifest] = manifestPath;
        psi.Environment[EnvMarker] = markerPath;
        psi.Environment[EnvDryRun] = dryRun ? "1" : "0";

        return new Plan(psi, manifestPath, manifestBytes, markerPath);
    }

    // Write the manifest file and launch the detached helper. Returns the marker
    // path so a dry-run/test can read the result.
    public static string Launch(UninstallManifest manifest, bool dryRun = false)
    {
        var plan = BuildPlan(manifest, Environment.ProcessId, dryRun);
        File.WriteAllBytes(plan.ManifestPath, plan.ManifestBytes);
        Process.Start(plan.StartInfo);
        return plan.MarkerPath;
    }

    // FIXED POSIX script. References only env vars we set + reads paths as data
    // from the manifest file. No path is ever interpolated into this text.
    internal const string PosixScript = """
        i=0
        while kill -0 "$ZEUS_UN_PID" 2>/dev/null; do
          i=$((i+1)); if [ "$i" -ge 240 ]; then break; fi; sleep 0.5
        done
        if kill -0 "$ZEUS_UN_PID" 2>/dev/null; then
          printf 'ABORT parent still alive\n' > "$ZEUS_UN_MARKER"; exit 1
        fi
        sleep 1
        while IFS= read -r -d '' p; do
          [ -n "$p" ] || continue
          if [ "$ZEUS_UN_DRYRUN" = "1" ]; then
            printf 'WOULD-DELETE %s\n' "$p" >> "$ZEUS_UN_MARKER"
          else
            rm -rf -- "$p" 2>/dev/null
          fi
        done < "$ZEUS_UN_MANIFEST"
        printf 'DONE\n' >> "$ZEUS_UN_MARKER"
        rm -f -- "$ZEUS_UN_MANIFEST" 2>/dev/null
        """;

    // FIXED Windows (PowerShell) script. NOTE: never use the automatic $pid
    // variable here — that's the helper's own PID; the parent PID is in env.
    internal const string WindowsScript = """
        $ErrorActionPreference='SilentlyContinue'
        $ppid=[int]$env:ZEUS_UN_PID
        for($i=0;$i -lt 240;$i++){ if(-not (Get-Process -Id $ppid -ErrorAction SilentlyContinue)){break}; Start-Sleep -Milliseconds 500 }
        if(Get-Process -Id $ppid -ErrorAction SilentlyContinue){ 'ABORT parent still alive' | Out-File -LiteralPath $env:ZEUS_UN_MARKER -Encoding UTF8; exit 1 }
        Start-Sleep -Seconds 1
        $dry = ($env:ZEUS_UN_DRYRUN -eq '1')
        $txt=[System.IO.File]::ReadAllText($env:ZEUS_UN_MANIFEST,[System.Text.Encoding]::UTF8)
        foreach($p in ($txt -split [char]0)){
          if([string]::IsNullOrWhiteSpace($p)){continue}
          if($dry){ "WOULD-DELETE $p" | Out-File -LiteralPath $env:ZEUS_UN_MARKER -Append -Encoding UTF8; continue }
          try{
            $it=Get-Item -LiteralPath $p -Force -ErrorAction Stop
            if($it.Attributes -band [System.IO.FileAttributes]::ReparsePoint){
              if($it.PSIsContainer){ & cmd.exe /c rmdir (Convert-Path -LiteralPath $p) | Out-Null } else { Remove-Item -LiteralPath $p -Force }
            } else {
              Remove-Item -LiteralPath $p -Recurse -Force
            }
          }catch{}
        }
        if(-not $dry){
          foreach($k in ($env:ZEUS_UN_REGKEYS -split [char]0)){ if($k){ & reg.exe delete $k /f | Out-Null } }
          if($env:ZEUS_UN_UNINSTALLER){ try{ Start-Process -FilePath cmd.exe -ArgumentList '/c',$env:ZEUS_UN_UNINSTALLER -Wait }catch{} }
        }
        'DONE' | Out-File -LiteralPath $env:ZEUS_UN_MARKER -Append -Encoding UTF8
        Remove-Item -LiteralPath $env:ZEUS_UN_MANIFEST -Force
        """;
}
