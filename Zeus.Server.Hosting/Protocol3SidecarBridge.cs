// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus - OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace Zeus.Server;

public sealed record Protocol3SidecarSnapshot(
    Uri DiagnosticsUrl,
    string Status,
    string DspEngine,
    int RxExpectedStreams,
    int RxActiveStreams,
    int DspActiveChannels);

public sealed class Protocol3SidecarBridge : IDisposable
{
    public const string HttpClientName = "Protocol3Sidecar";
    private const string HostedSessionArmValue = "ENABLE_SIDECAR_P3_HOSTED_SESSION";

    private readonly object _sync = new();
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<Protocol3SidecarBridge> _log;
    private Process? _process;
    private ProcessOutputTail? _processOutput;
    private bool _ownsProcess;
    private Uri? _diagnosticsUrl;
    private Protocol3SidecarSnapshot? _lastSnapshot;

    public Protocol3SidecarBridge(
        IHttpClientFactory httpFactory,
        IConfiguration configuration,
        ILogger<Protocol3SidecarBridge> log)
    {
        _httpFactory = httpFactory;
        _configuration = configuration;
        _log = log;
    }

    public object Status
    {
        get
        {
            lock (_sync)
            {
                return new
                {
                    configured = ExistingDiagnosticsUrl() is not null ||
                        !string.IsNullOrWhiteSpace(Setting("Project", "ZEUS_PROTOCOL3_SIDECAR_PROJECT")),
                    running = _process is { HasExited: false } || (_diagnosticsUrl is not null && !_ownsProcess),
                    diagnosticsUrl = _diagnosticsUrl?.ToString(),
                    last = _lastSnapshot,
                    sidecarOutput = _processOutput?.Snapshot(),
                };
            }
        }
    }

    public async Task<Protocol3SidecarSnapshot> ConnectAsync(
        IPEndPoint radioEndpoint,
        int p3Port,
        int sampleRateHz,
        int rxStreams,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(radioEndpoint);
        if (p3Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(p3Port));
        if (sampleRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        if (rxStreams is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(rxStreams));

        var existingUrl = ExistingDiagnosticsUrl();
        if (existingUrl is not null)
        {
            var existing = await WaitForReadyAsync(existingUrl, rxStreams, TimeSpan.FromSeconds(10), ct)
                .ConfigureAwait(false);
            Remember(existing, ownsProcess: false, process: null);
            return existing;
        }

        var project = FullPathSetting("Project", "ZEUS_PROTOCOL3_SIDECAR_PROJECT");
        var n9dspLibrary = FullPathSetting("N9DspLibrary", "ZEUS_PROTOCOL3_N9DSP_LIBRARY");
        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(n9dspLibrary))
        {
            throw new InvalidOperationException(
                "Protocol 3 sidecar is not configured. Set ZEUS_PROTOCOL3_SIDECAR_PROJECT to the private " +
                "Zeus.Protocol3.Probe.csproj and ZEUS_PROTOCOL3_N9DSP_LIBRARY to the private n9dsp native library.");
        }
        if (!File.Exists(project))
            throw new InvalidOperationException($"Protocol 3 sidecar project was not found: {project}");
        if (!File.Exists(n9dspLibrary))
            throw new InvalidOperationException($"N9DSP native library was not found: {n9dspLibrary}");

        await DisconnectAsync(ct).ConfigureAwait(false);

        var listenUrl = ListenUrl();
        var diagnosticsUrl = DiagnosticsUrlForListen(listenUrl);
        var psi = BuildStartInfo(project, n9dspLibrary, listenUrl, radioEndpoint.Address, p3Port, sampleRateHz, rxStreams);
        var process = Process.Start(psi) ??
            throw new InvalidOperationException("Protocol 3 sidecar process did not start.");
        var output = ProcessOutputTail.Attach(process);
        Remember(null, true, process, diagnosticsUrl, output);
        _log.LogInformation(
            "p3.sidecar.start pid={Pid} radio={Radio} p3Port={Port} diagnostics={Diagnostics}",
            process.Id,
            radioEndpoint.Address,
            p3Port,
            diagnosticsUrl);

        try
        {
            var snapshot = await WaitForReadyAsync(diagnosticsUrl, rxStreams, TimeSpan.FromSeconds(15), ct)
                .ConfigureAwait(false);
            Remember(snapshot, true, process, diagnosticsUrl, output);
            return snapshot;
        }
        catch
        {
            await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        Process? process;
        bool kill;
        lock (_sync)
        {
            process = _process;
            kill = _ownsProcess;
            _process = null;
            _processOutput = null;
            _ownsProcess = false;
            _diagnosticsUrl = null;
            _lastSnapshot = null;
        }

        if (process is null || !kill) return;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "p3.sidecar.stop.error");
        }
        finally
        {
            process.Dispose();
        }
    }

    public void Dispose()
    {
        try { DisconnectAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        catch { }
    }

    internal static Protocol3SidecarSnapshot ValidateDiagnosticsSnapshot(
        JsonObject diagnostics,
        Uri diagnosticsUrl,
        int expectedRxStreams)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(diagnosticsUrl);
        ValidateDiagnosticsUrl(diagnosticsUrl);

        var protocol = RequiredString(diagnostics, "protocol");
        if (!string.Equals(protocol, "p3", StringComparison.Ordinal))
            throw new InvalidOperationException($"Protocol 3 sidecar diagnostics reported protocol '{protocol}', expected 'p3'.");

        var p3 = RequiredObject(diagnostics, "p3");
        var status = RequiredString(p3, "status");
        if (!string.Equals(status, "ok", StringComparison.Ordinal) &&
            !string.Equals(status, "degraded", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Protocol 3 sidecar diagnostics status is '{status}', expected 'ok' or 'degraded'.");

        var rxActive = RequiredInt(p3, "rxActiveStreams");
        var rxExpected = p3.ContainsKey("rxExpectedStreams")
            ? RequiredInt(p3, "rxExpectedStreams")
            : rxActive;
        if (rxExpected < expectedRxStreams || rxActive < expectedRxStreams)
            throw new InvalidOperationException(
                $"Protocol 3 sidecar reported {rxActive}/{rxExpected} RX streams; Zeus requires {expectedRxStreams}.");

        var dsp = RequiredObject(p3, "dsp");
        var engine = RequiredString(dsp, "engine");
        if (!string.Equals(engine, "n9dsp", StringComparison.Ordinal))
            throw new InvalidOperationException($"Protocol 3 sidecar DSP engine is '{engine}', expected 'n9dsp'.");

        var dspChannels = dsp.ContainsKey("activeChannels")
            ? RequiredInt(dsp, "activeChannels")
            : rxActive;
        if (dspChannels < expectedRxStreams)
            throw new InvalidOperationException(
                $"Protocol 3 sidecar N9DSP reports {dspChannels} active DSP channels; Zeus requires {expectedRxStreams}.");

        return new Protocol3SidecarSnapshot(
            diagnosticsUrl,
            status,
            engine,
            rxExpected,
            rxActive,
            dspChannels);
    }

    private async Task<Protocol3SidecarSnapshot> WaitForReadyAsync(
        Uri diagnosticsUrl,
        int rxStreams,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        Exception? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var snapshot = await FetchSnapshotAsync(diagnosticsUrl, rxStreams, ct).ConfigureAwait(false);
                _log.LogInformation(
                    "p3.sidecar.ready diagnostics={Diagnostics} rx={RxActive}/{RxExpected} dsp={DspChannels}",
                    diagnosticsUrl,
                    snapshot.RxActiveStreams,
                    snapshot.RxExpectedStreams,
                    snapshot.DspActiveChannels);
                return snapshot;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                if (HasOwnedProcessExited(out var exitCode, out var output))
                {
                    var details = string.IsNullOrWhiteSpace(output)
                        ? string.Empty
                        : $" Last sidecar output:{Environment.NewLine}{output}";
                    throw new InvalidOperationException(
                        $"Protocol 3 sidecar exited before diagnostics became ready (exit code {exitCode}).{details}",
                        ex);
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(400), ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            lastError is null
                ? "Protocol 3 sidecar did not report ready diagnostics in time."
                : $"Protocol 3 sidecar did not report ready diagnostics in time: {lastError.Message}",
            lastError);
    }

    private async Task<Protocol3SidecarSnapshot> FetchSnapshotAsync(
        Uri diagnosticsUrl,
        int rxStreams,
        CancellationToken ct)
    {
        ValidateDiagnosticsUrl(diagnosticsUrl);
        var http = _httpFactory.CreateClient(HttpClientName);
        using var response = await http.GetAsync(diagnosticsUrl, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Protocol 3 sidecar diagnostics returned HTTP {(int)response.StatusCode}.");

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var root = await JsonNode.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false) as JsonObject
            ?? throw new InvalidOperationException("Protocol 3 sidecar diagnostics root is not an object.");
        return ValidateDiagnosticsSnapshot(root, diagnosticsUrl, rxStreams);
    }

    private ProcessStartInfo BuildStartInfo(
        string project,
        string n9dspLibrary,
        Uri listenUrl,
        IPAddress radioIp,
        int p3Port,
        int sampleRateHz,
        int rxStreams)
    {
        var dotnet = Setting("DotnetPath", "ZEUS_PROTOCOL3_SIDECAR_DOTNET");
        if (string.IsNullOrWhiteSpace(dotnet)) dotnet = "dotnet";

        var psi = new ProcessStartInfo(dotnet)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(project) ?? AppContext.BaseDirectory,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add(project);
        if (BoolSetting("NoBuild", "ZEUS_PROTOCOL3_SIDECAR_NO_BUILD"))
            psi.ArgumentList.Add("--no-build");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("--hosted-diagnostics-server");
        psi.ArgumentList.Add("--allow-degraded-start");
        psi.ArgumentList.Add("--listen");
        psi.ArgumentList.Add(listenUrl.ToString());
        psi.ArgumentList.Add("--host");
        psi.ArgumentList.Add(radioIp.ToString());
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add(p3Port.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--receive-ms");
        psi.ArgumentList.Add("3000");
        psi.ArgumentList.Add("--poll-ms");
        psi.ArgumentList.Add("1000");
        psi.ArgumentList.Add("--hosted-wait-ms");
        psi.ArgumentList.Add("7000");
        psi.ArgumentList.Add("--serve-ms");
        psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("--freeze-ready-snapshot");
        psi.ArgumentList.Add("--rx-count");
        psi.ArgumentList.Add(rxStreams.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--rx-rate");
        psi.ArgumentList.Add(sampleRateHz.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--n9dsp-library");
        psi.ArgumentList.Add(n9dspLibrary);
        psi.Environment["N9DSP_NATIVE_LIBRARY"] = n9dspLibrary;
        psi.Environment["P3_ZEUS_HOSTED_RADIO_SESSION"] = HostedSessionArmValue;
        return psi;
    }

    private Uri ListenUrl()
    {
        var configured = Setting("ListenUrl", "ZEUS_PROTOCOL3_SIDECAR_LISTEN");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!Uri.TryCreate(configured, UriKind.Absolute, out var uri))
                throw new InvalidOperationException($"Invalid Protocol 3 sidecar listen URL: {configured}");
            ValidateLoopbackHttpBase(uri, "Protocol 3 sidecar listen URL");
            return uri;
        }

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return new Uri($"http://127.0.0.1:{port}/");
    }

    private Uri? ExistingDiagnosticsUrl()
    {
        var raw = Setting("DiagnosticsUrl", "ZEUS_PROTOCOL3_SIDECAR_DIAGNOSTICS_URL");
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"Invalid Protocol 3 sidecar diagnostics URL: {raw}");
        ValidateDiagnosticsUrl(uri);
        return uri;
    }

    private static Uri DiagnosticsUrlForListen(Uri listenUrl)
    {
        ValidateLoopbackHttpBase(listenUrl, "Protocol 3 sidecar listen URL");
        return new UriBuilder(listenUrl)
        {
            Path = "/api/diagnostics/v2",
            Query = string.Empty,
            Fragment = string.Empty,
        }.Uri;
    }

    private static void ValidateDiagnosticsUrl(Uri uri)
    {
        if (!uri.IsAbsoluteUri ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsLoopback ||
            !string.Equals(uri.AbsolutePath, "/api/diagnostics/v2", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Protocol 3 sidecar diagnostics URL must be loopback http://.../api/diagnostics/v2.");
        }
    }

    private static void ValidateLoopbackHttpBase(Uri uri, string label)
    {
        if (!uri.IsAbsoluteUri ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsLoopback ||
            uri.Port is < 1 or > 65535)
        {
            throw new InvalidOperationException($"{label} must be loopback HTTP.");
        }
    }

    private string? Setting(string key, string env)
    {
        var value = Environment.GetEnvironmentVariable(env);
        if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        value = _configuration[$"Zeus:Protocol3:Sidecar:{key}"];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private string? FullPathSetting(string key, string env)
    {
        var value = Setting(key, env);
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(value));
    }

    private bool BoolSetting(string key, string env)
    {
        var value = Setting(key, env);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private void Remember(
        Protocol3SidecarSnapshot? snapshot,
        bool ownsProcess,
        Process? process,
        Uri? diagnosticsUrl = null,
        ProcessOutputTail? output = null)
    {
        lock (_sync)
        {
            _lastSnapshot = snapshot;
            _ownsProcess = ownsProcess;
            _process = process;
            _processOutput = output;
            _diagnosticsUrl = diagnosticsUrl ?? snapshot?.DiagnosticsUrl;
        }
    }

    private bool HasOwnedProcessExited(out int? exitCode, out string output)
    {
        lock (_sync)
        {
            if (_ownsProcess && _process is { HasExited: true } process)
            {
                exitCode = process.ExitCode;
                output = _processOutput?.Snapshot() ?? string.Empty;
                return true;
            }
        }
        exitCode = null;
        output = string.Empty;
        return false;
    }

    private static JsonObject RequiredObject(JsonObject parent, string property) =>
        parent[property] as JsonObject ??
        throw new InvalidOperationException($"Protocol 3 sidecar diagnostics missing object '{property}'.");

    private static string RequiredString(JsonObject parent, string property) =>
        parent[property]?.GetValue<string>() ??
        throw new InvalidOperationException($"Protocol 3 sidecar diagnostics missing string '{property}'.");

    private static int RequiredInt(JsonObject parent, string property)
    {
        var node = parent[property] ??
            throw new InvalidOperationException($"Protocol 3 sidecar diagnostics missing number '{property}'.");
        if (node is JsonValue value)
        {
            if (value.TryGetValue<int>(out var i)) return i;
            if (value.TryGetValue<long>(out var l) && l <= int.MaxValue && l >= int.MinValue) return (int)l;
            if (value.TryGetValue<uint>(out var u) && u <= int.MaxValue) return (int)u;
            if (value.TryGetValue<ulong>(out var ul) && ul <= int.MaxValue) return (int)ul;
        }
        throw new InvalidOperationException($"Protocol 3 sidecar diagnostics property '{property}' is not an int.");
    }

    private sealed class ProcessOutputTail
    {
        private readonly object _sync = new();
        private readonly Queue<string> _lines = new();
        private readonly int _maxLines;

        private ProcessOutputTail(int maxLines)
        {
            _maxLines = maxLines;
        }

        public static ProcessOutputTail Attach(Process process)
        {
            var tail = new ProcessOutputTail(80);
            process.OutputDataReceived += (_, e) => tail.Append("stdout", e.Data);
            process.ErrorDataReceived += (_, e) => tail.Append("stderr", e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return tail;
        }

        public string Snapshot()
        {
            lock (_sync)
                return string.Join(Environment.NewLine, _lines);
        }

        private void Append(string stream, string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (_sync)
            {
                _lines.Enqueue($"{stream}: {line}");
                while (_lines.Count > _maxLines) _lines.Dequeue();
            }
        }
    }
}
