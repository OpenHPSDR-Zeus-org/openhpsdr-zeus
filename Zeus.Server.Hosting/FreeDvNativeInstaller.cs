// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// FreeDvNativeInstaller — the in-app "Install FreeDV" provisioning flow for the
// codec2 modem library, modelled on VstEngineInstaller.
//
// codec2 cannot be built on a stock operator machine (its OFDM code is C99
// _Complex, which MSVC can't compile — Windows needs a MinGW-w64 toolchain) and
// upstream drowe67/codec2 publishes no prebuilt shared libraries. Zeus itself
// builds the per-platform binary in CI (.github/workflows/build-native-libs.yml)
// and commits it under Zeus.Dsp/runtimes/{rid}/native/. A normal Zeus build
// therefore already carries FreeDV; the panel only reports "not installed" on an
// OLDER build that predates the committed binary, or a platform whose binary was
// never shipped (e.g. win-arm64, tracked as a follow-up).
//
// This installer back-fills exactly that case without a full app update: it
// downloads the committed binary for the running platform straight from the Zeus
// repo (raw GitHub, pinned ref), stages it into the writable managed path
// (FreeDvNativeLoader.ManagedLibraryPath), then asks FreeDvService to reload the
// modem so NativeAvailable flips true live — no restart. Nothing is built on the
// user's machine and no extra hosting is required.

using System.Net;
using Zeus.Dsp.FreeDv;

namespace Zeus.Server;

/// <summary>Backs the in-app "Install FreeDV" action: downloads the prebuilt
/// codec2 library Zeus committed for the running platform and stages it at the
/// Zeus-managed path, then reloads the modem. Registered as a singleton in
/// <c>ZeusHost</c>.</summary>
public sealed class FreeDvNativeInstaller
{
    // Where the committed binary lives. The default points at the Zeus repo's
    // runtimes/ tree; both the ref and the base are overridable for staging and
    // tests. raw.githubusercontent serves the committed file with no auth (the
    // repo is public), no toolchain, and no extra hosting.
    internal const string DefaultBaseUrl =
        "https://raw.githubusercontent.com/OpenHPSDR-Zeus-org/openhpsdr-zeus";
    internal const string DefaultRef = "develop";

    public enum Phase { Idle, Downloading, Staging, Done, Failed }

    /// <summary>Coarse install progress for the polling frontend.</summary>
    public sealed record Status(Phase Phase, int Percent, string? Message)
    {
        public bool InProgress => Phase is Phase.Downloading or Phase.Staging;
    }

    private readonly ILogger<FreeDvNativeInstaller> _log;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly FreeDvService _freeDv;
    private readonly object _lock = new();
    private Status _status = new(Phase.Idle, 0, null);
    private Task? _running;

    public FreeDvNativeInstaller(
        ILogger<FreeDvNativeInstaller> log,
        IHttpClientFactory httpClientFactory,
        FreeDvService freeDv)
    {
        _log = log;
        _httpClientFactory = httpClientFactory;
        _freeDv = freeDv;
    }

    /// <summary>True when the codec2 library is already loadable — the install
    /// affordance is unnecessary.</summary>
    public bool Installed => _freeDv.NativeAvailable;

    /// <summary>Snapshot of the current install state for the status endpoint.</summary>
    public Status Current
    {
        get { lock (_lock) return _status; }
    }

    /// <summary>Kick off a background install if one isn't already running and the
    /// library isn't already present. Returns the (possibly unchanged) status
    /// immediately — the frontend polls <see cref="Current"/> for progress.</summary>
    public Status Start(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_running is { IsCompleted: false })
                return _status; // already running

            if (Installed)
            {
                _status = new Status(Phase.Done, 100, "FreeDV is already installed.");
                return _status;
            }

            if (FreeDvNativeLoader.ManagedLibraryPath() is null)
            {
                _status = new Status(Phase.Failed, 0,
                    "No writable install location on this platform.");
                return _status;
            }

            _status = new Status(Phase.Downloading, 0, "Contacting the download server…");
            _running = Task.Run(() => RunAsync(ct), CancellationToken.None);
            return _status;
        }
    }

    private void SetStatus(Phase phase, int percent, string? message)
    {
        lock (_lock) _status = new Status(phase, Math.Clamp(percent, 0, 100), message);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        string? tempFile = null;
        try
        {
            string rid = FreeDvNativeLoader.CurrentRid();
            string fileName = FreeDvNativeLoader.NativeFileName();
            string destPath = FreeDvNativeLoader.ManagedLibraryPath()
                ?? throw new InvalidOperationException("No managed install path on this platform.");
            string destDir = Path.GetDirectoryName(destPath)!;
            string url = BuildLibraryUrl(BaseUrl(), Ref(), rid, fileName);

            SetStatus(Phase.Downloading, 3, $"Downloading the FreeDV modem for {rid}…");
            var http = _httpClientFactory.CreateClient("ZeusFreeDvNative");

            // Download to a temp file alongside the managed dir so the final move
            // is atomic on the same volume (and never leaves a half-written DLL
            // where the loader would try to open it).
            Directory.CreateDirectory(destDir);
            tempFile = Path.Combine(destDir, $".codec2-download-{Guid.NewGuid():N}.tmp");
            await DownloadAsync(http, url, tempFile, rid, ct).ConfigureAwait(false);

            // Stage (85..95%): move into place, replacing any stale copy.
            SetStatus(Phase.Staging, 88, "Installing the FreeDV modem…");
            File.Move(tempFile, destPath, overwrite: true);
            tempFile = null;

            // Reload (95..100%): re-probe + swap in a fresh modem so FreeDV can go
            // live without a restart.
            SetStatus(Phase.Staging, 95, "Loading the FreeDV modem…");
            bool ok = _freeDv.ReloadNative();
            if (ok)
            {
                SetStatus(Phase.Done, 100, "FreeDV installed. Select FreeDV mode to use it.");
                _log.LogInformation("FreeDV codec2 staged at {Path} from {Url}", destPath, url);
            }
            else
            {
                SetStatus(Phase.Failed, 0,
                    "Downloaded the FreeDV modem, but the codec2 library still won't load on this system.");
                _log.LogWarning("FreeDV codec2 staged at {Path} but did not load", destPath);
            }
        }
        catch (PlatformUnavailableException ex)
        {
            SetStatus(Phase.Failed, 0, ex.Message);
            _log.LogWarning("FreeDV install unavailable: {Message}", ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            SetStatus(Phase.Failed, 0, "FreeDV install was cancelled.");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "FreeDV install failed");
            SetStatus(Phase.Failed, 0, $"FreeDV install failed: {Trunc(ex.Message)}");
        }
        finally
        {
            TryDelete(tempFile);
        }
    }

    private async Task DownloadAsync(HttpClient http, string url, string destPath, string rid, CancellationToken ct)
    {
        using var response = await http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new PlatformUnavailableException(
                $"FreeDV isn't published for this platform yet ({rid}). "
                + "The codec2 modem ships for win-x64, linux-x64, linux-arm64 and osx-arm64.");
        response.EnsureSuccessStatusCode();

        long total = response.Content.Headers.ContentLength ?? 0;
        await using var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            read += n;
            if (total > 0)
            {
                // Map download to the 3..85% band.
                int pct = 3 + (int)(read * 82 / total);
                SetStatus(Phase.Downloading, pct, "Downloading the FreeDV modem…");
            }
        }
    }

    /// <summary>Build the raw-GitHub URL for the committed codec2 binary. Pure +
    /// internal so the RID/path mapping is unit-testable without a network call.</summary>
    internal static string BuildLibraryUrl(string baseUrl, string gitRef, string rid, string fileName)
        => $"{baseUrl.TrimEnd('/')}/{gitRef}/Zeus.Dsp/runtimes/{rid}/native/{fileName}";

    private static string BaseUrl()
    {
        var env = Environment.GetEnvironmentVariable("ZEUS_FREEDV_NATIVE_BASE_URL");
        return string.IsNullOrWhiteSpace(env) ? DefaultBaseUrl : env.Trim();
    }

    private static string Ref()
    {
        var env = Environment.GetEnvironmentVariable("ZEUS_FREEDV_NATIVE_REF");
        return string.IsNullOrWhiteSpace(env) ? DefaultRef : env.Trim();
    }

    private static string Trunc(string s, int max = 200)
        => s.Length <= max ? s : s[..max] + "…";

    private static void TryDelete(string? path)
    {
        if (path is null) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    /// <summary>The committed binary doesn't exist for the running platform (a 404
    /// on the repo path) — surfaced as a clear operator message, not a stack dump.</summary>
    private sealed class PlatformUnavailableException(string message) : Exception(message);
}
