// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see ATTRIBUTIONS.md for provenance.

using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Zeus.Server.Voyeur;

/// <summary>
/// Local LLM digest for Voyeur Mode (zeus-la5 Phase 3B). Summarizes a session's
/// transcript into a "what was discussed" paragraph using llama.cpp, run as a
/// SEPARATE, SUPERVISED OS CHILD PROCESS — never P/Invoked — for exactly the
/// same crash-isolation reason as <see cref="WhisperTranscriber"/>: a native
/// fault must kill only the child, never the Zeus host. Everything runs OFF the
/// audio/DSP path (it reads stored transcript text), and the digest is
/// on-demand only, so it never adds steady load.
///
/// Local-only by design — the transcript never leaves the machine, $0, no API
/// key. Graceful absence: no binary/model ⇒ digest is simply unavailable and
/// the panel offers to download the model. The one-shot tool is
/// <c>llama-completion</c> (modern llama.cpp; <c>llama-cli</c> is the
/// interactive fallback). Discovery is dynamic + cross-platform, mirroring the
/// whisper transcriber (env <c>ZEUS_LLAMA_CLI</c> / <c>ZEUS_LLAMA_MODEL</c>,
/// PATH, Homebrew, the app's bundled dir, .exe on Windows).
/// </summary>
public sealed class LlamaSummarizer
{
    private readonly ILogger<LlamaSummarizer> _log;

    private const string SystemPrompt =
        "You are a ham-radio net assistant. You are given a rough, noisy speech-to-text " +
        "transcript of an amateur-radio HF net (callsigns and words may be imperfect). " +
        "Write a brief plain-English summary in 2-4 sentences: who appeared to run the net, " +
        "roughly who checked in, and what was discussed (band/propagation, weather, equipment, " +
        "topics). Do not invent specific callsigns or facts not present. Be concise and factual.";

    public LlamaSummarizer(ILogger<LlamaSummarizer> log)
    {
        _log = log;
        _log.LogInformation(
            "voyeur.llama init cli={Cli} model={Model}",
            LocateCli() ?? "missing", LocateModel() ?? "missing");
    }

    /// <summary>True when both a llama binary and a model are present right now.</summary>
    public bool Available => LocateCli() is not null && LocateModel() is not null;

    public string? CliPath => LocateCli();
    public string? ModelPath => LocateModel();

    /// <summary>Where the in-app installer drops the LLM model + binary.</summary>
    public static string ModelDir => Path.Combine(ZeusAppData(), "llama");
    public static string BinDir => Path.Combine(ModelDir, "bin");

    /// <summary>
    /// Summarize a transcript. Returns the digest text, or null if the LLM is
    /// unavailable, timed out, or produced nothing. NEVER throws to the caller;
    /// NEVER blocks past <paramref name="timeout"/> (a wedged child is killed).
    /// </summary>
    public async Task<string?> SummarizeAsync(string transcript, TimeSpan timeout, CancellationToken ct)
    {
        var cli = LocateCli();
        var model = LocateModel();
        if (cli is null || model is null || string.IsNullOrWhiteSpace(transcript)) return null;

        // Keep the prompt bounded — a multi-hour net can be long; the head +
        // tail carries the structure (who opened, who closed) without blowing
        // the context window of a small local model.
        var text = Trim(transcript, 6000);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = cli,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-m"); psi.ArgumentList.Add(model);
            psi.ArgumentList.Add("-sys"); psi.ArgumentList.Add(SystemPrompt);
            psi.ArgumentList.Add("-p"); psi.ArgumentList.Add(text);
            psi.ArgumentList.Add("-n"); psi.ArgumentList.Add("220");      // max new tokens
            psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("8192");     // context
            psi.ArgumentList.Add("--no-display-prompt");

            using var proc = new Process { StartInfo = psi };
            var sb = new StringBuilder();
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) sb.AppendLine(e.Data); };
            if (!proc.Start()) return null;
            proc.BeginOutputReadLine();
            try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* best effort */ }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                _log.LogWarning("voyeur.llama timed out after {Sec:F0}s", timeout.TotalSeconds);
                return null;
            }

            return Clean(sb.ToString());
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "voyeur.llama summarize failed");
            return null;
        }
    }

    // Strip llama-cli/completion interactive artifacts ("> EOF by user", the
    // "[ Prompt: … ]" stats line, leading "> " prompts) and collapse whitespace.
    private static string? Clean(string raw)
    {
        var lines = raw.Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l =>
            {
                var t = l.TrimStart();
                if (t.Length == 0) return false;
                if (t.StartsWith('>')) return false;           // interactive prompt echo
                if (t.StartsWith('[') && t.Contains("t/s")) return false; // stats line
                return true;
            })
            .ToList();
        var text = string.Join(' ', lines).Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string Trim(string s, int max)
    {
        if (s.Length <= max) return s;
        // Head + tail: the net's open and close carry the most structure.
        int half = max / 2;
        return s[..half] + "\n…\n" + s[^half..];
    }

    private static string? LocateCli()
    {
        var env = Environment.GetEnvironmentVariable("ZEUS_LLAMA_CLI");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

        bool win = OperatingSystem.IsWindows();
        // Prefer the one-shot completion tool; fall back to llama-cli.
        string[] names = win
            ? new[] { "llama-completion.exe", "llama-cli.exe" }
            : new[] { "llama-completion", "llama-cli" };

        var dirs = new List<string>
        {
            BinDir,
            AppContext.BaseDirectory,
            Path.Combine(AppContext.BaseDirectory, "llama"),
        };
        if (!win)
        {
            dirs.Add("/opt/homebrew/bin");
            dirs.Add("/usr/local/bin");
            dirs.Add("/usr/bin");
        }
        foreach (var n in names)
            foreach (var d in dirs)
            {
                try { var p = Path.Combine(d, n); if (File.Exists(p)) return p; }
                catch { /* ignore */ }
            }
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var n in names)
            foreach (var d in pathVar.Split(Path.PathSeparator))
            {
                try { var p = Path.Combine(d, n); if (File.Exists(p)) return p; }
                catch { /* ignore */ }
            }
        return null;
    }

    private static string? LocateModel()
    {
        var env = Environment.GetEnvironmentVariable("ZEUS_LLAMA_MODEL");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        if (!Directory.Exists(ModelDir)) return null;
        return Directory.EnumerateFiles(ModelDir, "*.gguf").FirstOrDefault();
    }

    private static string ZeusAppData()
    {
        var appData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);
        return Path.Combine(appData, "Zeus");
    }
}
