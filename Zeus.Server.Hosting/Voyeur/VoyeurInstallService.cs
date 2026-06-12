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

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Zeus.Server.Voyeur;

/// <summary>
/// In-app, terminal-free setup for Voyeur Mode transcription (zeus-la5) —
/// cross-platform (macOS / Linux / Windows). Downloads the whisper speech model
/// (the large, license-/choice-dependent file that can't ship in the installer)
/// into the Zeus app-data <c>whisper/</c> folder, with live progress, so the
/// operator never has to run a command. Discovery is dynamic, so the model is
/// picked up the moment the download finishes — no restart.
///
/// The whisper-cli BINARY is intended to ship bundled with Zeus per-platform
/// (built in CI alongside libwdsp); when a hosted per-RID binary URL is
/// configured here, this service will fetch it the same way. Until then, the
/// binary status is surfaced so the panel can show exactly what's missing.
/// </summary>
public sealed class VoyeurInstallService
{
    public enum Phase { Idle, Downloading, Done, Error }

    public sealed record Progress(
        Phase Phase, int Percent, string Message, string? Item,
        bool ModelPresent, bool BinaryPresent, string Rid);

    private readonly ILogger<VoyeurInstallService> _log;
    private readonly IHttpClientFactory _httpFactory;
    private readonly WhisperTranscriber _whisper;

    private readonly object _gate = new();
    private Phase _phase = Phase.Idle;
    private int _percent;
    private string _message = "";
    private string? _item;
    private CancellationTokenSource? _cts;

    // Models offered in-app. Stable Hugging Face URLs (whisper.cpp, MIT).
    // small.en is the lighter default; medium.en is the Phase-0 accuracy pick.
    private static readonly Dictionary<string, (string Url, long MinBytes, string Label)> Models = new()
    {
        ["small.en"] = ("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.en.bin",
                        400_000_000, "Small (English) — ~0.5 GB, fast"),
        ["medium.en"] = ("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-medium.en.bin",
                         1_300_000_000, "Medium (English) — ~1.5 GB, most accurate"),
    };

    public VoyeurInstallService(
        ILogger<VoyeurInstallService> log,
        IHttpClientFactory httpFactory,
        WhisperTranscriber whisper)
    {
        _log = log;
        _httpFactory = httpFactory;
        _whisper = whisper;
    }

    public static IEnumerable<object> AvailableModels =>
        Models.Select(m => new { id = m.Key, label = m.Value.Label });

    public Progress Status()
    {
        lock (_gate)
        {
            return new Progress(
                _phase, _percent, _message, _item,
                ModelPresent: _whisper.ModelPath is not null,
                BinaryPresent: _whisper.CliPath is not null,
                Rid: Rid());
        }
    }

    /// <summary>Begin downloading <paramref name="modelId"/>. Returns the current
    /// status immediately; progress is polled via <see cref="Status"/>. No-op if
    /// a download is already running.</summary>
    public Progress InstallModel(string modelId)
    {
        lock (_gate)
        {
            if (_phase == Phase.Downloading) return Status();
            if (!Models.ContainsKey(modelId))
            {
                _phase = Phase.Error;
                _message = $"unknown model '{modelId}'";
                return Status();
            }
            _phase = Phase.Downloading;
            _percent = 0;
            _item = modelId;
            _message = "starting…";
            _cts = new CancellationTokenSource();
        }
        var ct = _cts!.Token;
        _ = Task.Run(() => DownloadModelAsync(modelId, ct), ct);
        return Status();
    }

    /// <summary>Cancel an in-flight download.</summary>
    public void Cancel() { lock (_gate) { _cts?.Cancel(); } }

    private async Task DownloadModelAsync(string modelId, CancellationToken ct)
    {
        var (url, minBytes, _) = Models[modelId];
        Directory.CreateDirectory(WhisperTranscriber.ModelDir);
        var finalPath = Path.Combine(WhisperTranscriber.ModelDir, $"ggml-{modelId}.bin");
        var tmpPath = finalPath + ".part";

        try
        {
            var http = _httpFactory.CreateClient("voyeur-install");
            http.Timeout = Timeout.InfiniteTimeSpan; // large file; we cap via ct, not a fixed timeout

            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            long? total = resp.Content.Headers.ContentLength;

            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(tmpPath))
            {
                var buf = new byte[1 << 20];
                long read = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    read += n;
                    if (total is > 0)
                        SetProgress((int)(read * 100 / total.Value),
                            $"downloading {modelId} — {read / 1_000_000} / {total / 1_000_000} MB");
                    else
                        SetProgress(0, $"downloading {modelId} — {read / 1_000_000} MB");
                }
            }

            var size = new FileInfo(tmpPath).Length;
            if (size < minBytes)
                throw new InvalidOperationException(
                    $"download too small ({size} bytes) — likely an error page, not the model");

            // Atomic publish: only a fully-downloaded, sane-sized file becomes
            // the live model. A crash mid-download leaves only the .part.
            File.Move(tmpPath, finalPath, overwrite: true);

            lock (_gate)
            {
                _phase = Phase.Done;
                _percent = 100;
                _message = $"{modelId} ready";
            }
            _log.LogInformation("voyeur.install model {Model} done ({Mb} MB)", modelId, size / 1_000_000);
        }
        catch (OperationCanceledException)
        {
            TryDelete(tmpPath);
            lock (_gate) { _phase = Phase.Idle; _percent = 0; _message = "cancelled"; }
        }
        catch (Exception ex)
        {
            TryDelete(tmpPath);
            lock (_gate) { _phase = Phase.Error; _message = ex.Message; }
            _log.LogWarning(ex, "voyeur.install model {Model} failed", modelId);
        }
    }

    private void SetProgress(int percent, string message)
    {
        lock (_gate) { _percent = percent; _message = message; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }

    private static string Rid()
    {
        string os = OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsMacOS() ? "osx"
            : OperatingSystem.IsLinux() ? "linux" : "unknown";
        string arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            _ => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
        };
        return $"{os}-{arch}";
    }
}
