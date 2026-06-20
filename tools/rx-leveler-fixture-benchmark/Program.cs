// SPDX-License-Identifier: GPL-2.0-or-later

using System.Security.Cryptography;
using System.Text.Json;
using Zeus.Server;

return RxLevelerFixtureBenchmarkTool.Run(args);

internal static class RxLevelerFixtureBenchmarkTool
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static int Run(string[] args)
    {
        try
        {
            var options = ToolOptions.Parse(args);
            if (options.ShowHelp)
            {
                Console.WriteLine(ToolOptions.HelpText);
                return 0;
            }

            var report = DspRxAudioLevelerFixtureBenchmark.Build();
            string json = JsonSerializer.Serialize(report, JsonOptions);

            if (!string.IsNullOrWhiteSpace(options.OutputPath))
            {
                string outputPath = Path.GetFullPath(options.OutputPath);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Environment.CurrentDirectory);
                File.WriteAllText(outputPath, json);

                if (!options.JsonOnly)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        tool = "rx-leveler-fixture-benchmark",
                        outputPath,
                        sha256 = Sha256(outputPath),
                        scenarioCount = report.Scenarios.Length,
                        candidatePassCount = report.Scenarios.Count(static scenario => scenario.Comparison.CandidatePasses),
                        defaultBehaviorChanged = report.DefaultBehaviorChanged,
                        experimentalOptIn = report.ExperimentalOptIn,
                    }, JsonOptions));
                }
            }

            if (options.JsonOnly || string.IsNullOrWhiteSpace(options.OutputPath))
                Console.WriteLine(json);

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private sealed record ToolOptions(
        string OutputPath,
        bool JsonOnly,
        bool ShowHelp)
    {
        public const string HelpText =
            """
            Usage: rx-leveler-fixture-benchmark [options]

              --output-path <path>   Write the benchmark report JSON to this path.
              --json-only            Write only the benchmark JSON to stdout.
              --help                 Show this help text.
            """;

        public static ToolOptions Parse(string[] args)
        {
            string outputPath = string.Empty;
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

            return new ToolOptions(outputPath, jsonOnly, showHelp);
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
