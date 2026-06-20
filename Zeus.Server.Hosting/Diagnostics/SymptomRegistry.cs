// SPDX-License-Identifier: GPL-2.0-or-later
namespace Zeus.Server.Diagnostics;

/// <summary>
/// Canonical list of the symptoms the "Report a problem" picker offers, and the
/// probe recipe (which <see cref="IDiagnosticProbe"/> ids to run) for each.
///
/// Recipe philosophy: be robust, not minimal. Always gather the cheap, always-
/// relevant context (environment / connection / board) so a report is useful
/// even when the operator picked the wrong symptom; layer the heavier DSP and
/// TX/PS probes on top only where the symptom group implicates them. An unknown
/// or "other"/null symptom runs everything.
///
/// Registered as a singleton; consumed by <see cref="DiagnosticReportBuilder"/>.
/// </summary>
public sealed class SymptomRegistry
{
    // Canonical probe ids (owned by the probe author; mirrored here for recipes).
    private const string ProbeEnvironment = "environment";
    private const string ProbeConnection = "connection";
    private const string ProbeBoard = "board";
    private const string ProbeDspAudio = "dsp-audio";
    private const string ProbeTxPs = "tx-ps";

    /// <summary>The base recipe every report includes, in a stable display order.</summary>
    private static readonly string[] BaseProbes = [ProbeEnvironment, ProbeConnection, ProbeBoard];

    /// <summary>Every probe id, in stable order — used for "other"/unknown/null.</summary>
    private static readonly string[] AllProbes =
        [ProbeEnvironment, ProbeConnection, ProbeBoard, ProbeDspAudio, ProbeTxPs];

    private static readonly IReadOnlyList<Symptom> _all =
    [
        new Symptom("wont-connect", "Radio won't connect or keeps dropping", "Connection"),
        new Symptom("no-tx-power", "No or low transmit power", "Transmit"),
        new Symptom("ps-not-working", "PureSignal won't work or won't calibrate", "Transmit"),
        new Symptom("tx-audio", "Transmit audio, mic, or processing problem", "Transmit"),
        new Symptom("rx-no-audio", "No receive audio", "Receive"),
        new Symptom("rx-audio-quality", "Receive audio crackles or distorts", "Receive"),
        new Symptom("ui-display", "Display, waterfall, or panadapter problem", "Display"),
        new Symptom("other", "Something else or not sure", "Other"),
    ];

    /// <summary>The eight selectable symptoms, in picker order.</summary>
    public IReadOnlyList<Symptom> All => _all;

    /// <summary>
    /// Which probe ids to run for the given symptom. Robust by construction:
    /// always includes environment/connection/board; adds dsp-audio for the
    /// receive/tx-audio/display symptoms and tx-ps for the transmit-power/PS/
    /// tx-audio symptoms. Unknown, "other", or null returns all five probes.
    /// </summary>
    public IReadOnlyList<string> ProbeIdsFor(string? symptomId)
    {
        switch (symptomId)
        {
            case "wont-connect":
                return BaseProbes;

            case "no-tx-power":
            case "ps-not-working":
                return [.. BaseProbes, ProbeTxPs];

            case "tx-audio":
                // Touches both the audio chain and the TX/PS path.
                return [.. BaseProbes, ProbeDspAudio, ProbeTxPs];

            case "rx-no-audio":
            case "rx-audio-quality":
            case "ui-display":
                return [.. BaseProbes, ProbeDspAudio];

            // "other", null, or anything we don't recognise → cast the widest net.
            default:
                return AllProbes;
        }
    }
}
