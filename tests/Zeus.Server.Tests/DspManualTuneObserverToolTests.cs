// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Zeus.Server.Tests;

public sealed class DspManualTuneObserverToolTests
{
    private static readonly JsonSerializerOptions CamelCaseJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    [SkippableFact]
    public async Task ManualTuneObserverReportsSuggestedVfoForOffFilterPeak()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell manual-tune observer smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-manual-tune-observer-hint-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            using var server = JsonRouteServer.Start(new Dictionary<string, string>
            {
                ["/api/state"] = Json(new
                {
                    status = "Connected",
                    vfoHz = 14_331_500,
                    radioLoHz = 14_208_500,
                    mode = "USB",
                    filterLowHz = 100,
                    filterHighHz = 3_152
                }),
                ["/api/radio/diagnostics/dsp-scene"] = Json(new
                {
                    status = "fresh",
                    fresh = true,
                    signalProfile = "voice-like",
                    coherentMaxSnrDb = 20.0,
                    maxSnrDb = 20.0,
                    topPeaks = new object[]
                    {
                        FrontendTopPeak(14_366_750, 35_250, 16.8, -84.6),
                        FrontendTopPeak(14_076_688, -254_812, 26.2, -71.2)
                    }
                }),
                ["/api/dsp/live-diagnostics"] = Json(ManualTuneObserverLiveDiagnostics())
            });

            var reportPath = Path.Combine(bundleDir, "manual-observer-hint.json");
            var run = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "watch-dsp-manual-tune-observer.ps1"),
                "-BaseUrl", server.BaseUrl,
                "-ReportPath", reportPath,
                "-OutputRoot", Path.Combine(bundleDir, "captures"),
                "-PollCount", "3",
                "-PollIntervalSec", "0",
                "-StablePolls", "1",
                "-MinCoherentSnrDb", "6",
                "-SceneProfilePattern", "voice",
                "-MaxCaptures", "0",
                "-RequireFrontendNearPassband",
                "-JsonOnly");

            Assert.True(run.ExitCode == 0, run.CombinedOutput);
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
            var root = doc.RootElement;
            Assert.Equal(3, root.GetProperty("pollSampleCount").GetInt32());
            Assert.Equal(0, root.GetProperty("maxCaptures").GetInt32());
            Assert.Equal(3, root.GetProperty("frontendTuningHintPollCount").GetInt32());
            Assert.Equal(0, root.GetProperty("captureQualifiedPollCount").GetInt32());
            Assert.Equal(0, root.GetProperty("frontendFilterPassbandPollCount").GetInt32());
            Assert.Equal(3, root.GetProperty("frontendFilterOffPassbandPollCount").GetInt32());

            var hint = root.GetProperty("frontendBestTuningHint");
            Assert.Equal("above-filter", hint.GetProperty("reason").GetString());
            Assert.Equal(14_366_750, hint.GetProperty("peakFrequencyHz").GetInt64());
            Assert.Equal(35_250.0, hint.GetProperty("peakOffsetHz").GetDouble(), precision: 3);
            Assert.Equal(1_626.0, hint.GetProperty("filterCenterOffsetHz").GetDouble(), precision: 3);
            Assert.Equal(32_098.0, hint.GetProperty("filterDistanceHz").GetDouble(), precision: 3);
            Assert.Equal(33_624.0, hint.GetProperty("suggestedDialShiftHz").GetDouble(), precision: 3);
            Assert.Equal(14_365_124, hint.GetProperty("suggestedVfoHz").GetInt64());
            Assert.Equal(14.365124, hint.GetProperty("suggestedVfoMhz").GetDouble(), precision: 6);

            var poll = root.GetProperty("polls").EnumerateArray().First();
            Assert.Equal(33_624.0, poll.GetProperty("frontendSuggestedDialShiftHz").GetDouble(), precision: 3);
            Assert.Equal(14_365_124, poll.GetProperty("frontendSuggestedVfoHz").GetInt64());
            Assert.Equal("above-filter", poll.GetProperty("frontendSuggestedTuneReason").GetString());

            var recommendations = root.GetProperty("recommendations")
                .EnumerateArray()
                .Select(item => item.GetString() ?? "")
                .ToArray();
            Assert.Contains(recommendations, value => value.Contains("Read-only manual tuning hint", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(bundleDir))
            {
                Directory.Delete(bundleDir, recursive: true);
            }
        }
    }

    [SkippableFact]
    public async Task ManualTuneObserverReportsCaptureDisabledWhenMaxCapturesZero()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell manual-tune observer smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-manual-tune-observer-no-capture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            using var server = JsonRouteServer.Start(new Dictionary<string, string>
            {
                ["/api/state"] = Json(new
                {
                    status = "Connected",
                    vfoHz = 14_213_000,
                    radioLoHz = 14_208_500,
                    mode = "USB",
                    filterLowHz = 100,
                    filterHighHz = 3_152
                }),
                ["/api/radio/diagnostics/dsp-scene"] = Json(new
                {
                    status = "fresh",
                    fresh = true,
                    signalProfile = "voice-like",
                    coherentMaxSnrDb = 28.0,
                    maxSnrDb = 28.0,
                    topPeaks = new object[]
                    {
                        FrontendTopPeak(14_214_441, 1_441, 28.8, -75.6)
                    }
                }),
                ["/api/dsp/live-diagnostics"] = Json(ManualTuneObserverLiveDiagnostics())
            });

            var reportPath = Path.Combine(bundleDir, "manual-observer-no-capture.json");
            var run = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "watch-dsp-manual-tune-observer.ps1"),
                "-BaseUrl", server.BaseUrl,
                "-ReportPath", reportPath,
                "-OutputRoot", Path.Combine(bundleDir, "captures"),
                "-PollCount", "3",
                "-PollIntervalSec", "0",
                "-StablePolls", "1",
                "-MinCoherentSnrDb", "6",
                "-SceneProfilePattern", "voice",
                "-MaxCaptures", "0",
                "-RequireFrontendNearPassband",
                "-JsonOnly");

            Assert.True(run.ExitCode == 0, run.CombinedOutput);
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
            var root = doc.RootElement;
            Assert.Equal(3, root.GetProperty("pollSampleCount").GetInt32());
            Assert.Equal(0, root.GetProperty("captureCount").GetInt32());
            Assert.Equal(3, root.GetProperty("captureQualifiedPollCount").GetInt32());
            Assert.Equal(3, root.GetProperty("frontendFilterPassbandPollCount").GetInt32());

            var recommendations = root.GetProperty("recommendations")
                .EnumerateArray()
                .Select(item => item.GetString() ?? "")
                .ToArray();
            Assert.Contains(recommendations, value => value.Contains("Capture is disabled by -MaxCaptures 0", StringComparison.Ordinal));
            Assert.DoesNotContain(recommendations, value => value.Contains("No stable voice-like manual-tune VFO met the capture threshold", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(bundleDir))
            {
                Directory.Delete(bundleDir, recursive: true);
            }
        }
    }

    private static object FrontendTopPeak(long frequencyHz, int offsetHz, double snrDb, double dbfs) => new
    {
        frequencyHz,
        offsetHz,
        snrDb,
        dbfs,
        confidence = 0.9
    };

    private static object ManualTuneObserverLiveDiagnostics() => new
    {
        status = "ready-for-live-benchmark",
        requestedNrMode = "Nr5",
        effectiveNrMode = "Nr5",
        readyForNr5Tuning = true,
        runtimeEvidence = new
        {
            status = "ready",
            audioStatus = "ready",
            audioRmsDbfs = -34.0,
            audioPeakDbfs = -12.0,
            rxAudioLevelerInputRmsDbfs = -34.0,
            rxAudioLevelerOutputRmsDbfs = -34.0,
            rxAudioLevelerDesiredGainDb = 0.0,
            rxAudioLevelerAppliedGainDb = 0.0,
            rxAudioLevelerGainDeltaDb = 0.0,
            rxAudioLevelerNr5SpeechHoldBlocks = 0,
            rxAudioLevelerBoostSlewLimited = false,
            rxAudioLevelerOutputLimited = false
        },
        nr5SpnrDiagnostics = new
        {
            signalConfidence = 0.7,
            signalProbability = 0.6,
            agcGate = 0.6,
            recoveryDrive = 0.2,
            weakSignalMemory = 0.5,
            maskSmoothing = 0.3,
            inputDbfs = -34.0,
            outputDbfs = -31.0,
            outputPeakDbfs = -9.0
        }
    };

    private static string Json(object value) => JsonSerializer.Serialize(value, CamelCaseJson);

    private static async Task<ToolResult> RunPowerShellAsync(
        string powerShell,
        string workingDirectory,
        string scriptPath,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(powerShell)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {powerShell}.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        var output = await outputTask;
        var error = await errorTask;
        return new ToolResult(process.ExitCode, output, error);
    }

    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var script = Path.Combine(directory.FullName, "tools", "watch-dsp-manual-tune-observer.ps1");
            if (File.Exists(script))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not locate repository root from test output directory.");
    }

    private static string? FindPowerShell()
    {
        var systemPowerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        if (File.Exists(systemPowerShell))
        {
            return systemPowerShell;
        }

        return FindOnPath("powershell.exe")
            ?? FindOnPath("powershell")
            ?? FindOnPath("pwsh.exe")
            ?? FindOnPath("pwsh");
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            var candidate = Path.Combine(directory.Trim(), fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private sealed class JsonRouteServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly IReadOnlyDictionary<string, string> _routes;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        private JsonRouteServer(TcpListener listener, IReadOnlyDictionary<string, string> routes)
        {
            _listener = listener;
            _routes = routes;
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}";
            _loop = Task.Run(AcceptLoopAsync);
        }

        public string BaseUrl { get; }

        public static JsonRouteServer Start(IReadOnlyDictionary<string, string> routes)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new JsonRouteServer(listener, routes);
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }

            _cts.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch
                {
                    if (_cts.IsCancellationRequested)
                    {
                        break;
                    }

                    continue;
                }

                _ = Task.Run(() => HandleClientAsync(client), _cts.Token);
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using var clientRef = client;
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync();
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync()))
            {
            }

            var path = "/";
            var parts = (requestLine ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && Uri.TryCreate(parts[1], UriKind.RelativeOrAbsolute, out var uri))
            {
                path = uri.IsAbsoluteUri ? uri.AbsolutePath : uri.OriginalString.Split('?', 2)[0];
            }

            var found = _routes.TryGetValue(path, out var json);
            var body = found ? json! : "{\"error\":\"not found\"}";
            var status = found ? "200 OK" : "404 Not Found";
            var bytes = Encoding.UTF8.GetBytes(body);
            var header = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header);
            await stream.WriteAsync(bytes);
        }
    }

    private sealed record ToolResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public string CombinedOutput => $"{StandardOutput}{Environment.NewLine}{StandardError}";
    }
}
