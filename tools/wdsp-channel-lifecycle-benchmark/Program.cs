// SPDX-License-Identifier: GPL-2.0-or-later
//
// WDSP channel lifecycle evidence producer. This is an offline benchmark tool:
// it exercises Zeus's WdspDspEngine wrapper only and never talks to a radio.

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Zeus.Contracts;
using Zeus.Dsp;
using Zeus.Dsp.Wdsp;

return WdspChannelLifecycleBenchmarkTool.Run(args);

internal static class WdspChannelLifecycleBenchmarkTool
{
    private const int SampleRateHz = 192_000;
    private const int PixelWidth = 2048;
    private const int TotalComplexSamples = 32 * 1024;
    private const int FeedChunkComplexSamples = 126;
    private const double TestToneHz = 1_500.0;
    private const double TestToneAmplitude = 0.3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static int Run(string[] args)
    {
        var options = ToolOptions.Parse(args);
        if (options.ShowHelp)
        {
            Console.WriteLine(ToolOptions.HelpText);
            return 0;
        }

        var transitions = new List<TransitionRecord>();
        var logger = new CaptureLogger<WdspDspEngine>();
        var nativeRuntime = CaptureNativeRuntimeIdentity();
        var summary = new LifecycleSummary();
        int nativeExceptionCount = 0;
        WdspDspEngine? engine = null;
        int? rxChannel = null;

        try
        {
            engine = new WdspDspEngine(logger);
            double[] iq = GenerateToneIq();

            rxChannel = Measure(transitions, "rx-open-channel", "RXA OpenChannel", -1, () =>
                engine.OpenChannel(SampleRateHz, PixelWidth));

            Measure(transitions, "rx-configure-channel", "RXA mode/filter/AGC setup", -1, () =>
            {
                engine.SetMode(rxChannel.Value, RxMode.USB);
                engine.SetFilter(rxChannel.Value, 150, 2850);
                engine.SetAgcTop(rxChannel.Value, 80.0);
                engine.SetNoiseReduction(rxChannel.Value, new NrConfig());
                engine.SetZoom(rxChannel.Value, 1);
            });

            int txChannel = Measure(transitions, "tx-open-channel", "TXA OpenTxChannel", -1, () =>
                engine.OpenTxChannel(outputRateHz: 48_000));
            summary.TxChannelId = txChannel;

            for (int cycle = 0; cycle < options.Cycles; cycle++)
            {
                summary.CycleCount++;
                FeedIq(engine, rxChannel.Value, iq);
                int preMoxDrained = Measure(transitions, "rx-drain-before-mox", "RXA ReadAudio before MOX", cycle, () =>
                    DrainAudio(engine, rxChannel.Value, minSamples: 1024),
                    static (record, drained) => record.AudioSamples = drained);
                summary.PreMoxAudioDrainSamples += preMoxDrained;

                var preMeters = Measure(transitions, "rx-meter-before-mox", "RXA meter sample before MOX", cycle, () =>
                    ReadRxMetersUntilEscaped(engine, rxChannel.Value),
                    static (record, meters) => record.RxMeters = meters);
                if (!preMeters.EscapedSentinel)
                    summary.MeterEscapeCount++;

                Measure(transitions, "set-mox-on", "SetMox(true)", cycle, () => engine.SetMox(true));
                Thread.Sleep(50);
                Measure(transitions, "set-mox-off", "SetMox(false)", cycle, () => engine.SetMox(false));

                FeedIq(engine, rxChannel.Value, iq);
                int postMoxDrained = Measure(transitions, "rx-drain-after-mox", "RXA ReadAudio after MOX", cycle, () =>
                    DrainAudio(engine, rxChannel.Value, minSamples: 1024),
                    static (record, drained) => record.AudioSamples = drained);
                summary.PostMoxAudioDrainSamples += postMoxDrained;

                var postMeters = Measure(transitions, "rx-meter-after-mox", "RXA meter sample after MOX", cycle, () =>
                    ReadRxMetersUntilEscaped(engine, rxChannel.Value),
                    static (record, meters) => record.RxMeters = meters);
                if (!postMeters.EscapedSentinel)
                    summary.MeterEscapeCount++;

                var txMeters = Measure(transitions, "tx-meter-after-mox", "TXA stage meter sample after MOX cycle", cycle, () =>
                    TxMeterSnapshot.From(engine.GetTxStageMeters()),
                    static (record, meters) => record.TxMeters = meters);
                if (!txMeters.SilentOrTicked)
                    summary.MeterEscapeCount++;
            }

            int closeTarget = rxChannel.Value;
            Measure(transitions, "rx-close-channel", "RXA CloseChannel", -1, () => engine.CloseChannel(closeTarget));
            rxChannel = null;

            int staleAudioAfterClose = Measure(transitions, "rx-drain-after-close", "RXA ReadAudio after CloseChannel", -1, () =>
                DrainAudio(engine, closeTarget, minSamples: 1, maxAttempts: 1),
                static (record, drained) => record.AudioSamples = drained);
            summary.StaleAudioAfterCloseSamples = staleAudioAfterClose;
            if (staleAudioAfterClose > 0)
                summary.AudioDrainFailureCount++;

            var closedMeters = Measure(transitions, "rx-meter-after-close", "RXA meter sample after CloseChannel", -1, () =>
                MeterSnapshot.From(engine.GetRxStageMeters(closeTarget)),
                static (record, meters) => record.RxMeters = meters);
            if (!closedMeters.Silent)
                summary.MeterEscapeCount++;

            rxChannel = Measure(transitions, "rx-reopen-channel", "RXA OpenChannel after close", -1, () =>
                engine.OpenChannel(SampleRateHz, PixelWidth));
            Measure(transitions, "rx-reconfigure-channel", "RXA mode/filter/AGC setup after reopen", -1, () =>
            {
                engine.SetMode(rxChannel.Value, RxMode.USB);
                engine.SetFilter(rxChannel.Value, 150, 2850);
                engine.SetAgcTop(rxChannel.Value, 80.0);
                engine.SetNoiseReduction(rxChannel.Value, new NrConfig());
            });

            FeedIq(engine, rxChannel.Value, iq);
            int reopenedDrain = Measure(transitions, "rx-drain-after-reopen", "RXA ReadAudio after reopen", -1, () =>
                DrainAudio(engine, rxChannel.Value, minSamples: 1024),
                static (record, drained) => record.AudioSamples = drained);
            summary.ReopenAudioDrainSamples = reopenedDrain;
        }
        catch (Exception ex)
        {
            nativeExceptionCount++;
            transitions.Add(TransitionRecord.Failed("native-exception", "Unhandled WDSP lifecycle exception", -1, ex));
        }
        finally
        {
            if (engine is not null)
            {
                if (rxChannel is int id)
                {
                    try
                    {
                        engine.CloseChannel(id);
                    }
                    catch (Exception ex)
                    {
                        nativeExceptionCount++;
                        transitions.Add(TransitionRecord.Failed("rx-final-close-channel", "RXA final CloseChannel", -1, ex));
                    }
                }

                engine.Dispose();
            }
        }

        int transitionFailureCount = transitions.Count(static transition => !transition.Success);
        summary.AudioDrainSamples = summary.PreMoxAudioDrainSamples + summary.PostMoxAudioDrainSamples + summary.ReopenAudioDrainSamples;
        summary.AudioDrainFailureCount += transitions.Count(static transition =>
            (transition.StageId == "rx-drain-before-mox" ||
             transition.StageId == "rx-drain-after-mox" ||
             transition.StageId == "rx-drain-after-reopen") &&
            (transition.AudioSamples ?? 0) <= 0);

        bool stateTransitionSuccess =
            transitionFailureCount == 0 &&
            summary.AudioDrainFailureCount == 0 &&
            summary.StaleAudioAfterCloseSamples == 0;
        int gateFailureCount =
            (stateTransitionSuccess ? 0 : 1) +
            summary.MeterEscapeCount +
            nativeExceptionCount;

        string status = nativeRuntime.Status != "found"
            ? "native-runtime-not-found"
            : gateFailureCount == 0
                ? "ready"
                : "lifecycle-gate-failed";

        var report = new
        {
            schemaVersion = 1,
            tool = "run-dsp-wdsp-channel-lifecycle",
            evidenceKind = "wdsp-channel-lifecycle-json",
            scenarioId = "wdsp-channel-lifecycle",
            generatedUtc = DateTimeOffset.UtcNow,
            wdspRuntimeRid = nativeRuntime.Rid,
            wdspRuntimePath = nativeRuntime.Path,
            wdspRuntimePathKind = nativeRuntime.PathKind,
            wdspRuntimeFileName = nativeRuntime.FileName,
            wdspRuntimeLength = nativeRuntime.Length,
            wdspRuntimeSha256 = nativeRuntime.Sha256,
            wdspRuntimeStatus = nativeRuntime.Status,
            sampleRateHz = SampleRateHz,
            pixelWidth = PixelWidth,
            cycleCountRequested = options.Cycles,
            cycleCount = summary.CycleCount,
            transitionCount = transitions.Count,
            transitionFailureCount,
            stateTransitionSuccess,
            nativeExceptionCount,
            meterEscapeCount = summary.MeterEscapeCount,
            audioDrainSamples = summary.AudioDrainSamples,
            preMoxAudioDrainSamples = summary.PreMoxAudioDrainSamples,
            postMoxAudioDrainSamples = summary.PostMoxAudioDrainSamples,
            reopenAudioDrainSamples = summary.ReopenAudioDrainSamples,
            staleAudioAfterCloseSamples = summary.StaleAudioAfterCloseSamples,
            audioDrainFailureCount = summary.AudioDrainFailureCount,
            lifecycleGateFailureCount = gateFailureCount,
            readyForReview = status == "ready",
            status,
            defaultBehaviorChanged = false,
            transitions,
            capturedLogs = logger.Records,
            limitations = new[]
            {
                "This report exercises Zeus WdspDspEngine wrapper lifecycle calls offline only.",
                "It does not prove G2 live transport, on-air receive/transmit behavior, PureSignal safety, or cross-radio acceptance.",
                "SetChannelState prior values are preserved through captured WdspDspEngine SetMox logs until native structured lifecycle probes exist.",
            },
        };

        string json = JsonSerializer.Serialize(report, JsonOptions);
        if (!string.IsNullOrWhiteSpace(options.OutputPath))
        {
            string outputPath = Path.GetFullPath(options.OutputPath);
            if (File.Exists(outputPath) && !options.Force)
                throw new InvalidOperationException($"Output file already exists: {outputPath} (use --force to overwrite)");

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Environment.CurrentDirectory);
            File.WriteAllText(outputPath, json);

            if (!options.JsonOnly)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    tool = "run-dsp-wdsp-channel-lifecycle",
                    outputPath,
                    sha256 = Sha256(outputPath),
                    status,
                    readyForReview = status == "ready",
                    transitionCount = transitions.Count,
                    transitionFailureCount,
                    nativeExceptionCount,
                    meterEscapeCount = summary.MeterEscapeCount,
                    audioDrainFailureCount = summary.AudioDrainFailureCount,
                    lifecycleGateFailureCount = gateFailureCount,
                }, JsonOptions));
            }
        }

        if (options.JsonOnly || string.IsNullOrWhiteSpace(options.OutputPath))
            Console.WriteLine(json);

        return options.FailOnGate && status != "ready" ? 1 : 0;
    }

    private static double[] GenerateToneIq()
    {
        var iq = new double[TotalComplexSamples * 2];
        for (int n = 0; n < TotalComplexSamples; n++)
        {
            double phase = 2.0 * Math.PI * TestToneHz * n / SampleRateHz;
            iq[2 * n] = TestToneAmplitude * Math.Cos(phase);
            iq[2 * n + 1] = TestToneAmplitude * Math.Sin(phase);
        }

        return iq;
    }

    private static void FeedIq(WdspDspEngine engine, int channel, double[] iq)
    {
        for (int offset = 0; offset < TotalComplexSamples; offset += FeedChunkComplexSamples)
        {
            int take = Math.Min(FeedChunkComplexSamples, TotalComplexSamples - offset);
            engine.FeedIq(channel, iq.AsSpan(2 * offset, 2 * take));
        }
    }

    private static int DrainAudio(WdspDspEngine engine, int channel, int minSamples, int maxAttempts = 80)
    {
        var buffer = new float[2048];
        int total = 0;
        for (int attempt = 0; attempt < maxAttempts && total < minSamples; attempt++)
        {
            if (attempt > 0)
                Thread.Sleep(10);

            total += engine.ReadAudio(channel, buffer);
        }

        return total;
    }

    private static MeterSnapshot ReadRxMetersUntilEscaped(WdspDspEngine engine, int channel, int maxAttempts = 20)
    {
        MeterSnapshot last = MeterSnapshot.From(engine.GetRxStageMeters(channel));
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            last = MeterSnapshot.From(engine.GetRxStageMeters(channel), attempt);
            if (last.EscapedSentinel)
                return last;

            Thread.Sleep(10);
        }

        return last;
    }

    private static void Measure(List<TransitionRecord> transitions, string stageId, string label, int cycle, Action action) =>
        Measure(transitions, stageId, label, cycle, () =>
        {
            action();
            return 0;
        });

    private static T Measure<T>(
        List<TransitionRecord> transitions,
        string stageId,
        string label,
        int cycle,
        Func<T> action,
        Action<TransitionRecord, T>? enrich = null)
    {
        var record = new TransitionRecord
        {
            StageId = stageId,
            Label = label,
            Cycle = cycle,
        };
        var sw = Stopwatch.StartNew();
        try
        {
            T result = action();
            record.Success = true;
            enrich?.Invoke(record, result);
            return result;
        }
        catch (Exception ex)
        {
            record.Success = false;
            record.ExceptionType = ex.GetType().FullName;
            record.ExceptionMessage = ex.Message;
            throw;
        }
        finally
        {
            sw.Stop();
            record.ElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 6);
            transitions.Add(record);
        }
    }

    private static NativeRuntimeIdentity CaptureNativeRuntimeIdentity()
    {
        string rid = CurrentRid();
        string fileName = NativeFileName();

        foreach (string candidate in CandidateNativePaths(typeof(WdspDspEngine).Assembly, rid, fileName).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(candidate))
                continue;

            var info = new FileInfo(candidate);
            return new NativeRuntimeIdentity(
                Rid: rid,
                FileName: fileName,
                Path: ToRuntimeIdentityPath(candidate, out string pathKind),
                PathKind: pathKind,
                Length: info.Length,
                Sha256: HashFileSha256(candidate),
                Status: "found");
        }

        return new NativeRuntimeIdentity(
            Rid: rid,
            FileName: fileName,
            Path: string.Empty,
            PathKind: "not-found",
            Length: 0,
            Sha256: string.Empty,
            Status: "not-found");
    }

    private static IEnumerable<string> CandidateNativePaths(Assembly assembly, string rid, string fileName)
    {
        string? asmDir = Path.GetDirectoryName(assembly.Location);
        if (!string.IsNullOrEmpty(asmDir))
        {
            yield return Path.Combine(asmDir, "runtimes", rid, "native", fileName);
            yield return Path.Combine(asmDir, fileName);
        }

        string baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "runtimes", rid, "native", fileName);
        yield return Path.Combine(baseDir, fileName);
    }

    private static string CurrentRid()
    {
        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "x64",
        };
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return $"osx-{arch}";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return $"linux-{arch}";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return $"win-{arch}";
        return $"unknown-{arch}";
    }

    private static string NativeFileName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "libwdsp.dylib";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "libwdsp.so";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "wdsp.dll";
        return "libwdsp";
    }

    private static string ToRuntimeIdentityPath(string path, out string pathKind)
    {
        string fullPath = Path.GetFullPath(path);
        string baseDir = Path.GetFullPath(AppContext.BaseDirectory);
        if (IsSubPathOf(baseDir, fullPath))
        {
            pathKind = "app-output-relative";
            return Path.GetRelativePath(baseDir, fullPath).Replace('\\', '/');
        }

        pathKind = "absolute";
        return fullPath;
    }

    private static bool IsSubPathOf(string root, string path)
    {
        string normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string HashFileSha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private sealed class LifecycleSummary
    {
        public int CycleCount { get; set; }
        public int TxChannelId { get; set; }
        public int PreMoxAudioDrainSamples { get; set; }
        public int PostMoxAudioDrainSamples { get; set; }
        public int ReopenAudioDrainSamples { get; set; }
        public int AudioDrainSamples { get; set; }
        public int StaleAudioAfterCloseSamples { get; set; }
        public int AudioDrainFailureCount { get; set; }
        public int MeterEscapeCount { get; set; }
    }

    private sealed class TransitionRecord
    {
        public string StageId { get; init; } = "";
        public string Label { get; init; } = "";
        public int Cycle { get; init; }
        public bool Success { get; set; }
        public double ElapsedMs { get; set; }
        public int? AudioSamples { get; set; }
        public MeterSnapshot? RxMeters { get; set; }
        public TxMeterSnapshot? TxMeters { get; set; }
        public string? ExceptionType { get; set; }
        public string? ExceptionMessage { get; set; }

        public static TransitionRecord Failed(string stageId, string label, int cycle, Exception ex) =>
            new()
            {
                StageId = stageId,
                Label = label,
                Cycle = cycle,
                Success = false,
                ExceptionType = ex.GetType().FullName,
                ExceptionMessage = ex.Message,
            };
    }

    private sealed record MeterSnapshot(
        float SignalPk,
        float SignalAv,
        float AdcPk,
        float AdcAv,
        float AgcGain,
        float AgcEnvPk,
        float AgcEnvAv,
        bool EscapedSentinel,
        bool Silent,
        int AttemptCount)
    {
        public static MeterSnapshot From(RxStageMeters meters, int attemptCount = 1)
        {
            bool escaped = meters.SignalPk > -399.0f &&
                meters.SignalAv > -399.0f &&
                meters.AdcPk > -399.0f &&
                meters.AdcAv > -399.0f &&
                meters.AgcGain > -300.0f &&
                meters.AgcEnvPk > -399.0f &&
                meters.AgcEnvAv > -399.0f;
            bool silent = meters.SignalPk <= -199.0f &&
                meters.SignalAv <= -199.0f &&
                meters.AdcPk <= -199.0f &&
                meters.AdcAv <= -199.0f &&
                Math.Abs(meters.AgcGain) <= 0.0001f &&
                meters.AgcEnvPk <= -199.0f &&
                meters.AgcEnvAv <= -199.0f;

            return new MeterSnapshot(
                FiniteDb(meters.SignalPk),
                FiniteDb(meters.SignalAv),
                FiniteDb(meters.AdcPk),
                FiniteDb(meters.AdcAv),
                FiniteDb(meters.AgcGain),
                FiniteDb(meters.AgcEnvPk),
                FiniteDb(meters.AgcEnvAv),
                escaped,
                silent,
                attemptCount);
        }
    }

    private sealed record TxMeterSnapshot(
        float MicPk,
        float OutPk,
        bool SilentOrTicked)
    {
        public static TxMeterSnapshot From(TxStageMeters meters)
        {
            bool silentOrTicked = meters.MicPk <= -199.0f ||
                meters.OutPk > -399.0f;
            return new TxMeterSnapshot(FiniteDb(meters.MicPk), FiniteDb(meters.OutPk), silentOrTicked);
        }
    }

    private static float FiniteDb(float value) =>
        float.IsNaN(value) || float.IsInfinity(value) ? -400.0f : value;

    private sealed record LogRecord(
        string Level,
        int EventId,
        string Message,
        string? ExceptionType,
        string? ExceptionMessage);

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        private const int MaxRecords = 200;
        private readonly object _gate = new();
        private readonly List<LogRecord> _records = new();

        public IReadOnlyList<LogRecord> Records
        {
            get
            {
                lock (_gate)
                    return _records.ToArray();
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (formatter is null)
                return;

            lock (_gate)
            {
                if (_records.Count >= MaxRecords)
                    return;

                _records.Add(new LogRecord(
                    logLevel.ToString(),
                    eventId.Id,
                    formatter(state, exception),
                    exception?.GetType().FullName,
                    exception?.Message));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose()
            {
            }
        }
    }

    private sealed record NativeRuntimeIdentity(
        string Rid,
        string FileName,
        string Path,
        string PathKind,
        long Length,
        string Sha256,
        string Status);

    private sealed record ToolOptions(
        string OutputPath,
        int Cycles,
        bool Force,
        bool FailOnGate,
        bool JsonOnly,
        bool ShowHelp)
    {
        public const string HelpText =
            """
            Usage: wdsp-channel-lifecycle-benchmark [options]

              --output-path <path>   Write the lifecycle report JSON to this path.
              --cycles <count>       Number of MOX on/off cycles to exercise. Default: 3.
              --force                Overwrite an existing output file.
              --fail-on-gate         Return exit code 1 when lifecycle gates fail.
              --json-only            Write only the lifecycle report JSON to stdout.
              --help                 Show this help text.
            """;

        public static ToolOptions Parse(string[] args)
        {
            string outputPath = string.Empty;
            int cycles = 3;
            bool force = false;
            bool failOnGate = false;
            bool jsonOnly = false;
            bool showHelp = false;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg)
                {
                    case "--output-path":
                    case "-o":
                        outputPath = RequiredValue(args, ref i, arg);
                        break;
                    case "--cycles":
                    case "-c":
                        string cycleText = RequiredValue(args, ref i, arg);
                        if (!int.TryParse(cycleText, out cycles) || cycles <= 0 || cycles > 25)
                            throw new ArgumentException("--cycles must be between 1 and 25.");
                        break;
                    case "--force":
                        force = true;
                        break;
                    case "--fail-on-gate":
                        failOnGate = true;
                        break;
                    case "--json-only":
                        jsonOnly = true;
                        break;
                    case "--help":
                    case "-h":
                    case "/?":
                        showHelp = true;
                        break;
                    default:
                        throw new ArgumentException($"Unknown argument '{arg}'. Use --help for usage.");
                }
            }

            return new ToolOptions(outputPath, cycles, force, failOnGate, jsonOnly, showHelp);
        }

        private static string RequiredValue(string[] args, ref int index, string name)
        {
            if (index + 1 >= args.Length)
                throw new ArgumentException($"Missing value for {name}.");
            index++;
            string value = args[index];
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"Missing value for {name}.");
            return value;
        }
    }
}
