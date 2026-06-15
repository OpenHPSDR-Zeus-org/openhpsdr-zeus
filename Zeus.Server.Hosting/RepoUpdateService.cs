// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// RepoUpdateService — reports whether the running checkout is behind its
// configured git upstream and performs a fast-forward pull on request. It is a
// thin git-CLI wrapper (no new dependencies): every operation shells out to the
// `git` already on PATH, scoped to the repo root with `git -C <root>`.
//
// This drives Settings -> Updates (/api/system/update). It deliberately does
// NOT rebuild or restart — the running binaries are stale after a pull, so the
// UI tells the operator to run scripts/update.* and restart. If the app is not
// running from a git checkout (a packaged build) every call reports
// IsGitRepo=false and the UI shows manual-update guidance instead.

using System.Diagnostics;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>Git-checkout update helper backing the Settings -> Updates panel.
/// Registered as a singleton in <c>ZeusHost</c>.</summary>
public sealed class RepoUpdateService
{
    private readonly ILogger<RepoUpdateService> _log;
    private readonly string? _repoRoot;

    public RepoUpdateService(ILogger<RepoUpdateService> log)
    {
        _log = log;
        _repoRoot = ResolveRepoRoot();
        if (_repoRoot is null)
            _log.LogInformation("RepoUpdateService: not running from a git checkout; update checks disabled");
        else
            _log.LogInformation("RepoUpdateService: repo root {Root}", _repoRoot);
    }

    /// <summary>Compute the checkout's status versus its upstream. When
    /// <paramref name="fetch"/> is true the upstream is fetched first (network);
    /// a failed fetch is non-fatal and the last-known counts are returned with
    /// an <see cref="RepoUpdateStatus.Error"/> note.</summary>
    public async Task<RepoUpdateStatus> GetStatusAsync(bool fetch, CancellationToken ct)
    {
        if (_repoRoot is null)
            return NotAGitRepo();

        try
        {
            var branch = NullIfEmpty((await GitAsync(ct, 10_000, "rev-parse", "--abbrev-ref", "HEAD")).Out);

            var upstream = await GitAsync(ct, 10_000, "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}");
            var upstreamRef = upstream.Ok ? NullIfEmpty(upstream.Out) : null;
            var remote = upstreamRef is { } u && u.Contains('/') ? u[..u.IndexOf('/')] : null;

            // Surface an explicit note when there's no upstream (detached HEAD or a
            // branch with no tracking ref) so the UI doesn't fall through to a
            // misleading "up to date" with behind=0.
            string? note = upstreamRef is null
                ? "No upstream branch configured — set one with: git branch --set-upstream-to=<remote>/<branch>"
                : null;
            if (fetch && remote is not null)
            {
                var f = await GitAsync(ct, 30_000, "fetch", "--quiet", remote);
                if (!f.Ok)
                {
                    note = $"fetch failed (offline?): {Trunc(PreferErr(f))}";
                    _log.LogDebug("git fetch failed: {Err}", PreferErr(f));
                }
            }

            var currentSha = NullIfEmpty((await GitAsync(ct, 10_000, "rev-parse", "HEAD")).Out);
            var shortSha = NullIfEmpty((await GitAsync(ct, 10_000, "rev-parse", "--short", "HEAD")).Out);
            var subject = NullIfEmpty((await GitAsync(ct, 10_000, "log", "-1", "--format=%s")).Out);
            var dirty = !string.IsNullOrWhiteSpace((await GitAsync(ct, 10_000, "status", "--porcelain")).Out);

            var behind = 0;
            var ahead = 0;
            string? latestSha = null;
            string? latestSubject = null;
            var canFf = false;

            if (upstreamRef is not null)
            {
                behind = ParseInt((await GitAsync(ct, 10_000, "rev-list", "--count", "HEAD..@{u}")).Out);
                ahead = ParseInt((await GitAsync(ct, 10_000, "rev-list", "--count", "@{u}..HEAD")).Out);
                latestSha = NullIfEmpty((await GitAsync(ct, 10_000, "rev-parse", "--short", "@{u}")).Out);
                latestSubject = NullIfEmpty((await GitAsync(ct, 10_000, "log", "-1", "--format=%s", "@{u}")).Out);
                var ancestor = await GitAsync(ct, 10_000, "merge-base", "--is-ancestor", "HEAD", "@{u}");
                canFf = behind > 0 && ancestor.Ok && !dirty;
            }

            string? remoteUrl = remote is not null
                ? NullIfEmpty((await GitAsync(ct, 10_000, "remote", "get-url", remote)).Out)
                : null;

            return new RepoUpdateStatus(
                IsGitRepo: true,
                Branch: branch,
                CurrentSha: currentSha,
                CurrentShortSha: shortSha,
                CurrentSubject: subject,
                UpstreamRef: upstreamRef,
                Behind: behind,
                Ahead: ahead,
                Dirty: dirty,
                CanFastForward: canFf,
                LatestRemoteSha: latestSha,
                LatestRemoteSubject: latestSubject,
                RemoteUrl: remoteUrl,
                CheckedUtc: DateTime.UtcNow.ToString("o"),
                Error: note);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "repo update status failed");
            return new RepoUpdateStatus(
                IsGitRepo: true, Branch: null, CurrentSha: null, CurrentShortSha: null,
                CurrentSubject: null, UpstreamRef: null, Behind: 0, Ahead: 0, Dirty: false,
                CanFastForward: false, LatestRemoteSha: null, LatestRemoteSubject: null,
                RemoteUrl: null, CheckedUtc: DateTime.UtcNow.ToString("o"),
                Error: GitMissing(ex) ? "git is not installed or not on PATH" : ex.Message);
        }
    }

    /// <summary>Fast-forward the checkout to its upstream. Refuses when the
    /// working tree is dirty (a ff would otherwise be rejected or clobber edits).
    /// Never rebuilds/restarts — the caller surfaces RequiresRebuild.</summary>
    public async Task<RepoUpdateResult> PullAsync(CancellationToken ct)
    {
        if (_repoRoot is null)
            return new RepoUpdateResult(false, null, false, "Not running from a git checkout — update manually.");

        try
        {
            var dirty = !string.IsNullOrWhiteSpace((await GitAsync(ct, 10_000, "status", "--porcelain")).Out);
            if (dirty)
                return new RepoUpdateResult(false, null, false,
                    "Working tree has uncommitted changes — commit or stash them before updating.");

            var before = NullIfEmpty((await GitAsync(ct, 10_000, "rev-parse", "--short", "HEAD")).Out);
            var pull = await GitAsync(ct, 60_000, "pull", "--ff-only");
            var after = NullIfEmpty((await GitAsync(ct, 10_000, "rev-parse", "--short", "HEAD")).Out);

            if (!pull.Ok)
                return new RepoUpdateResult(false, after, false, $"git pull failed: {Trunc(PreferErr(pull))}");

            var changed = before is not null && after is not null && before != after;
            var msg = changed
                ? $"Updated {before} -> {after}. Rebuild and restart Zeus to apply (scripts/update)."
                : "Already up to date.";
            return new RepoUpdateResult(true, after, changed, msg);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "repo update pull failed");
            return new RepoUpdateResult(false, null, false,
                GitMissing(ex) ? "git is not installed or not on PATH" : ex.Message);
        }
    }

    // ----- git process helper -----

    private async Task<(bool Ok, string Out, string Err)> GitAsync(
        CancellationToken ct, int timeoutMs, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _repoRoot!,
        };
        // Scope every invocation to the repo root so the CWD (the exe's bin dir)
        // doesn't matter, and quoting of a path with spaces is handled by
        // ArgumentList rather than manual escaping.
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(_repoRoot!);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        using var p = new Process { StartInfo = psi };
        p.Start();
        // Read both pipes concurrently (avoids the classic full-buffer deadlock)
        // and bind them to the timeout token so a process that exits but never
        // closes its pipes can't hang the read past the timeout.
        var outTask = p.StandardOutput.ReadToEndAsync(cts.Token);
        var errTask = p.StandardError.ReadToEndAsync(cts.Token);

        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
            // Observe the abandoned reads so they don't surface as unobserved
            // task exceptions once the killed process's pipes close.
            try { await Task.WhenAll(outTask, errTask).ConfigureAwait(false); } catch { /* ignore */ }
            return (false, string.Empty, "git timed out");
        }

        var stdout = (await outTask.ConfigureAwait(false)).Trim();
        var stderr = (await errTask.ConfigureAwait(false)).Trim();
        return (p.ExitCode == 0, stdout, stderr);
    }

    // ----- helpers -----

    private static RepoUpdateStatus NotAGitRepo() => new(
        IsGitRepo: false, Branch: null, CurrentSha: null, CurrentShortSha: null,
        CurrentSubject: null, UpstreamRef: null, Behind: 0, Ahead: 0, Dirty: false,
        CanFastForward: false, LatestRemoteSha: null, LatestRemoteSubject: null,
        RemoteUrl: null, CheckedUtc: null, Error: "Not running from a git checkout.");

    private static string PreferErr((bool Ok, string Out, string Err) r)
        => !string.IsNullOrWhiteSpace(r.Err) ? r.Err : r.Out;

    private static bool GitMissing(Exception ex)
        => ex is System.ComponentModel.Win32Exception;

    private static int ParseInt(string s)
        => int.TryParse(s.Trim(), out var n) ? n : 0;

    private static string? NullIfEmpty(string s)
        => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string Trunc(string s, int max = 300)
        => s.Length <= max ? s : s[..max] + "…";

    /// <summary>Walk up from the running binary's directory until a `.git` entry
    /// is found (directory for a normal clone, file for a worktree/submodule).</summary>
    private static string? ResolveRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var gitPath = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
