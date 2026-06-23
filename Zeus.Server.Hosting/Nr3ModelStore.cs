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
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using Microsoft.Extensions.Logging;

namespace Zeus.Server;

/// <summary>
/// Stores the operator-installed RNNoise (NR3) model file on disk and tracks
/// which one is active. Zeus ships NO bundled model — NR3 stays inert until the
/// operator installs an RNNoise weights file (upload or URL download via the DSP
/// menu). One model at a time: installing replaces the previous one.
///
/// The file lives under <c>%LOCALAPPDATA%/Zeus/nr3-models/</c>
/// (cross-platform <see cref="Environment.SpecialFolder.LocalApplicationData"/>:
/// <c>~/.local/share/Zeus</c> on Linux, <c>~/Library/Application Support/Zeus</c>
/// on macOS). The file itself is the persistence — no LiteDB row — so a model
/// survives restarts and the active model is simply "the one file in the dir".
///
/// The native loader (<c>RNNRloadModel</c>, native/wdsp/rnnr.c) takes a path and
/// is process-global, so <see cref="DspPipelineService"/> calls
/// <c>IDspEngine.LoadNr3Model</c> with <see cref="GetActiveModelPath"/> on engine
/// (re)creation and whenever <see cref="Changed"/> fires.
/// </summary>
public sealed class Nr3ModelStore
{
    // RNNoise weight dumps are tiny (the stock xiph model is well under 1 MiB),
    // but custom/experimental models vary. 64 MiB is a generous ceiling that
    // still rejects an accidental upload of the wrong (huge) file.
    private const long MaxModelBytes = 64L * 1024 * 1024;

    private readonly ILogger<Nr3ModelStore> _log;
    private readonly string _dir;
    private readonly object _gate = new();

    /// <summary>Raised after the active model changes (install or remove) so the
    /// DSP pipeline can re-push it to the engine. Argument is the new active
    /// model path, or null when the model was removed.</summary>
    public event Action<string?>? Changed;

    public Nr3ModelStore(ILogger<Nr3ModelStore> log)
    {
        _log = log;
        var appData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);
        _dir = Path.Combine(appData, "Zeus", "nr3-models");
    }

    // Test/host override so xUnit (parallel WebApplicationFactory) and headless
    // runs can point at an isolated throw-away directory.
    public Nr3ModelStore(ILogger<Nr3ModelStore> log, string directory)
    {
        _log = log;
        _dir = directory;
    }

    /// <summary>Absolute path of the installed model file, or null when none is
    /// installed. The active model is the single file in the models dir.</summary>
    public string? GetActiveModelPath()
    {
        lock (_gate)
        {
            if (!Directory.Exists(_dir)) return null;
            // First (and by contract only) file in the dir is the active model.
            foreach (var path in Directory.EnumerateFiles(_dir))
                return path;
            return null;
        }
    }

    /// <summary>File name of the installed model, or null when none is
    /// installed. Surfaced to the UI via StateDto.Nr3ModelName.</summary>
    public string? GetActiveModelName()
    {
        var path = GetActiveModelPath();
        return path is null ? null : Path.GetFileName(path);
    }

    /// <summary>
    /// Install (replacing any prior model) the given bytes under a sanitized
    /// copy of <paramref name="originalName"/>. Returns the absolute path the
    /// file was written to. Throws <see cref="ArgumentException"/> on an empty
    /// payload or one over <see cref="MaxModelBytes"/>. Raises
    /// <see cref="Changed"/> with the new path.
    /// </summary>
    public string Install(ReadOnlySpan<byte> content, string originalName)
    {
        if (content.Length == 0)
            throw new ArgumentException("NR3 model file is empty.", nameof(content));
        if (content.Length > MaxModelBytes)
            throw new ArgumentException(
                $"NR3 model file is {content.Length} bytes, over the {MaxModelBytes}-byte limit.",
                nameof(content));

        var safeName = SanitizeFileName(originalName);
        string destPath;
        lock (_gate)
        {
            Directory.CreateDirectory(_dir);
            // One model at a time — clear any previous file(s) so GetActiveModelPath
            // is unambiguous.
            foreach (var existing in Directory.EnumerateFiles(_dir))
                TryDelete(existing);

            destPath = Path.Combine(_dir, safeName);
            File.WriteAllBytes(destPath, content.ToArray());
        }

        _log.LogInformation(
            "nr3.model.installed name=\"{Name}\" bytes={Bytes} path=\"{Path}\"",
            safeName, content.Length, destPath);
        Changed?.Invoke(destPath);
        return destPath;
    }

    /// <summary>Remove the installed model. Returns true if a model existed and
    /// was removed. Raises <see cref="Changed"/> (null) when something was
    /// removed so the engine clears its loaded model.</summary>
    public bool Remove()
    {
        bool removed = false;
        lock (_gate)
        {
            if (Directory.Exists(_dir))
            {
                foreach (var path in Directory.EnumerateFiles(_dir))
                {
                    TryDelete(path);
                    removed = true;
                }
            }
        }

        if (removed)
        {
            _log.LogInformation("nr3.model.removed");
            Changed?.Invoke(null);
        }
        return removed;
    }

    // Strip any directory component (defends against "../" path traversal in an
    // uploaded filename) and fall back to a stable name when the result is empty.
    private static string SanitizeFileName(string originalName)
    {
        var name = Path.GetFileName(originalName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(name))
            name = "model.rnnn";
        // Replace any remaining invalid chars defensively.
        foreach (var bad in Path.GetInvalidFileNameChars())
            name = name.Replace(bad, '_');
        return name;
    }

    private void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException ex) { _log.LogWarning("nr3.model.delete.failed path=\"{Path}\" detail={Msg}", path, ex.Message); }
        catch (UnauthorizedAccessException ex) { _log.LogWarning("nr3.model.delete.denied path=\"{Path}\" detail={Msg}", path, ex.Message); }
    }
}
