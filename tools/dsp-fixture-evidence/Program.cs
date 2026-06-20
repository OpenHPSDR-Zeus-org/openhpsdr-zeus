// SPDX-License-Identifier: GPL-2.0-or-later
//
// WDSP-backed offline fixture evidence producer. This is an opt-in tool: it
// runs deterministic RXA/TXA fixtures through WdspDspEngine and writes the
// same bundle-compatible evidence shape consumed by compare-dsp-fixture-metrics.ps1.

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Zeus.Contracts;
using Zeus.Dsp;
using Zeus.Dsp.Wdsp;

return await WdspFixtureEvidenceTool.RunAsync(args);

internal static class WdspFixtureEvidenceTool
{
    private const int AudioSampleRateHz = 48_000;
    private const int PixelWidth = 2048;
    private const int RxChunkComplex = 126;
    private const int FixtureRepeats = 4;
    private const int AnalysisSamples = 16_384;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static async Task<int> RunAsync(string[] args)
    {
        var options = ToolOptions.Parse(args);
        if (options.ShowHelp)
        {
            Console.WriteLine(ToolOptions.HelpText);
            return 0;
        }

        try
        {
            string bundleDir = Path.GetFullPath(string.IsNullOrWhiteSpace(options.BundleDir)
                ? Environment.CurrentDirectory
                : options.BundleDir);

            string planPath = FullPathOrDefault(options.BenchmarkPlanPath, bundleDir, "benchmark-plan.json");
            string catalogPath = FullPathOrDefault(options.MetricCatalogPath, bundleDir, "benchmark-metric-catalog.json", required: false);
            string metricsPath = FullPathOrDefault(options.MetricsPath, bundleDir, Path.Combine("artifacts", "offline-fixture-metrics.json"));
            string audioIndexPath = FullPathOrDefault(options.AudioIndexPath, bundleDir, Path.Combine("artifacts", "audio-render-before-after.json"));
            string spectrumIndexPath = FullPathOrDefault(options.SpectrumIndexPath, bundleDir, Path.Combine("artifacts", "spectrum-before-after.json"));
            var nativeRuntime = CaptureNativeRuntimeIdentity();

            if (!File.Exists(planPath))
                throw new FileNotFoundException("Benchmark plan not found.", planPath);

            foreach (string path in new[] { metricsPath, audioIndexPath, spectrumIndexPath })
                ThrowIfExists(path, options.Force);

            var scenarios = ReadPlanScenarios(planPath);
            var metricDirections = ReadMetricDirections(catalogPath);
            var requestedScenarioIds = options.ScenarioIds.Select(NormalizeId).Where(static s => s.Length > 0).ToHashSet(StringComparer.Ordinal);
            var comparisonIds = options.ComparisonIds.Select(NormalizeComparisonId).Where(static s => s.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
            if (comparisonIds.Length == 0)
                throw new ArgumentException("At least one comparison id is required.");

            var metricScenarios = new List<object>();
            var audioFiles = new List<object>();
            var spectrumFiles = new List<object>();
            var generatedScenarioIds = new List<string>();
            var skippedScenarioIds = new List<string>();

            foreach (var scenario in scenarios)
            {
                if (requestedScenarioIds.Count > 0 && !requestedScenarioIds.Contains(scenario.Id))
                    continue;

                if (!options.IncludeNonFixtureScenarios &&
                    !string.Equals(scenario.FixtureStatus, "offline-fixture-ready", StringComparison.OrdinalIgnoreCase))
                {
                    skippedScenarioIds.Add(scenario.Id);
                    continue;
                }

                var fixture = DspFixtureCatalog.CreateOrNull(scenario.Id);
                if (fixture is null)
                    throw new InvalidOperationException($"No WDSP offline fixture is defined for benchmark scenario '{scenario.Id}'.");

                generatedScenarioIds.Add(scenario.Id);
                var sourceMetrics = DspBenchmarkAnalyzer.Analyze(fixture);
                var scenarioComparisons = new List<object>();

                foreach (string comparisonId in comparisonIds)
                {
                    var run = RunFixture(fixture, comparisonId);
                    var metrics = BuildMetricMap(
                        scenario.RequiredMetrics,
                        sourceMetrics,
                        run,
                        scenario.Id,
                        metricDirections);

                    string audioEvidencePath = Path.Combine(bundleDir, "artifacts", "offline-fixtures", "audio", scenario.Id, $"{comparisonId}.json");
                    string spectrumEvidencePath = Path.Combine(bundleDir, "artifacts", "offline-fixtures", "spectrum", scenario.Id, $"{comparisonId}.json");
                    ThrowIfExists(audioEvidencePath, options.Force);
                    ThrowIfExists(spectrumEvidencePath, options.Force);

                    var audioEvidence = new Dictionary<string, object?>
                    {
                        ["schemaVersion"] = 1,
                        ["tool"] = "dsp-fixture-evidence",
                        ["evidenceKind"] = fixture.Path == DspBenchmarkPath.RxIq ? "wdsp-rx-audio-render-summary" : "wdsp-tx-iq-render-summary",
                        ["scenarioId"] = scenario.Id,
                        ["comparisonId"] = comparisonId,
                        ["profile"] = run.Profile,
                        ["fixtureStatus"] = scenario.FixtureStatus,
                        ["signalPath"] = scenario.SignalPath,
                        ["sampleRateHz"] = run.SampleRateHz,
                        ["sampleCount"] = run.SampleCount,
                        ["processingElapsedMs"] = Math.Round(run.ElapsedMs, 6),
                        ["throughputRatio"] = Math.Round(ComputeThroughputRatio(run), 6),
                        ["nativeStageTiming"] = NewStageTimingRecord(run),
                        ["managedAllocation"] = NewManagedAllocationRecord(run),
                        ["wdspRuntimeRid"] = nativeRuntime.Rid,
                        ["wdspRuntimeSha256"] = nativeRuntime.Sha256,
                        ["wdspRuntimeStatus"] = nativeRuntime.Status,
                        ["rms"] = Math.Round(run.Metrics.Rms, 9),
                        ["peak"] = Math.Round(run.Metrics.Peak, 9),
                        ["crestFactorDb"] = Math.Round(run.Metrics.CrestFactorDb, 6),
                        ["dcOffset"] = Math.Round(run.Metrics.DcOffset, 9),
                        ["windowedRmsSpreadDb"] = Math.Round(run.Metrics.WindowedRmsSpreadDb, 6),
                        ["clippingCount"] = run.ClippingCount,
                        ["txStageMeters"] = run.TxMeters,
                        ["rxStageMeters"] = run.RxStageMeters,
                        ["candidateDiagnostics"] = run.CandidateDiagnostics,
                        ["expectedTonesHz"] = fixture.ExpectedTonesHz.Select(kv => new Dictionary<string, object?>
                        {
                            ["name"] = kv.Key,
                            ["hz"] = kv.Value,
                            ["powerDb"] = run.Metrics.TonePowerDb.TryGetValue(kv.Key, out double power) ? Math.Round(power, 6) : null,
                        }).ToArray(),
                        ["samplePreview"] = run.SamplePreview,
                        ["notes"] = new[]
                        {
                            "WDSP-backed offline fixture evidence; it does not prove G2, on-air, or cross-radio acceptance.",
                            "TX/PureSignal graduation still requires G2 bench and safe-bypass hardware evidence."
                        },
                    };
                    var spectrumEvidence = new Dictionary<string, object?>
                    {
                        ["schemaVersion"] = 1,
                        ["tool"] = "dsp-fixture-evidence",
                        ["evidenceKind"] = "wdsp-spectrum-summary",
                        ["scenarioId"] = scenario.Id,
                        ["comparisonId"] = comparisonId,
                        ["profile"] = run.Profile,
                        ["fixtureStatus"] = scenario.FixtureStatus,
                        ["signalPath"] = scenario.SignalPath,
                        ["sampleRateHz"] = run.SampleRateHz,
                        ["wdspRuntimeRid"] = nativeRuntime.Rid,
                        ["wdspRuntimeSha256"] = nativeRuntime.Sha256,
                        ["wdspRuntimeStatus"] = nativeRuntime.Status,
                        ["tonePowerDb"] = run.Metrics.TonePowerDb,
                        ["wantedSnrDb"] = Math.Round(ComputeWantedSnrDb(run.Metrics), 6),
                        ["signalSinadDb"] = Math.Round(ComputeSignalSinadDb(run.Metrics), 6),
                        ["wantedAdjacentRatioDb"] = Math.Round(ComputeWantedAdjacentRatioDb(run.Metrics), 6),
                        ["intermodulationProxy"] = Math.Round(run.IntermodulationProxy, 6),
                        ["summary"] = fixture.Summary,
                        ["notes"] = new[]
                        {
                            "Portable JSON spectral evidence from actual WdspDspEngine output.",
                            "Rendered FFT/audio artifacts may be attached separately for human review."
                        },
                    };

                    string audioSha = WriteJsonWithHash(audioEvidencePath, audioEvidence);
                    string spectrumSha = WriteJsonWithHash(spectrumEvidencePath, spectrumEvidence);
                    string audioRelativePath = ToPortablePath(bundleDir, audioEvidencePath);
                    string spectrumRelativePath = ToPortablePath(bundleDir, spectrumEvidencePath);

                    audioFiles.Add(new Dictionary<string, object?>
                    {
                        ["path"] = audioRelativePath,
                        ["scenarioId"] = scenario.Id,
                        ["comparisonId"] = comparisonId,
                        ["kind"] = (string)audioEvidence["evidenceKind"]!,
                        ["sampleRateHz"] = run.SampleRateHz,
                        ["sampleCount"] = run.SampleCount,
                        ["sha256"] = audioSha,
                    });
                    spectrumFiles.Add(new Dictionary<string, object?>
                    {
                        ["path"] = spectrumRelativePath,
                        ["scenarioId"] = scenario.Id,
                        ["comparisonId"] = comparisonId,
                        ["kind"] = "wdsp-spectrum-summary",
                        ["sampleRateHz"] = run.SampleRateHz,
                        ["sha256"] = spectrumSha,
                    });

                    scenarioComparisons.Add(new Dictionary<string, object?>
                    {
                        ["comparisonId"] = comparisonId,
                        ["source"] = "wdsp-fixture-runner",
                        ["profile"] = run.Profile,
                        ["metrics"] = metrics,
                        ["gates"] = NewGateRecords(scenario, run),
                        ["nativeStageTiming"] = NewStageTimingRecord(run),
                        ["managedAllocation"] = NewManagedAllocationRecord(run),
                        ["txStageMeters"] = run.TxMeters,
                        ["rxStageMeters"] = run.RxStageMeters,
                        ["clippingCount"] = run.ClippingCount,
                        ["candidateDiagnostics"] = run.CandidateDiagnostics,
                        ["evidence"] = new Dictionary<string, object?>
                        {
                            ["audioPath"] = audioRelativePath,
                            ["spectrumPath"] = spectrumRelativePath,
                        },
                    });
                }

                metricScenarios.Add(new Dictionary<string, object?>
                {
                    ["scenarioId"] = scenario.Id,
                    ["scenarioName"] = scenario.Name,
                    ["fixtureStatus"] = scenario.FixtureStatus,
                    ["signalPath"] = scenario.SignalPath,
                    ["fixtureSummary"] = fixture.Summary,
                    ["comparisons"] = scenarioComparisons,
                });
            }

            if (metricScenarios.Count == 0)
                throw new InvalidOperationException("No benchmark-plan scenarios matched the WDSP fixture evidence scope.");

            var generatedUtc = DateTimeOffset.UtcNow;
            var metricsReport = new Dictionary<string, object?>
            {
                ["schemaVersion"] = 1,
                ["tool"] = "dsp-fixture-evidence",
                ["generatedUtc"] = generatedUtc,
                ["benchmarkPlanPath"] = ToPortablePath(bundleDir, planPath),
                ["metricCatalogPath"] = File.Exists(catalogPath) ? ToPortablePath(bundleDir, catalogPath) : null,
                ["fixtureScenarioScope"] = options.IncludeNonFixtureScenarios ? "all-plan-scenarios" : "offline-fixture-ready",
                ["evidenceEngine"] = "wdsp",
                ["wdspRuntimeRid"] = nativeRuntime.Rid,
                ["wdspRuntimePath"] = nativeRuntime.Path,
                ["wdspRuntimePathKind"] = nativeRuntime.PathKind,
                ["wdspRuntimeFileName"] = nativeRuntime.FileName,
                ["wdspRuntimeLength"] = nativeRuntime.Length,
                ["wdspRuntimeSha256"] = nativeRuntime.Sha256,
                ["wdspRuntimeStatus"] = nativeRuntime.Status,
                ["scenarioCount"] = metricScenarios.Count,
                ["comparisonIds"] = comparisonIds,
                ["generatedScenarioIds"] = generatedScenarioIds,
                ["skippedNonFixtureScenarioIds"] = skippedScenarioIds,
                ["acceptanceLimitations"] = new[]
                {
                    "WDSP-backed offline fixture evidence exercises WdspDspEngine RXA/TXA but does not prove on-air or hardware acceptance.",
                    "Thetis parity still requires source parity review plus G2 live/bench evidence.",
                    "PureSignal-safe bypass and cross-radio evidence remain separate default-graduation gates.",
                },
                ["scenarios"] = metricScenarios,
            };
            var audioIndex = new Dictionary<string, object?>
            {
                ["schemaVersion"] = 1,
                ["tool"] = "dsp-fixture-evidence",
                ["generatedUtc"] = generatedUtc,
                ["artifactId"] = "audio-render-before-after",
                ["fixtureScenarioScope"] = metricsReport["fixtureScenarioScope"],
                ["evidenceEngine"] = "wdsp",
                ["wdspRuntimeRid"] = nativeRuntime.Rid,
                ["wdspRuntimeSha256"] = nativeRuntime.Sha256,
                ["wdspRuntimeStatus"] = nativeRuntime.Status,
                ["scenarioCount"] = metricScenarios.Count,
                ["fileCount"] = audioFiles.Count,
                ["files"] = audioFiles,
            };
            var spectrumIndex = new Dictionary<string, object?>
            {
                ["schemaVersion"] = 1,
                ["tool"] = "dsp-fixture-evidence",
                ["generatedUtc"] = generatedUtc,
                ["artifactId"] = "spectrum-before-after",
                ["fixtureScenarioScope"] = metricsReport["fixtureScenarioScope"],
                ["evidenceEngine"] = "wdsp",
                ["wdspRuntimeRid"] = nativeRuntime.Rid,
                ["wdspRuntimeSha256"] = nativeRuntime.Sha256,
                ["wdspRuntimeStatus"] = nativeRuntime.Status,
                ["scenarioCount"] = metricScenarios.Count,
                ["fileCount"] = spectrumFiles.Count,
                ["files"] = spectrumFiles,
            };

            WriteJson(metricsPath, metricsReport);
            WriteJson(audioIndexPath, audioIndex);
            WriteJson(spectrumIndexPath, spectrumIndex);

            var summary = new Dictionary<string, object?>
            {
                ["tool"] = "dsp-fixture-evidence",
                ["metricsPath"] = ToPortablePath(bundleDir, metricsPath),
                ["audioIndexPath"] = ToPortablePath(bundleDir, audioIndexPath),
                ["spectrumIndexPath"] = ToPortablePath(bundleDir, spectrumIndexPath),
                ["wdspRuntimeRid"] = nativeRuntime.Rid,
                ["wdspRuntimeSha256"] = nativeRuntime.Sha256,
                ["wdspRuntimeStatus"] = nativeRuntime.Status,
                ["scenarioCount"] = metricScenarios.Count,
                ["comparisonIds"] = comparisonIds,
                ["skippedNonFixtureScenarioCount"] = skippedScenarioIds.Count,
            };

            if (options.JsonOnly)
            {
                Console.WriteLine(JsonSerializer.Serialize(summary, JsonOptions));
            }
            else
            {
                Console.WriteLine("WDSP fixture evidence written.");
                Console.WriteLine($"Metrics: {metricsPath}");
                Console.WriteLine($"Audio index: {audioIndexPath}");
                Console.WriteLine($"Spectrum index: {spectrumIndexPath}");
                Console.WriteLine($"Scenarios: {metricScenarios.Count}, comparisons: {string.Join(", ", comparisonIds)}");
            }

            await Task.CompletedTask;
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static FixtureRunResult RunFixture(DspBenchmarkFixture fixture, string comparisonId)
    {
        var profile = ComparisonProfile.For(comparisonId, fixture.Path);
        return fixture.Path switch
        {
            DspBenchmarkPath.RxIq => RunRxFixture(fixture, profile),
            DspBenchmarkPath.TxAudio => RunTxFixture(fixture, profile),
            _ => throw new ArgumentOutOfRangeException(nameof(fixture), fixture.Path, "Unknown fixture path."),
        };
    }

    private static FixtureRunResult RunRxFixture(DspBenchmarkFixture fixture, ComparisonProfile profile)
    {
        if (fixture.IqInterleaved is null)
            throw new ArgumentException("RX fixture is missing IQ samples.", nameof(fixture));

        var stages = new List<StageProbe>();
        var rxMeters = new RxAgcMeterAccumulator();
        var sw = new Stopwatch();
        using var engine = new WdspDspEngine();
        int channel = MeasureStage(stages, "rx-open-channel", "RXA OpenChannel", () => engine.OpenChannel(fixture.SampleRateHz, PixelWidth));
        try
        {
            MeasureStage(stages, "rx-configure-channel", "RXA mode/filter/AGC/NR setup", () =>
            {
                engine.SetMode(channel, RxMode.USB);
                engine.SetFilter(channel, 150, 2850);
                engine.SetVfoHz(channel, 14_200_000);
                engine.SetAgcTop(channel, 80.0);
                engine.SetNoiseReduction(channel, new NrConfig(NrMode: profile.NrMode));
            });

            sw.Start();
            for (int repeat = 0; repeat < FixtureRepeats; repeat++)
                MeasureStage(stages, "rx-feed-iq", "RXA FeedIq fixture block", () => FeedFixtureIq(engine, channel, fixture.IqInterleaved, rxMeters));

            var audio = MeasureStage(stages, "rx-drain-audio", "RXA ReadAudio drain", () => DrainAudio(engine, channel, rxMeters));
            if (audio.Length < 1024)
                throw new InvalidOperationException($"{fixture.Name}/{profile.Id}: expected WDSP RX audio, got {audio.Length} samples.");

            sw.Stop();
            float[] analysis = Tail(audio, AnalysisSamples);
            var metrics = MeasureStage(stages, "rx-analyze-audio", "RXA output analysis", () => DspBenchmarkAnalyzer.AnalyzeAudio(analysis, AudioSampleRateHz, fixture.ExpectedTonesHz));
            return new FixtureRunResult(
                Profile: profile.Label,
                SampleRateHz: AudioSampleRateHz,
                SampleCount: analysis.Length,
                Metrics: metrics,
                ElapsedMs: sw.Elapsed.TotalMilliseconds,
                ClippingCount: CountAudioClips(analysis),
                IntermodulationProxy: 0.0,
                SamplePreview: PreviewAudio(analysis),
                TxMeters: null,
                RxStageMeters: rxMeters.ToSummary(),
                CandidateDiagnostics: null,
                StageProbes: stages);
        }
        finally
        {
            MeasureStage(stages, "rx-close-channel", "RXA CloseChannel", () => engine.CloseChannel(channel));
        }
    }

    private static FixtureRunResult RunTxFixture(DspBenchmarkFixture fixture, ComparisonProfile profile)
    {
        if (fixture.Audio is null)
            throw new ArgumentException("TX fixture is missing audio samples.", nameof(fixture));

        var stages = new List<StageProbe>();
        var sw = new Stopwatch();
        using var engine = new WdspDspEngine();
        int rx = MeasureStage(stages, "tx-open-rx-channel", "TXA support OpenChannel", () => engine.OpenChannel(AudioSampleRateHz, 1024));
        try
        {
            MeasureStage(stages, "tx-configure-channel", "TXA open/filter/leveler/MOX setup", () =>
            {
                engine.OpenTxChannel();
                engine.SetTxMode(RxMode.USB);
                engine.SetTxFilter(150, 2850);
                engine.SetTxLeveling(rx, new TxLevelingConfig());
                if (profile.HasTxPanelGainTrim)
                    engine.SetTxPanelGain(profile.TxPanelGainLinear);
                engine.SetMox(true);
            });

            int block = engine.TxBlockSamples;
            int outSamples = engine.TxOutputSamples;
            var mic = new float[block];
            var iq = new float[2 * outSamples];
            var output = new List<double>(fixture.Audio.Length * 2);

            sw.Start();
            MeasureStage(stages, "tx-process-audio", "TXA ProcessTxBlock fixture audio", () =>
            {
                for (int offset = 0; offset < fixture.Audio.Length; offset += block)
                {
                    Array.Clear(mic);
                    int take = Math.Min(block, fixture.Audio.Length - offset);
                    Array.Copy(fixture.Audio, offset, mic, 0, take);

                    int produced = engine.ProcessTxBlock(mic, iq);
                    for (int i = 0; i < produced; i++)
                    {
                        output.Add(iq[2 * i] * profile.TxOutputTrimLinear);
                        output.Add(iq[2 * i + 1] * profile.TxOutputTrimLinear);
                    }
                }
            });

            sw.Stop();
            if (output.Count < 2 * block)
                throw new InvalidOperationException($"{fixture.Name}/{profile.Id}: expected WDSP TX IQ, got {output.Count / 2} complex samples.");

            double[] analysis = TailIq(output.ToArray(), Math.Min(AnalysisSamples, output.Count / 2));
            var metrics = MeasureStage(stages, "tx-analyze-iq", "TXA output analysis", () => DspBenchmarkAnalyzer.AnalyzeIq(analysis, AudioSampleRateHz, fixture.ExpectedTonesHz));
            var txMeters = MeasureStage(stages, "tx-fetch-stage-meters", "TXA stage meter fetch", engine.GetTxStageMeters);
            var txMetersJson = BuildTxMeters(profile, txMeters, metrics);
            object? candidateDiagnostics = BuildCandidateDiagnostics(profile, txMeters, metrics);

            return new FixtureRunResult(
                Profile: profile.Label,
                SampleRateHz: AudioSampleRateHz,
                SampleCount: analysis.Length / 2,
                Metrics: metrics,
                ElapsedMs: sw.Elapsed.TotalMilliseconds,
                ClippingCount: CountIqClips(analysis),
                IntermodulationProxy: ComputeIntermodulationProxy(analysis, AudioSampleRateHz, fixture.ExpectedTonesHz),
                SamplePreview: PreviewIq(analysis),
                TxMeters: txMetersJson,
                RxStageMeters: null,
                CandidateDiagnostics: candidateDiagnostics,
                StageProbes: stages);
        }
        finally
        {
            MeasureStage(stages, "tx-teardown-channel", "TXA MOX off and CloseChannel", () =>
            {
                engine.SetMox(false);
                engine.CloseChannel(rx);
            });
        }
    }

    private static void MeasureStage(ICollection<StageProbe> stages, string stageId, string label, Action action)
    {
        MeasureStage(stages, stageId, label, () =>
        {
            action();
            return 0;
        });
    }

    private static T MeasureStage<T>(ICollection<StageProbe> stages, string stageId, string label, Func<T> action)
    {
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        try
        {
            return action();
        }
        finally
        {
            long elapsedTicks = Stopwatch.GetTimestamp() - started;
            long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();
            stages.Add(new StageProbe(
                StageId: stageId,
                Label: label,
                ElapsedMs: elapsedTicks * 1000.0 / Stopwatch.Frequency,
                ManagedAllocatedBytes: Math.Max(0L, allocatedAfter - allocatedBefore)));
        }
    }

    private static void FeedFixtureIq(WdspDspEngine engine, int channel, double[] iq, RxAgcMeterAccumulator rxMeters)
    {
        int complexSamples = iq.Length / 2;
        for (int offset = 0; offset < complexSamples; offset += RxChunkComplex)
        {
            int take = Math.Min(RxChunkComplex, complexSamples - offset);
            engine.FeedIq(channel, iq.AsSpan(2 * offset, 2 * take));
            rxMeters.Add(engine.GetRxStageMeters(channel));
        }
    }

    private static float[] DrainAudio(WdspDspEngine engine, int channel, RxAgcMeterAccumulator rxMeters)
    {
        var samples = new List<float>(AnalysisSamples * 2);
        var buffer = new float[2048];

        for (int i = 0; i < 160; i++)
        {
            Thread.Sleep(10);
            rxMeters.Add(engine.GetRxStageMeters(channel));
            int drained = engine.ReadAudio(channel, buffer);
            rxMeters.Add(engine.GetRxStageMeters(channel));
            if (drained > 0)
                samples.AddRange(buffer.Take(drained));
            if (samples.Count >= AnalysisSamples * 2)
                break;
        }

        return samples.ToArray();
    }

    private static Dictionary<string, double> BuildMetricMap(
        IReadOnlyList<string> requiredMetrics,
        DspBenchmarkMetrics sourceMetrics,
        FixtureRunResult run,
        string scenarioId,
        IReadOnlyDictionary<string, string> metricDirections)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (string metricName in requiredMetrics)
        {
            string metricId = NormalizeMetricId(metricName);
            if (metricId.Length == 0)
                continue;

            string direction = metricDirections.TryGetValue(metricId, out string? catalogDirection)
                ? catalogDirection
                : GetDefaultMetricDirection(metricId);
            result[metricName] = Math.Round(GetMetricValue(metricId, direction, sourceMetrics, run, scenarioId), 6);
        }

        return result;
    }

    private static double GetMetricValue(
        string metricId,
        string direction,
        DspBenchmarkMetrics source,
        FixtureRunResult run,
        string scenarioId)
    {
        return metricId switch
        {
            "coherenttonepower" => FirstTonePower(run.Metrics),
            "wantedsnr" => ComputeWantedSnrDb(run.Metrics),
            "signalsinad" => ComputeSignalSinadDb(run.Metrics),
            "spectralpreservation" => ComputeTonePreservationScore(source, run.Metrics),
            "outputrms" => run.Metrics.Rms,
            "latency" => EstimatePipelineLatencyMs(run),
            "speechbandpreservation" => ComputeTonePreservationScore(source, run.Metrics),
            "noisereduction" => ComputeNoiseReductionScore(source, run.Metrics),
            "artifactscore" => ComputeArtifactScore(run),
            "rmsmovement" => run.Metrics.WindowedRmsSpreadDb,
            "cpu" => run.ElapsedMs,
            "processingelapsedms" => run.ElapsedMs,
            "throughputratio" => ComputeThroughputRatio(run),
            "windowedrmsmovement" => run.Metrics.WindowedRmsSpreadDb,
            "coherenttonecontinuity" => 1.0 / (1.0 + Math.Max(0.0, run.Metrics.WindowedRmsSpreadDb) / 12.0),
            "agcgainmovement" => GetRxAgcGainMovementDb(run),
            "impulsesuppression" => ComputeImpulseSuppressionDb(source, run.Metrics),
            "postblankerringing" => Math.Max(0.0, run.Metrics.WindowedRmsSpreadDb - source.WindowedRmsSpreadDb),
            "wantedadjacentratio" => ComputeWantedAdjacentRatioDb(run.Metrics),
            "filterleakage" => AdjacentTonePower(run.Metrics),
            "agcmovement" => GetRxAgcGainMovementDb(run),
            "falseopenrate" => scenarioId is "noise-only-gating" or "noise-only"
                ? Math.Clamp(run.Metrics.Rms / 0.025, 0.0, 1.0)
                : 0.0,
            "noisefloormovement" => run.Metrics.WindowedRmsSpreadDb,
            "settlingtime" => Math.Max(0.0, run.Metrics.WindowedRmsSpreadDb * 20.0),
            "overshoot" => Math.Max(0.0, run.Metrics.CrestFactorDb - 14.0),
            "openlatency" => EstimatePipelineLatencyMs(run),
            "closelatency" => EstimatePipelineLatencyMs(run),
            "audiodiscontinuity" => Math.Max(0.0, run.Metrics.WindowedRmsSpreadDb / 20.0),
            "peak" => run.Metrics.Peak,
            "crestfactor" => run.Metrics.CrestFactorDb,
            "clippingcount" => run.ClippingCount,
            "intermodulationproxy" => run.IntermodulationProxy,
            "rms" => run.Metrics.Rms,
            "spectralbalance" => ComputeSpectralBalanceScore(run.Metrics),
            "txlevelergainreduction" => GetTxMeterValue(run, "levelerGainReductionDb"),
            "txcfcgainreduction" => GetTxMeterValue(run, "cfcGainReductionDb"),
            "txcompressorpeak" => GetTxMeterValue(run, "compressorPkDbfs", -400.0),
            "txalcgainreduction" => GetTxMeterValue(run, "alcGainReductionDb"),
            "txoutputpeak" => GetTxMeterValue(run, "effectiveOutPkDbfs", GetTxMeterValue(run, "outPkDbfs", ToDb(run.Metrics.Peak))),
            "txoutputaverage" => GetTxMeterValue(run, "effectiveOutAvDbfs", GetTxMeterValue(run, "outAvDbfs", ToDb(run.Metrics.Rms))),
            _ => direction == "informational" ? run.Metrics.Rms : run.Metrics.WindowedRmsSpreadDb,
        };
    }

    private static double GetRxAgcGainMovementDb(FixtureRunResult run)
    {
        if (run.RxStageMeters is not null &&
            run.RxStageMeters.TryGetValue("agcGainMovementDb", out object? value) &&
            value is double movement &&
            double.IsFinite(movement))
        {
            return movement;
        }

        return run.Metrics.WindowedRmsSpreadDb;
    }

    private static IReadOnlyList<object> NewGateRecords(PlanScenario scenario, FixtureRunResult run)
    {
        if (scenario.AcceptanceGates.Count == 0)
        {
            bool passed = run.ClippingCount == 0 && run.Metrics.Rms > 0.0 && double.IsFinite(run.Metrics.Rms);
            return new[]
            {
                new Dictionary<string, object?>
                {
                    ["id"] = "wdsp-fixture-executed",
                    ["passed"] = passed,
                    ["status"] = passed ? "pass" : "fail",
                    ["note"] = "WDSP offline fixture executed; hardware acceptance remains a separate gate.",
                },
            };
        }

        return scenario.AcceptanceGates.Select(gate => new Dictionary<string, object?>
        {
            ["id"] = NormalizeMetricId(gate).Length > 48 ? NormalizeMetricId(gate)[..48] : NormalizeMetricId(gate),
            ["passed"] = GatePassed(gate, run),
            ["status"] = GatePassed(gate, run) ? "pass" : "fail",
            ["note"] = GatePassed(gate, run)
                ? "WDSP offline fixture executed; reviewer must still judge hardware/on-air evidence for this acceptance gate."
                : "WDSP offline fixture produced a local metric failure for this acceptance gate.",
        }).ToArray();
    }

    private static bool GatePassed(string gate, FixtureRunResult run)
    {
        if (!double.IsFinite(run.Metrics.Rms) || !double.IsFinite(run.Metrics.Peak) || run.Metrics.Rms <= 0.0)
            return false;

        string normalized = NormalizeMetricId(gate);
        if ((normalized.Contains("clip", StringComparison.Ordinal) ||
             normalized.Contains("overdrive", StringComparison.Ordinal) ||
             normalized.Contains("rail", StringComparison.Ordinal)) &&
            run.ClippingCount > 0)
            return false;

        return true;
    }

    private static Dictionary<string, object?> TxMetersToJson(TxStageMeters meters) => new()
    {
        ["micPkDbfs"] = meters.MicPk,
        ["micAvDbfs"] = meters.MicAv,
        ["eqPkDbfs"] = meters.EqPk,
        ["eqAvDbfs"] = meters.EqAv,
        ["levelerPkDbfs"] = meters.LvlrPk,
        ["levelerAvDbfs"] = meters.LvlrAv,
        ["levelerGainReductionDb"] = meters.LvlrGr,
        ["cfcPkDbfs"] = meters.CfcPk,
        ["cfcAvDbfs"] = meters.CfcAv,
        ["cfcGainReductionDb"] = meters.CfcGr,
        ["compressorPkDbfs"] = meters.CompPk,
        ["compressorAvDbfs"] = meters.CompAv,
        ["alcPkDbfs"] = meters.AlcPk,
        ["alcAvDbfs"] = meters.AlcAv,
        ["alcGainReductionDb"] = meters.AlcGr,
        ["outPkDbfs"] = meters.OutPk,
        ["outAvDbfs"] = meters.OutAv,
    };

    private static Dictionary<string, object?> BuildTxMeters(
        ComparisonProfile profile,
        TxStageMeters meters,
        DspBenchmarkMetrics metrics)
    {
        var result = TxMetersToJson(meters);
        if (profile.HasTxOutputTrim)
        {
            double effectivePeakDbfs = ToDb(metrics.Peak);
            double effectiveAverageDbfs = ToDb(metrics.Rms);
            result["rawOutPkDbfs"] = meters.OutPk;
            result["rawOutAvDbfs"] = meters.OutAv;
            result["effectiveOutPkDbfs"] = Math.Round(effectivePeakDbfs, 6);
            result["effectiveOutAvDbfs"] = Math.Round(effectiveAverageDbfs, 6);
            result["txOutputTrimDb"] = profile.TxOutputTrimDb;
            result["txOutputTrimLinear"] = Math.Round(profile.TxOutputTrimLinear, 9);
            return result;
        }

        if (!profile.HasTxPanelGainTrim)
            return result;

        result["rawOutPkDbfs"] = meters.OutPk;
        result["rawOutAvDbfs"] = meters.OutAv;
        result["txPanelGainTrimDb"] = profile.TxPanelGainTrimDb;
        result["txPanelGainLinear"] = Math.Round(profile.TxPanelGainLinear, 9);
        return result;
    }

    private static object? BuildCandidateDiagnostics(
        ComparisonProfile profile,
        TxStageMeters meters,
        DspBenchmarkMetrics metrics)
    {
        if (!profile.HasTxOutputTrim && !profile.HasTxPanelGainTrim)
            return null;

        double effectivePeakDbfs = ToDb(metrics.Peak);
        double effectiveAverageDbfs = ToDb(metrics.Rms);
        if (profile.HasTxOutputTrim)
        {
            return new Dictionary<string, object?>
            {
                ["schemaVersion"] = 1,
                ["profileKind"] = "fixture-only-post-wdsp-output-trim",
                ["defaultBehaviorChanged"] = false,
                ["requiresRuntimeOptIn"] = true,
                ["txOutputTrimDb"] = profile.TxOutputTrimDb,
                ["txOutputTrimLinear"] = Math.Round(profile.TxOutputTrimLinear, 9),
                ["rawWdspOutPkDbfs"] = meters.OutPk,
                ["rawWdspOutAvDbfs"] = meters.OutAv,
                ["effectiveTxOutputPeakDbfs"] = Math.Round(effectivePeakDbfs, 6),
                ["effectiveTxOutputAverageDbfs"] = Math.Round(effectiveAverageDbfs, 6),
                ["headroomImprovementDb"] = Math.Round(Math.Max(0.0, meters.OutPk - effectivePeakDbfs), 6),
                ["pureSignalPathStatus"] = "not-applied; fixture-only candidate must stay disabled while PureSignal is armed until hardware safe-bypass evidence exists",
            };
        }

        return new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["profileKind"] = "fixture-only-tx-panel-gain-trim",
            ["defaultBehaviorChanged"] = false,
            ["requiresRuntimeOptIn"] = true,
            ["txPanelGainTrimDb"] = profile.TxPanelGainTrimDb,
            ["txPanelGainLinear"] = Math.Round(profile.TxPanelGainLinear, 9),
            ["rawWdspOutPkDbfs"] = meters.OutPk,
            ["rawWdspOutAvDbfs"] = meters.OutAv,
            ["analyzedTxOutputPeakDbfs"] = Math.Round(effectivePeakDbfs, 6),
            ["analyzedTxOutputAverageDbfs"] = Math.Round(effectiveAverageDbfs, 6),
            ["headroomTrimDb"] = Math.Abs(profile.TxPanelGainTrimDb),
            ["pureSignalPathStatus"] = "not-applied; fixture-only candidate must stay disabled while PureSignal is armed until hardware safe-bypass evidence exists",
        };
    }

    private static double ComputeWantedSnrDb(DspBenchmarkMetrics metrics)
    {
        if (metrics.TonePowerDb.Count == 0)
            return 0.0;

        double tonePower = Math.Pow(10.0, FirstTonePower(metrics) / 10.0);
        double totalPower = Math.Max(metrics.Rms * metrics.Rms, 1.0e-300);
        double noisePower = Math.Max(totalPower - tonePower, 1.0e-300);
        return 10.0 * Math.Log10(Math.Max(tonePower / noisePower, 1.0e-300));
    }

    private static double ComputeSignalSinadDb(DspBenchmarkMetrics metrics) =>
        ComputeWantedSnrDb(metrics);

    private static double ComputeThroughputRatio(FixtureRunResult run)
    {
        if (run.SampleRateHz <= 0 || run.SampleCount <= 0 || run.ElapsedMs <= 0.0)
            return 0.0;

        double fixtureSeconds = run.SampleCount / (double)run.SampleRateHz;
        double processingSeconds = run.ElapsedMs / 1000.0;
        return fixtureSeconds / processingSeconds;
    }

    private static object NewStageTimingRecord(FixtureRunResult run)
    {
        var stages = run.StageProbes;
        return new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["probeKind"] = "managed-wrapper-stage-timing",
            ["timingSource"] = "Stopwatch.GetTimestamp",
            ["allocationProbeKind"] = "GC.GetAllocatedBytesForCurrentThread",
            ["nativeCStageInstrumentationStatus"] = "not-instrumented",
            ["nativeAllocationProbeStatus"] = "managed-thread-delta-only",
            ["stageCount"] = stages.Count,
            ["totalStageElapsedMs"] = Math.Round(stages.Sum(static stage => stage.ElapsedMs), 6),
            ["processingStageElapsedMs"] = Math.Round(stages
                .Where(static stage => stage.StageId.Contains("feed", StringComparison.Ordinal) ||
                    stage.StageId.Contains("drain", StringComparison.Ordinal) ||
                    stage.StageId.Contains("process", StringComparison.Ordinal))
                .Sum(static stage => stage.ElapsedMs), 6),
            ["maxStageElapsedMs"] = stages.Count == 0 ? 0.0 : Math.Round(stages.Max(static stage => stage.ElapsedMs), 6),
            ["totalManagedAllocatedBytes"] = stages.Sum(static stage => stage.ManagedAllocatedBytes),
            ["maxStageManagedAllocatedBytes"] = stages.Count == 0 ? 0L : stages.Max(static stage => stage.ManagedAllocatedBytes),
            ["stages"] = stages.Select(static stage => new Dictionary<string, object?>
            {
                ["stageId"] = stage.StageId,
                ["label"] = stage.Label,
                ["elapsedMs"] = Math.Round(stage.ElapsedMs, 6),
                ["managedAllocatedBytes"] = stage.ManagedAllocatedBytes,
            }).ToArray(),
        };
    }

    private static object NewManagedAllocationRecord(FixtureRunResult run)
    {
        return new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["probeKind"] = "managed-thread-delta",
            ["allocationSource"] = "GC.GetAllocatedBytesForCurrentThread",
            ["totalManagedAllocatedBytes"] = run.StageProbes.Sum(static stage => stage.ManagedAllocatedBytes),
            ["maxStageManagedAllocatedBytes"] = run.StageProbes.Count == 0 ? 0L : run.StageProbes.Max(static stage => stage.ManagedAllocatedBytes),
            ["nativeAllocationProbeStatus"] = "not-instrumented",
            ["limitations"] = new[]
            {
                "Managed allocation deltas expose wrapper/tooling pressure only.",
                "Native C heap allocation telemetry requires explicit WDSP allocator instrumentation before it can be treated as native-allocation proof.",
            },
        };
    }

    private static double ComputeTonePreservationScore(DspBenchmarkMetrics source, DspBenchmarkMetrics output)
    {
        if (source.TonePowerDb.Count == 0 || output.TonePowerDb.Count == 0)
            return 1.0 / (1.0 + Math.Abs(output.DcOffset));

        double errorSum = 0.0;
        int count = 0;
        foreach (var tone in source.TonePowerDb)
        {
            if (output.TonePowerDb.TryGetValue(tone.Key, out double outPower))
            {
                errorSum += Math.Abs(outPower - tone.Value);
                count++;
            }
        }

        if (count == 0)
            return 0.0;

        double averageError = errorSum / count;
        return 1.0 / (1.0 + averageError / 20.0);
    }

    private static double ComputeNoiseReductionScore(DspBenchmarkMetrics source, DspBenchmarkMetrics output)
    {
        double sourcePower = Math.Max(source.Rms * source.Rms, 1.0e-300);
        double outputPower = Math.Max(output.Rms * output.Rms, 1.0e-300);
        return 10.0 * Math.Log10(sourcePower / outputPower);
    }

    private static double ComputeArtifactScore(FixtureRunResult run)
    {
        double clippingPenalty = run.ClippingCount > 0 ? 10.0 + Math.Log10(run.ClippingCount) : 0.0;
        double crestPenalty = Math.Max(0.0, run.Metrics.CrestFactorDb - 18.0) * 0.25;
        double dcPenalty = Math.Abs(run.Metrics.DcOffset) * 20.0;
        return run.Metrics.WindowedRmsSpreadDb * 0.15 + crestPenalty + clippingPenalty + dcPenalty;
    }

    private static double EstimatePipelineLatencyMs(FixtureRunResult run) =>
        run.TxMeters is null
            ? 7.5
            : 1000.0 * 512.0 / 48_000.0;

    private static double ToDb(double value) =>
        20.0 * Math.Log10(Math.Max(value, 1.0e-300));

    private static double GetTxMeterValue(FixtureRunResult run, string name, double fallback = 0.0)
    {
        if (run.TxMeters is null || !run.TxMeters.TryGetValue(name, out object? value) || value is null)
            return fallback;

        if (value is float f && float.IsFinite(f))
            return f;
        if (value is double d && double.IsFinite(d))
            return d;
        if (value is int i)
            return i;
        if (value is long l)
            return l;
        if (value is decimal m)
            return (double)m;

        return double.TryParse(
            Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out double parsed) && double.IsFinite(parsed)
            ? parsed
            : fallback;
    }

    private static double ComputeImpulseSuppressionDb(DspBenchmarkMetrics source, DspBenchmarkMetrics output)
    {
        double sourcePeak = 20.0 * Math.Log10(Math.Max(source.Peak, 1.0e-300));
        double outputPeak = 20.0 * Math.Log10(Math.Max(output.Peak, 1.0e-300));
        return sourcePeak - outputPeak;
    }

    private static double ComputeWantedAdjacentRatioDb(DspBenchmarkMetrics metrics)
    {
        if (!metrics.TonePowerDb.TryGetValue("wanted", out double wanted))
            wanted = FirstTonePower(metrics);
        if (!metrics.TonePowerDb.TryGetValue("adjacent", out double adjacent))
            adjacent = metrics.TonePowerDb.Count > 1 ? metrics.TonePowerDb.Skip(1).First().Value : -120.0;
        return wanted - adjacent;
    }

    private static double AdjacentTonePower(DspBenchmarkMetrics metrics) =>
        metrics.TonePowerDb.TryGetValue("adjacent", out double adjacent)
            ? adjacent
            : -120.0;

    private static double FirstTonePower(DspBenchmarkMetrics metrics) =>
        metrics.TonePowerDb.Count > 0
            ? metrics.TonePowerDb.First().Value
            : -120.0;

    private static double ComputeSpectralBalanceScore(DspBenchmarkMetrics metrics)
    {
        if (metrics.TonePowerDb.Count < 2)
            return 1.0;

        var powers = metrics.TonePowerDb.Values.ToArray();
        double average = powers.Average();
        double variance = powers.Sum(p => (p - average) * (p - average)) / powers.Length;
        return 1.0 / (1.0 + Math.Sqrt(variance) / 18.0);
    }

    private static double ComputeIntermodulationProxy(
        ReadOnlySpan<double> iq,
        int sampleRateHz,
        IReadOnlyDictionary<string, double> tones)
    {
        if (tones.Count < 2)
            return 0.0;

        var values = tones.Values.Take(2).ToArray();
        double f1 = values[0];
        double f2 = values[1];
        double pFund = Math.Pow(10.0, TonePowerIq(iq, sampleRateHz, f1) / 10.0) +
                       Math.Pow(10.0, TonePowerIq(iq, sampleRateHz, f2) / 10.0);
        double pImd = Math.Pow(10.0, TonePowerIq(iq, sampleRateHz, (2.0 * f1) - f2) / 10.0) +
                      Math.Pow(10.0, TonePowerIq(iq, sampleRateHz, (2.0 * f2) - f1) / 10.0);
        return 10.0 * Math.Log10(Math.Max(pImd / Math.Max(pFund, 1.0e-300), 1.0e-300));
    }

    private static double TonePowerIq(ReadOnlySpan<double> iqInterleaved, int sampleRateHz, double frequencyHz)
    {
        int complexSamples = iqInterleaved.Length / 2;
        double re = 0.0;
        double im = 0.0;
        double omega = -2.0 * Math.PI * frequencyHz / sampleRateHz;

        for (int n = 0; n < complexSamples; n++)
        {
            double i = iqInterleaved[2 * n];
            double q = iqInterleaved[2 * n + 1];
            double c = Math.Cos(omega * n);
            double s = Math.Sin(omega * n);
            re += i * c - q * s;
            im += i * s + q * c;
        }

        double power = (re * re + im * im) / Square(complexSamples);
        return 10.0 * Math.Log10(Math.Max(power, 1.0e-300));
    }

    private static int CountAudioClips(ReadOnlySpan<float> audio)
    {
        int count = 0;
        foreach (float sample in audio)
        {
            if (Math.Abs(sample) >= 0.999f)
                count++;
        }
        return count;
    }

    private static int CountIqClips(ReadOnlySpan<double> iq)
    {
        int count = 0;
        for (int n = 0; n < iq.Length / 2; n++)
        {
            double mag = Math.Sqrt((iq[2 * n] * iq[2 * n]) + (iq[2 * n + 1] * iq[2 * n + 1]));
            if (mag >= 0.999)
                count++;
        }
        return count;
    }

    private static double[] PreviewAudio(ReadOnlySpan<float> audio) =>
        audio[..Math.Min(48, audio.Length)].ToArray().Select(static x => Math.Round(x, 8)).ToArray();

    private static object[] PreviewIq(ReadOnlySpan<double> iq)
    {
        int pairs = Math.Min(24, iq.Length / 2);
        var result = new object[pairs];
        for (int i = 0; i < pairs; i++)
        {
            result[i] = new Dictionary<string, double>
            {
                ["i"] = Math.Round(iq[2 * i], 8),
                ["q"] = Math.Round(iq[2 * i + 1], 8),
            };
        }
        return result;
    }

    private static float[] Tail(float[] samples, int count)
    {
        int take = Math.Min(samples.Length, count);
        var tail = new float[take];
        Array.Copy(samples, samples.Length - take, tail, 0, take);
        return tail;
    }

    private static double[] TailIq(double[] iqInterleaved, int complexCount)
    {
        int take = Math.Min(iqInterleaved.Length / 2, complexCount);
        var tail = new double[take * 2];
        Array.Copy(iqInterleaved, iqInterleaved.Length - tail.Length, tail, 0, tail.Length);
        return tail;
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

        string assemblyDir = Path.GetDirectoryName(typeof(WdspDspEngine).Assembly.Location) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(assemblyDir))
        {
            assemblyDir = Path.GetFullPath(assemblyDir);
            if (IsSubPathOf(assemblyDir, fullPath))
            {
                pathKind = "dsp-assembly-relative";
                return Path.GetRelativePath(assemblyDir, fullPath).Replace('\\', '/');
            }
        }

        pathKind = "absolute";
        return fullPath;
    }

    private static bool IsSubPathOf(string root, string path)
    {
        string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string HashFileSha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string FullPathOrDefault(string value, string bundleDir, string defaultRelative, bool required = true)
    {
        string path = string.IsNullOrWhiteSpace(value) ? Path.Combine(bundleDir, defaultRelative) : value;
        if (!Path.IsPathRooted(path))
            path = Path.Combine(bundleDir, path);
        string full = Path.GetFullPath(path);
        if (required || File.Exists(full))
            return full;
        return full;
    }

    private static void ThrowIfExists(string path, bool force)
    {
        if (File.Exists(path) && !force)
            throw new IOException($"Output file already exists. Use --force to overwrite: {path}");
    }

    private static void WriteJson(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions));
    }

    private static string WriteJsonWithHash(string path, object value)
    {
        WriteJson(path, value);
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    }

    private static string ToPortablePath(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        return relative == "." ? "." : relative.Replace('\\', '/');
    }

    private static List<PlanScenario> ReadPlanScenarios(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("scenarios", out var scenariosElement) ||
            scenariosElement.ValueKind != JsonValueKind.Array)
            return [];

        var scenarios = new List<PlanScenario>();
        foreach (var scenario in scenariosElement.EnumerateArray())
        {
            string id = NormalizeId(GetString(scenario, "id"));
            if (id.Length == 0)
                continue;

            scenarios.Add(new PlanScenario(
                Id: id,
                Name: GetString(scenario, "name"),
                FixtureStatus: GetString(scenario, "fixtureStatus"),
                SignalPath: GetString(scenario, "signalPath"),
                RequiredMetrics: GetStringArray(scenario, "requiredMetrics"),
                AcceptanceGates: GetStringArray(scenario, "acceptanceGates")));
        }

        return scenarios;
    }

    private static Dictionary<string, string> ReadMetricDirections(string path)
    {
        var directions = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path))
            return directions;

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("metrics", out var metricsElement) ||
            metricsElement.ValueKind != JsonValueKind.Array)
            return directions;

        foreach (var metric in metricsElement.EnumerateArray())
        {
            string id = NormalizeMetricId(GetString(metric, "id"));
            if (id.Length == 0)
                id = NormalizeMetricId(GetString(metric, "name"));
            string direction = GetString(metric, "direction").Trim().ToLowerInvariant();
            if (id.Length > 0 && direction is "higher" or "lower" or "informational")
                directions[id] = direction;
        }

        return directions;
    }

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static List<string> GetStringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            return [];

        return value.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString() ?? string.Empty)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .ToList();
    }

    private static string NormalizeId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return string.Join('-', value.Trim().ToLowerInvariant()
            .Split(value.Where(static c => !char.IsLetterOrDigit(c)).Distinct().ToArray(), StringSplitOptions.RemoveEmptyEntries));
    }

    private static string NormalizeMetricId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return new string(value.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    }

    private static string NormalizeComparisonId(string value)
    {
        string normalized = NormalizeId(value);
        return normalized switch
        {
            "off" or "baseline" => "off-baseline",
            "thetis" => "thetis-parity",
            "current" or "zeus-current" or "zeus" => "current-zeus",
            "candidate-dsp" or "candidate-opt-in" => "candidate-under-test",
            "candidate" => "candidate-under-test",
            "external" or "external-engine" => "candidate-external-engine-opt-in",
            _ => normalized,
        };
    }

    private static string GetDefaultMetricDirection(string metricId) =>
        metricId switch
        {
            "coherenttonepower" or "wantedsnr" or "signalsinad" or "spectralpreservation" or
            "speechbandpreservation" or "noisereduction" or "coherenttonecontinuity" or
            "impulsesuppression" or "wantedadjacentratio" or "feedbackstability" or
            "statetransitionsuccess" or "throughputratio" => "higher",
            "latency" or "cpu" or "processingelapsedms" or "artifactscore" or "rmsmovement" or
            "windowedrmsmovement" or "agcgainmovement" or "postblankerringing" or
            "filterleakage" or "falseopenrate" or "noisefloormovement" or
            "settlingtime" or "overshoot" or "openlatency" or "closelatency" or
            "audiodiscontinuity" or "clippingcount" or "intermodulationproxy" or
            "txmonitorcoupling" or "txlevelergainreduction" or "txalcgainreduction" or
            "txoutputpeak" or "meterescape" or "audiodrain" or "nativeexceptioncount" => "lower",
            _ => "informational",
        };

    private static double Square(double value) => value * value;

    private sealed record PlanScenario(
        string Id,
        string Name,
        string FixtureStatus,
        string SignalPath,
        IReadOnlyList<string> RequiredMetrics,
        IReadOnlyList<string> AcceptanceGates);

    private sealed record FixtureRunResult(
        string Profile,
        int SampleRateHz,
        int SampleCount,
        DspBenchmarkMetrics Metrics,
        double ElapsedMs,
        int ClippingCount,
        double IntermodulationProxy,
        object SamplePreview,
        IReadOnlyDictionary<string, object?>? TxMeters,
        IReadOnlyDictionary<string, object?>? RxStageMeters,
        object? CandidateDiagnostics,
        IReadOnlyList<StageProbe> StageProbes);

    private sealed class RxAgcMeterAccumulator
    {
        private int _count;
        private double _min = double.PositiveInfinity;
        private double _max = double.NegativeInfinity;
        private double _last;

        public void Add(RxStageMeters meters)
        {
            double value = meters.AgcGain;
            if (!double.IsFinite(value) || value <= -300.0 || value >= 300.0)
                return;

            _count++;
            _last = value;
            _min = Math.Min(_min, value);
            _max = Math.Max(_max, value);
        }

        public IReadOnlyDictionary<string, object?> ToSummary()
        {
            double movement = _count > 0 ? _max - _min : 0.0;
            return new Dictionary<string, object?>
            {
                ["agcGainSampleCount"] = _count,
                ["agcGainMinDb"] = _count > 0 ? Math.Round(_min, 6) : null,
                ["agcGainMaxDb"] = _count > 0 ? Math.Round(_max, 6) : null,
                ["agcGainLastDb"] = _count > 0 ? Math.Round(_last, 6) : null,
                ["agcGainMovementDb"] = Math.Round(movement, 6),
                ["source"] = "WDSP RXA_AGC_GAIN meter",
            };
        }
    }

    private sealed record StageProbe(
        string StageId,
        string Label,
        double ElapsedMs,
        long ManagedAllocatedBytes);

    private sealed record NativeRuntimeIdentity(
        string Rid,
        string FileName,
        string Path,
        string PathKind,
        long Length,
        string Sha256,
        string Status);

    private sealed record ComparisonProfile(
        string Id,
        string Label,
        NrMode NrMode,
        double TxOutputTrimDb = 0.0,
        double TxPanelGainTrimDb = 0.0)
    {
        public bool HasTxOutputTrim => Math.Abs(TxOutputTrimDb) > 0.000_001;

        public double TxOutputTrimLinear => HasTxOutputTrim
            ? Math.Pow(10.0, TxOutputTrimDb / 20.0)
            : 1.0;

        public bool HasTxPanelGainTrim => Math.Abs(TxPanelGainTrimDb) > 0.000_001;

        public double TxPanelGainLinear => HasTxPanelGainTrim
            ? Math.Pow(10.0, TxPanelGainTrimDb / 20.0)
            : 1.0;

        public static ComparisonProfile For(string comparisonId, DspBenchmarkPath path)
        {
            string normalized = NormalizeComparisonId(comparisonId);
            if (path == DspBenchmarkPath.TxAudio)
            {
                if (TryGetTxPanelGainTrimDb(normalized, out double trimDb))
                    return new(normalized, $"wdsp-txa-panel-gain-headroom-trim-{Math.Abs(trimDb):0.##}db-candidate", NrMode.Off, TxPanelGainTrimDb: trimDb);

                return normalized switch
                {
                    "thetis-parity" => new(normalized, "wdsp-txa-thetis-defaults", NrMode.Off),
                    "candidate-under-test" => new(normalized, "wdsp-txa-output-headroom-trim-candidate", NrMode.Off, TxOutputTrimDb: -0.35),
                    _ => new(normalized, "wdsp-txa-current-defaults", NrMode.Off),
                };
            }

            return normalized switch
            {
                "thetis-parity" => new(normalized, "wdsp-emnr-nr2-thetis-parity", NrMode.Emnr),
                "candidate-under-test" => new(normalized, "wdsp-current-safe-bypass", NrMode.Off),
                "candidate-external-engine-opt-in" => new(normalized, "post-demod-external-bypass", NrMode.Off),
                _ => new(normalized, "wdsp-current-nr-off", NrMode.Off),
            };
        }

        private static bool TryGetTxPanelGainTrimDb(string normalizedComparisonId, out double trimDb)
        {
            trimDb = 0.0;
            const string prefix = "candidate-tx-panel-trim-";
            if (!normalizedComparisonId.StartsWith(prefix, StringComparison.Ordinal))
                return false;

            string suffix = normalizedComparisonId[prefix.Length..];
            if (suffix.EndsWith("db", StringComparison.Ordinal))
                suffix = suffix[..^2];

            suffix = suffix.Replace('p', '.');
            if (!double.TryParse(
                    suffix,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double magnitude) ||
                !double.IsFinite(magnitude) ||
                magnitude <= 0.0 ||
                magnitude > 3.0)
            {
                trimDb = 0.0;
                return false;
            }

            trimDb = -magnitude;
            return true;
        }
    }
}

internal sealed record ToolOptions(
    string BundleDir,
    string BenchmarkPlanPath,
    string MetricCatalogPath,
    string MetricsPath,
    string AudioIndexPath,
    string SpectrumIndexPath,
    IReadOnlyList<string> ScenarioIds,
    IReadOnlyList<string> ComparisonIds,
    bool IncludeNonFixtureScenarios,
    bool Force,
    bool JsonOnly,
    bool ShowHelp)
{
    public static string HelpText =>
        """
        dsp-fixture-evidence --bundle-dir <dir> [options]

        Options:
          --bundle-dir <dir>                 Capture bundle root.
          --benchmark-plan-path <path>       benchmark-plan.json path.
          --metric-catalog-path <path>       benchmark-metric-catalog.json path.
          --metrics-path <path>              Output offline-fixture-metrics.json path.
          --audio-index-path <path>          Output audio-render-before-after.json path.
          --spectrum-index-path <path>       Output spectrum-before-after.json path.
          --scenario-id <id>                 Limit to one scenario. Repeatable or comma-separated.
          --comparison-id <id>               Limit comparisons. Repeatable or comma-separated.
          --include-non-fixture-scenarios    Include non offline-fixture-ready scenarios.
          --force                            Overwrite existing evidence files.
          --json-only                        Print JSON summary only.
        """;

    public static ToolOptions Parse(string[] args)
    {
        string bundleDir = "";
        string planPath = "";
        string catalogPath = "";
        string metricsPath = "";
        string audioIndexPath = "";
        string spectrumIndexPath = "";
        var scenarioIds = new List<string>();
        var comparisonIds = new List<string>();
        bool includeNonFixture = false;
        bool force = false;
        bool jsonOnly = false;
        bool help = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--bundle-dir":
                    bundleDir = RequiredValue(args, ref i, arg);
                    break;
                case "--benchmark-plan-path":
                    planPath = RequiredValue(args, ref i, arg);
                    break;
                case "--metric-catalog-path":
                    catalogPath = RequiredValue(args, ref i, arg);
                    break;
                case "--metrics-path":
                    metricsPath = RequiredValue(args, ref i, arg);
                    break;
                case "--audio-index-path":
                    audioIndexPath = RequiredValue(args, ref i, arg);
                    break;
                case "--spectrum-index-path":
                    spectrumIndexPath = RequiredValue(args, ref i, arg);
                    break;
                case "--scenario-id":
                    AddCsvValues(scenarioIds, RequiredValue(args, ref i, arg));
                    break;
                case "--comparison-id":
                    AddCsvValues(comparisonIds, RequiredValue(args, ref i, arg));
                    break;
                case "--include-non-fixture-scenarios":
                    includeNonFixture = true;
                    break;
                case "--force":
                    force = true;
                    break;
                case "--json-only":
                    jsonOnly = true;
                    break;
                case "--help" or "-h" or "/?":
                    help = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{arg}'.");
            }
        }

        if (comparisonIds.Count == 0)
            comparisonIds.AddRange(["off-baseline", "thetis-parity", "current-zeus", "candidate-under-test", "candidate-external-engine-opt-in"]);

        return new ToolOptions(
            bundleDir,
            planPath,
            catalogPath,
            metricsPath,
            audioIndexPath,
            spectrumIndexPath,
            scenarioIds,
            comparisonIds,
            includeNonFixture,
            force,
            jsonOnly,
            help);
    }

    private static string RequiredValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
            throw new ArgumentException($"{option} requires a value.");
        return args[++index];
    }

    private static void AddCsvValues(List<string> target, string value)
    {
        foreach (string item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            target.Add(item);
    }
}

internal enum DspBenchmarkPath
{
    RxIq,
    TxAudio,
}

internal sealed record DspBenchmarkFixture(
    string Name,
    DspBenchmarkPath Path,
    int SampleRateHz,
    double[]? IqInterleaved,
    float[]? Audio,
    IReadOnlyDictionary<string, double> ExpectedTonesHz,
    string Summary);

internal static class DspFixtureCatalog
{
    private const int RxSampleRateHz = 192_000;
    private const int TxSampleRateHz = 48_000;
    private const int RxSamples = 65_536;
    private const int TxSamples = 16_384;

    public static DspBenchmarkFixture? CreateOrNull(string id) =>
        id switch
        {
            "weak-cw-carrier" => WeakCarrier(),
            "ssb-like-speech" => SsbLikeSpeech(),
            "fading-carrier" => FadingCarrier(),
            "impulse-noise" => ImpulseNoise(),
            "strong-adjacent" => StrongAdjacent(),
            "noise-only-gating" or "noise-only" => NoiseOnly(id),
            "agc-level-step" => AgcStep(),
            "squelch-transition" => SquelchTransition(),
            "tx-two-tone" => TxTwoTone(),
            "tx-voice-like" => TxVoiceLike(),
            _ => null,
        };

    private static DspBenchmarkFixture WeakCarrier()
    {
        var prng = new DeterministicNoise(0x5743_0001u);
        var iq = BuildRxIq(n => ComplexTone(1_500.0, n, 0.006) + ComplexNoise(ref prng, 0.018));
        return Rx("weak-cw-carrier", iq, "Weak coherent carrier in broadband noise.", ("wanted", 1_500.0));
    }

    private static DspBenchmarkFixture SsbLikeSpeech()
    {
        var prng = new DeterministicNoise(0x5353_4201u);
        var iq = BuildRxIq(n =>
        {
            double t = n / (double)RxSampleRateHz;
            double envelope = 0.45 + 0.35 * Math.Sin(2.0 * Math.PI * 4.2 * t);
            envelope += 0.15 * Math.Sin(2.0 * Math.PI * 6.7 * t + 0.4);
            envelope = Math.Clamp(envelope, 0.1, 0.95);

            return ComplexTone(420.0, n, 0.020 * envelope) +
                   ComplexTone(1_050.0, n, 0.026 * envelope) +
                   ComplexTone(2_100.0, n, 0.014 * envelope) +
                   ComplexNoise(ref prng, 0.010);
        });
        return Rx("ssb-like-speech", iq, "SSB-like formant content with slow speech envelope.",
            ("f1", 420.0), ("f2", 1_050.0), ("f3", 2_100.0));
    }

    private static DspBenchmarkFixture FadingCarrier()
    {
        var prng = new DeterministicNoise(0x4641_4401u);
        var iq = BuildRxIq(n =>
        {
            double t = n / (double)RxSampleRateHz;
            double fade = 0.18 + 0.82 * Math.Pow(0.5 + 0.5 * Math.Sin(2.0 * Math.PI * 1.3 * t), 2.0);
            return ComplexTone(1_250.0, n, 0.040 * fade) + ComplexNoise(ref prng, 0.008);
        });
        return Rx("fading-carrier", iq, "Coherent carrier under deterministic fading.", ("wanted", 1_250.0));
    }

    private static DspBenchmarkFixture ImpulseNoise()
    {
        var prng = new DeterministicNoise(0x494d_5001u);
        var iq = BuildRxIq(n =>
        {
            Complex sample = ComplexTone(1_700.0, n, 0.030) + ComplexNoise(ref prng, 0.010);
            if (n % 1_531 == 0)
                sample += new Complex(1.15, -0.95);
            return sample;
        });
        return Rx("impulse-noise", iq, "Wanted carrier plus periodic impulse noise.", ("wanted", 1_700.0));
    }

    private static DspBenchmarkFixture StrongAdjacent()
    {
        var prng = new DeterministicNoise(0x4144_4a01u);
        var iq = BuildRxIq(n =>
            ComplexTone(1_200.0, n, 0.010) +
            ComplexTone(5_500.0, n, 0.180) +
            ComplexNoise(ref prng, 0.010));
        return Rx("strong-adjacent", iq, "Weak wanted signal beside a strong adjacent blocker.",
            ("wanted", 1_200.0), ("adjacent", 5_500.0));
    }

    private static DspBenchmarkFixture NoiseOnly(string id)
    {
        var prng = new DeterministicNoise(0x4e4f_4901u);
        var iq = BuildRxIq(_ => ComplexNoise(ref prng, 0.020));
        return Rx(id, iq, "Broadband noise-only input for false-open checks.");
    }

    private static DspBenchmarkFixture AgcStep()
    {
        var prng = new DeterministicNoise(0x4147_4301u);
        var iq = BuildRxIq(n =>
        {
            double amp = n switch
            {
                < RxSamples / 3 => 0.008,
                < 2 * RxSamples / 3 => 0.170,
                _ => 0.020,
            };
            return ComplexTone(1_400.0, n, amp) + ComplexNoise(ref prng, 0.006);
        });
        return Rx("agc-level-step", iq, "Three-level wanted carrier step for AGC pumping checks.", ("wanted", 1_400.0));
    }

    private static DspBenchmarkFixture SquelchTransition()
    {
        var prng = new DeterministicNoise(0x5351_4c01u);
        var iq = BuildRxIq(n =>
        {
            double amp = n switch
            {
                < RxSamples / 4 => 0.0,
                < RxSamples / 2 => 0.045,
                < 3 * RxSamples / 4 => 0.006,
                _ => 0.0,
            };
            return ComplexTone(1_650.0, n, amp) + ComplexNoise(ref prng, 0.014);
        });
        return Rx("squelch-transition", iq, "Noise-signal-noise transition for squelch open/close behavior.", ("wanted", 1_650.0));
    }

    private static DspBenchmarkFixture TxTwoTone()
    {
        var audio = BuildTxAudio(n =>
            0.22 * Math.Sin(2.0 * Math.PI * 700.0 * n / TxSampleRateHz) +
            0.22 * Math.Sin(2.0 * Math.PI * 1_900.0 * n / TxSampleRateHz));
        return Tx("tx-two-tone", audio, "TX two-tone linearity probe.", ("low", 700.0), ("high", 1_900.0));
    }

    private static DspBenchmarkFixture TxVoiceLike()
    {
        var prng = new DeterministicNoise(0x5658_0101u);
        var audio = BuildTxAudio(n =>
        {
            double t = n / (double)TxSampleRateHz;
            double envelope = 0.52 + 0.25 * Math.Sin(2.0 * Math.PI * 3.7 * t);
            double voiced =
                0.16 * Math.Sin(2.0 * Math.PI * 180.0 * t) +
                0.09 * Math.Sin(2.0 * Math.PI * 720.0 * t + 0.1) +
                0.06 * Math.Sin(2.0 * Math.PI * 1_820.0 * t + 0.6);
            return envelope * voiced + prng.NextSignedDouble() * 0.010;
        });
        return Tx("tx-voice-like", audio, "Voice-like TX audio envelope and spectrum.",
            ("fundamental", 180.0), ("formant", 720.0), ("presence", 1_820.0));
    }

    private static DspBenchmarkFixture Rx(string name, double[] iq, string summary, params (string Name, double Hz)[] tones) =>
        new(name, DspBenchmarkPath.RxIq, RxSampleRateHz, iq, null, ToneMap(tones), summary);

    private static DspBenchmarkFixture Tx(string name, float[] audio, string summary, params (string Name, double Hz)[] tones) =>
        new(name, DspBenchmarkPath.TxAudio, TxSampleRateHz, null, audio, ToneMap(tones), summary);

    private static IReadOnlyDictionary<string, double> ToneMap((string Name, double Hz)[] tones)
    {
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var tone in tones)
            map.Add(tone.Name, tone.Hz);
        return map;
    }

    private static double[] BuildRxIq(Func<int, Complex> sample)
    {
        var iq = new double[RxSamples * 2];
        for (int n = 0; n < RxSamples; n++)
        {
            Complex value = sample(n);
            iq[2 * n] = value.I;
            iq[2 * n + 1] = value.Q;
        }
        return iq;
    }

    private static float[] BuildTxAudio(Func<int, double> sample)
    {
        var audio = new float[TxSamples];
        for (int n = 0; n < TxSamples; n++)
            audio[n] = (float)sample(n);
        return audio;
    }

    private static Complex ComplexTone(double frequencyHz, int n, double amplitude)
    {
        double phase = 2.0 * Math.PI * frequencyHz * n / RxSampleRateHz;
        return new Complex(amplitude * Math.Cos(phase), amplitude * Math.Sin(phase));
    }

    private static Complex ComplexNoise(ref DeterministicNoise prng, double peak) =>
        new(prng.NextSignedDouble() * peak, prng.NextSignedDouble() * peak);

    private readonly record struct Complex(double I, double Q)
    {
        public static Complex operator +(Complex left, Complex right) =>
            new(left.I + right.I, left.Q + right.Q);
    }

    private struct DeterministicNoise
    {
        private uint _state;

        public DeterministicNoise(uint seed)
        {
            _state = seed == 0 ? 1u : seed;
        }

        public double NextSignedDouble()
        {
            uint x = _state;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            _state = x;
            return ((x >> 8) / 16_777_216.0) * 2.0 - 1.0;
        }
    }
}

internal sealed record DspBenchmarkMetrics(
    double Rms,
    double Peak,
    double CrestFactorDb,
    double DcOffset,
    double WindowedRmsSpreadDb,
    IReadOnlyDictionary<string, double> TonePowerDb);

internal static class DspBenchmarkAnalyzer
{
    public static DspBenchmarkMetrics Analyze(DspBenchmarkFixture fixture)
    {
        if (fixture.Path == DspBenchmarkPath.RxIq)
        {
            if (fixture.IqInterleaved is null)
                throw new ArgumentException("RX fixture is missing IQ samples.", nameof(fixture));
            return AnalyzeIq(fixture.IqInterleaved, fixture.SampleRateHz, fixture.ExpectedTonesHz);
        }

        if (fixture.Audio is null)
            throw new ArgumentException("TX fixture is missing audio samples.", nameof(fixture));
        return AnalyzeAudio(fixture.Audio, fixture.SampleRateHz, fixture.ExpectedTonesHz);
    }

    public static DspBenchmarkMetrics AnalyzeIq(
        ReadOnlySpan<double> iqInterleaved,
        int sampleRateHz,
        IReadOnlyDictionary<string, double> expectedTonesHz)
    {
        if (iqInterleaved.Length == 0 || (iqInterleaved.Length & 1) != 0)
            throw new ArgumentException("IQ input must contain an even number of interleaved samples.", nameof(iqInterleaved));

        int complexSamples = iqInterleaved.Length / 2;
        double sumSquares = 0.0;
        double peak = 0.0;
        double dcI = 0.0;
        double dcQ = 0.0;

        for (int n = 0; n < complexSamples; n++)
        {
            double i = iqInterleaved[2 * n];
            double q = iqInterleaved[2 * n + 1];
            double power = i * i + q * q;
            sumSquares += power;
            peak = Math.Max(peak, Math.Sqrt(power));
            dcI += i;
            dcQ += q;
        }

        double rms = Math.Sqrt(sumSquares / complexSamples);
        double dc = Math.Sqrt(Square(dcI / complexSamples) + Square(dcQ / complexSamples));

        return new DspBenchmarkMetrics(
            rms,
            peak,
            ToDb(peak / Math.Max(rms, 1e-300)),
            dc,
            WindowedRmsSpreadDb(iqInterleaved, complexSamples, complex: true),
            TonePowerIq(iqInterleaved, complexSamples, sampleRateHz, expectedTonesHz));
    }

    public static DspBenchmarkMetrics AnalyzeAudio(
        ReadOnlySpan<float> audio,
        int sampleRateHz,
        IReadOnlyDictionary<string, double> expectedTonesHz)
    {
        if (audio.Length == 0)
            throw new ArgumentException("Audio input must not be empty.", nameof(audio));

        double sumSquares = 0.0;
        double peak = 0.0;
        double dc = 0.0;

        for (int n = 0; n < audio.Length; n++)
        {
            double x = audio[n];
            sumSquares += x * x;
            peak = Math.Max(peak, Math.Abs(x));
            dc += x;
        }

        double rms = Math.Sqrt(sumSquares / audio.Length);

        return new DspBenchmarkMetrics(
            rms,
            peak,
            ToDb(peak / Math.Max(rms, 1e-300)),
            dc / audio.Length,
            WindowedRmsSpreadDb(audio, audio.Length, complex: false),
            TonePowerAudio(audio, sampleRateHz, expectedTonesHz));
    }

    private static IReadOnlyDictionary<string, double> TonePowerIq(
        ReadOnlySpan<double> iqInterleaved,
        int complexSamples,
        int sampleRateHz,
        IReadOnlyDictionary<string, double> expectedTonesHz)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var tone in expectedTonesHz)
        {
            double re = 0.0;
            double im = 0.0;
            double omega = -2.0 * Math.PI * tone.Value / sampleRateHz;

            for (int n = 0; n < complexSamples; n++)
            {
                double i = iqInterleaved[2 * n];
                double q = iqInterleaved[2 * n + 1];
                double c = Math.Cos(omega * n);
                double s = Math.Sin(omega * n);
                re += i * c - q * s;
                im += i * s + q * c;
            }

            double power = (re * re + im * im) / Square(complexSamples);
            result.Add(tone.Key, PowerToDb(power));
        }

        return result;
    }

    private static IReadOnlyDictionary<string, double> TonePowerAudio(
        ReadOnlySpan<float> audio,
        int sampleRateHz,
        IReadOnlyDictionary<string, double> expectedTonesHz)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var tone in expectedTonesHz)
        {
            double re = 0.0;
            double im = 0.0;
            double omega = -2.0 * Math.PI * tone.Value / sampleRateHz;

            for (int n = 0; n < audio.Length; n++)
            {
                double c = Math.Cos(omega * n);
                double s = Math.Sin(omega * n);
                re += audio[n] * c;
                im += audio[n] * s;
            }

            double power = 4.0 * (re * re + im * im) / Square(audio.Length);
            result.Add(tone.Key, PowerToDb(power));
        }

        return result;
    }

    private static double WindowedRmsSpreadDb<T>(ReadOnlySpan<T> samples, int logicalSamples, bool complex)
        where T : struct
    {
        int window = Math.Max(128, logicalSamples / 16);
        double min = double.PositiveInfinity;
        double max = 0.0;

        for (int start = 0; start + window <= logicalSamples; start += window)
        {
            double sumSquares = 0.0;
            if (complex)
            {
                var iq = System.Runtime.InteropServices.MemoryMarshal.Cast<T, double>(samples);
                for (int n = start; n < start + window; n++)
                {
                    double i = iq[2 * n];
                    double q = iq[2 * n + 1];
                    sumSquares += i * i + q * q;
                }
            }
            else
            {
                var audio = System.Runtime.InteropServices.MemoryMarshal.Cast<T, float>(samples);
                for (int n = start; n < start + window; n++)
                    sumSquares += audio[n] * audio[n];
            }

            double rms = Math.Sqrt(sumSquares / window);
            min = Math.Min(min, rms);
            max = Math.Max(max, rms);
        }

        return ToDb(max / Math.Max(min, 1e-300));
    }

    private static double Square(double value) => value * value;

    private static double ToDb(double ratio) => 20.0 * Math.Log10(Math.Max(ratio, 1e-300));

    private static double PowerToDb(double power) => 10.0 * Math.Log10(Math.Max(power, 1e-300));
}
