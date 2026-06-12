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
using Microsoft.Extensions.Logging;

namespace Zeus.Server.Voyeur;

/// <summary>
/// Runs whisper.cpp as a SEPARATE, SUPERVISED OS CHILD PROCESS to transcribe a
/// captured "over" (Voyeur Mode, zeus-la5 Phase 2). The child-process boundary
/// is load-bearing safety, not a convenience: whisper.cpp is native code, and
/// a segfault / access-violation / OOM there must kill only the child, never
/// the Zeus host (which carries the realtime RX/DSP/PureSignal/TX threads).
/// We therefore NEVER P/Invoke whisper — we shell out to <c>whisper-cli</c>,
/// feed it the over's WAV, read the text it writes, and bound it with a hard
/// timeout (kill + drop the segment) so a wedged child can never stall the
/// pipeline.
///
/// Graceful absence: if the binary or model isn't installed, transcription is
/// simply OFF — Voyeur Mode still captures overs (Phase 1), and the panel tells
/// the operator how to enable ASR. Discovery is env-overridable
/// (<c>ZEUS_WHISPER_CLI</c> / <c>ZEUS_WHISPER_MODEL</c>) and otherwise probes
/// the usual Homebrew / system locations plus the Zeus app-data
/// <c>whisper/</c> folder for a <c>ggml-*.en.bin</c> model.
/// </summary>
public sealed class WhisperTranscriber
{
    private readonly ILogger<WhisperTranscriber> _log;
    private readonly string? _cliPath;
    private readonly string? _modelPath;

    // Ham-context prompt — biases decoding toward phonetics / Q-codes /
    // callsign shapes. Proven on the Phase-0 spike against real eCARS audio.
    private const string HamPrompt =
        "Amateur radio HF SSB net. Operators identify by callsign using the NATO " +
        "phonetic alphabet: Alpha Bravo Charlie Delta Echo Foxtrot Golf Hotel India " +
        "Juliet Kilo Lima Mike November Oscar Papa Quebec Romeo Sierra Tango Uniform " +
        "Victor Whiskey X-ray Yankee Zulu. Callsigns look like W1ABC, KB2UKA, VE3XYZ, " +
        "G4ABC. Common terms: QSL, QRZ, QSO, QTH, QRM, QRN, QSB, roger, copy, over, " +
        "standing by, this is, back to net control, signal report five nine, " +
        "seventy-three, handle, grid square, rig, antenna, beam, dipole, watts.";

    public WhisperTranscriber(ILogger<WhisperTranscriber> log)
    {
        _log = log;
        _cliPath = LocateCli();
        _modelPath = LocateModel();
        if (Available)
            _log.LogInformation("voyeur.whisper ready cli={Cli} model={Model}", _cliPath, _modelPath);
        else
            _log.LogInformation(
                "voyeur.whisper not configured (cli={Cli} model={Model}) — capture-only until installed",
                _cliPath ?? "missing", _modelPath ?? "missing");
    }

    /// <summary>True when both the binary and a model are present.</summary>
    public bool Available => _cliPath is not null && _modelPath is not null;

    /// <summary>Where the model is expected to live, for the panel's setup
    /// instructions even when one isn't present yet.</summary>
    public static string ModelDir => Path.Combine(ZeusAppData(), "whisper");

    /// <summary>
    /// Transcribe a WAV (any sample rate — whisper-cli resamples to 16 kHz
    /// internally). Returns the transcript text, or null if transcription is
    /// unavailable, timed out, or failed. NEVER throws into the caller, and
    /// NEVER blocks past <paramref name="timeout"/> — a wedged child is killed.
    /// </summary>
    public async Task<string?> TranscribeAsync(string wavPath, TimeSpan timeout, CancellationToken ct)
    {
        if (!Available || !File.Exists(wavPath)) return null;

        var outBase = Path.Combine(Path.GetTempPath(), "zeus-voyeur-" + Guid.NewGuid().ToString("N"));
        var txtPath = outBase + ".txt";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _cliPath!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-m"); psi.ArgumentList.Add(_modelPath!);
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add(wavPath);
            psi.ArgumentList.Add("--prompt"); psi.ArgumentList.Add(HamPrompt);
            psi.ArgumentList.Add("-otxt");
            psi.ArgumentList.Add("-of"); psi.ArgumentList.Add(outBase);
            psi.ArgumentList.Add("-np"); // no per-segment progress prints

            using var proc = new Process { StartInfo = psi };
            if (!proc.Start()) return null;

            // Below-normal priority so ASR bursts never preempt realtime threads.
            try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* best effort */ }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Timeout or shutdown — kill the whole child tree and drop the
                // segment. Never let a wedged child hold the pipeline.
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                _log.LogWarning("voyeur.whisper timed out after {Sec:F0}s — segment dropped", timeout.TotalSeconds);
                return null;
            }

            if (proc.ExitCode != 0)
            {
                _log.LogWarning("voyeur.whisper exit={Code}", proc.ExitCode);
                return null;
            }

            if (!File.Exists(txtPath)) return null;
            var text = (await File.ReadAllTextAsync(txtPath, ct)).Trim();
            return string.IsNullOrWhiteSpace(text) ? null : Clean(text);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "voyeur.whisper transcribe failed — segment left untranscribed");
            return null;
        }
        finally
        {
            try { if (File.Exists(txtPath)) File.Delete(txtPath); } catch { /* ignore */ }
        }
    }

    // whisper tags non-speech as [BLANK_AUDIO] / (noise) etc.; collapse those
    // and whitespace so a quiet over yields an empty (=> dropped) transcript.
    private static string Clean(string text)
    {
        var lines = text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('[') && !l.StartsWith('('))
            .ToList();
        return string.Join(' ', lines).Trim();
    }

    private static string? LocateCli()
    {
        var env = Environment.GetEnvironmentVariable("ZEUS_WHISPER_CLI");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        // GUI/desktop processes often lack /opt/homebrew/bin on PATH, so probe
        // the usual install locations explicitly.
        string[] names = { "whisper-cli", "whisper", "main" };
        string[] dirs =
        {
            "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin",
        };
        foreach (var d in dirs)
            foreach (var n in names)
            {
                var p = Path.Combine(d, n);
                if (File.Exists(p)) return p;
            }
        // Fall back to PATH search.
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var d in pathVar.Split(Path.PathSeparator))
            foreach (var n in names)
            {
                try { var p = Path.Combine(d, n); if (File.Exists(p)) return p; }
                catch { /* malformed PATH entry */ }
            }
        return null;
    }

    private static string? LocateModel()
    {
        var env = Environment.GetEnvironmentVariable("ZEUS_WHISPER_MODEL");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        var dir = ModelDir;
        if (!Directory.Exists(dir)) return null;
        // Prefer medium.en (Phase-0 recommended default) > small.en > anything.
        string[] prefs = { "ggml-medium.en.bin", "ggml-small.en.bin", "ggml-base.en.bin" };
        foreach (var pref in prefs)
        {
            var p = Path.Combine(dir, pref);
            if (File.Exists(p)) return p;
        }
        return Directory.EnumerateFiles(dir, "ggml-*.bin").FirstOrDefault();
    }

    private static string ZeusAppData()
    {
        var appData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);
        return Path.Combine(appData, "Zeus");
    }
}
