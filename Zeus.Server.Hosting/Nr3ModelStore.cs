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
/// which one is active. Zeus ships a <b>bundled default model</b>
/// (<see cref="BundledDefaultFileName"/>, next to the app under
/// <see cref="AppContext.BaseDirectory"/>) so NR3 works out of the box; the
/// operator can override it by installing their own weights file (upload or URL
/// download via the DSP menu). One operator model at a time: installing replaces
/// the previous one; removing it reverts to the bundled default.
///
/// Resolution order for the active model is: operator-installed file (if any)
/// → bundled default (if shipped) → none. The operator file lives under
/// <c>%LOCALAPPDATA%/Zeus/nr3-models/</c>
/// (cross-platform <see cref="Environment.SpecialFolder.LocalApplicationData"/>:
/// <c>~/.local/share/Zeus</c> on Linux, <c>~/Library/Application Support/Zeus</c>
/// on macOS). The file itself is the persistence — no LiteDB row — so a model
/// survives restarts and the active operator model is simply "the one file in
/// the dir".
///
/// The native loader (<c>RNNRloadModel</c>, native/wdsp/rnnr.c) takes a path and
/// is process-global, so <see cref="DspPipelineService"/> calls
/// <c>IDspEngine.LoadNr3Model</c> with <see cref="GetActiveModelPath"/> on engine
/// (re)creation and whenever <see cref="Changed"/> fires.
/// </summary>
public sealed class Nr3ModelStore
{
    /// <summary>File name of the bundled default RNNoise model, copied next to
    /// the app at build/publish (see Zeus.Server.Hosting.csproj). A standard
    /// xiph/rnnoise model in DNNw weights-file format (BSD-3-Clause) compatible
    /// with the vendored rnnoise architecture — see native/rnnoise/VENDORING.md
    /// and ATTRIBUTIONS.md.</summary>
    public const string BundledDefaultFileName = "rnnoise-default.bin";

    /// <summary>Display name surfaced to the UI when the bundled default is the
    /// active model (no operator model installed).</summary>
    public const string BundledDefaultDisplayName = "RNNoise (bundled default)";

    // RNNoise weight dumps are tiny (the stock xiph model is well under 1 MiB),
    // but custom/experimental models vary. 64 MiB is a generous ceiling that
    // still rejects an accidental upload of the wrong (huge) file.
    private const long MaxModelBytes = 64L * 1024 * 1024;

    private readonly ILogger<Nr3ModelStore> _log;
    private readonly string _dir;
    // Absolute path of the shipped default model, or null when no default is
    // present (e.g. a dev build that didn't copy the asset, or a test).
    private readonly string? _bundledDefaultPath;
    private readonly object _gate = new();

    /// <summary>Raised after the active model changes (install or remove) so the
    /// DSP pipeline can re-push it to the engine. Argument is the new active
    /// model path (operator file, or the bundled default after a remove), or
    /// null when neither is available.</summary>
    public event Action<string?>? Changed;

    public Nr3ModelStore(ILogger<Nr3ModelStore> log)
    {
        _log = log;
        var appData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);
        _dir = Path.Combine(appData, "Zeus", "nr3-models");
        var bundled = Path.Combine(AppContext.BaseDirectory, BundledDefaultFileName);
        _bundledDefaultPath = File.Exists(bundled) ? bundled : null;
    }

    // Test/host override so xUnit (parallel WebApplicationFactory) and headless
    // runs can point at an isolated throw-away directory. A test may also supply
    // a bundled-default path to exercise the fallback; null disables it.
    public Nr3ModelStore(ILogger<Nr3ModelStore> log, string directory, string? bundledDefaultPath = null)
    {
        _log = log;
        _dir = directory;
        _bundledDefaultPath = bundledDefaultPath is not null && File.Exists(bundledDefaultPath)
            ? bundledDefaultPath
            : null;
    }

    // The operator-installed model path (first file in the dir), or null. Caller
    // must hold _gate.
    private string? GetOperatorModelPathLocked()
    {
        if (!Directory.Exists(_dir)) return null;
        foreach (var path in Directory.EnumerateFiles(_dir))
            return path;
        return null;
    }

    /// <summary>Absolute path of the active model — the operator-installed file
    /// if any, otherwise the bundled default, otherwise null. This is what the
    /// DSP pipeline loads.</summary>
    public string? GetActiveModelPath()
    {
        lock (_gate)
        {
            return GetOperatorModelPathLocked() ?? _bundledDefaultPath;
        }
    }

    /// <summary>Name shown to the operator: the installed file name, or
    /// <see cref="BundledDefaultDisplayName"/> when the bundled default is
    /// active, or null when neither is available. Surfaced via
    /// StateDto.Nr3ModelName.</summary>
    public string? GetActiveModelName()
    {
        lock (_gate)
        {
            var op = GetOperatorModelPathLocked();
            if (op is not null) return Path.GetFileName(op);
            return _bundledDefaultPath is not null ? BundledDefaultDisplayName : null;
        }
    }

    /// <summary>True when an operator-installed model exists (so the UI can offer
    /// "Remove" and label the source). False when running on the bundled default
    /// or with no model at all. Surfaced via StateDto.Nr3UsingBundledDefault
    /// (negated).</summary>
    public bool HasOperatorModel()
    {
        lock (_gate) { return GetOperatorModelPathLocked() is not null; }
    }

    /// <summary>True when the active model is the bundled default (no operator
    /// model installed and a default is shipped).</summary>
    public bool UsingBundledDefault()
    {
        lock (_gate) { return GetOperatorModelPathLocked() is null && _bundledDefaultPath is not null; }
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

    /// <summary>Remove the operator-installed model. Returns true if one existed
    /// and was removed. Raises <see cref="Changed"/> with the now-active path —
    /// the bundled default if one is shipped, otherwise null — so the engine
    /// reverts to the default model (or clears) rather than going inert.</summary>
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
            _log.LogInformation("nr3.model.removed reverting_to={Target}",
                _bundledDefaultPath is not null ? "bundled-default" : "none");
            Changed?.Invoke(GetActiveModelPath());
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
