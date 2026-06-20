// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections.Generic;
using System.Globalization;

namespace Zeus.Server.Diagnostics;

/// <summary>
/// The most common cause of crackling / dropping receive audio: the output
/// audio buffer keeps running dry (underruns). The DSP side can be perfectly
/// healthy (no queue-full, no overruns) while the OS audio sink starves because
/// the machine can't feed it on time — heard as crackle, stutter, or gaps. The
/// dsp-audio probe reports the cumulative underrun sample count; this rule turns
/// a large count into a plain-language likely-cause instead of leaving the
/// number buried in the system dump.
/// </summary>
public sealed class AudioUnderrunRule : IKnownIssueRule
{
    public string Id => "audio-underrun";

    public IReadOnlyCollection<string> Symptoms { get; } = ["rx-audio-quality", "rx-no-audio"];

    public DiagnosticFinding? Evaluate(
        DiagnosticContext ctx, IReadOnlyList<DiagnosticSection> sections)
    {
        var section = RuleInspection.Section(sections, "dsp-audio");
        if (section is null) return null;

        if (!TryParseLong(RuleInspection.Value(section, "audio.underrunSamplesTotal"), out long underruns))
            return null;

        int sampleRate = TryParseInt(RuleInspection.Value(section, "audio.sampleRateHz"), 48000);
        if (sampleRate <= 0) sampleRate = 48000;

        // Ignore a trivial handful (a single underrun at device start is normal).
        // Flag once the lost audio exceeds ~0.1 s — that is clearly audible.
        if (underruns < sampleRate / 10) return null;

        double seconds = (double)underruns / sampleRate;
        return new DiagnosticFinding(
            Title: "Receive audio is dropping out (output buffer underruns)",
            Detail:
                $"Zeus's audio output ran out of buffered sound {underruns.ToString("N0", CultureInfo.InvariantCulture)} " +
                $"times so far this session (about {seconds.ToString("0.0", CultureInfo.InvariantCulture)} seconds of audio lost). " +
                "That is exactly what crackling, stuttering, or brief gaps sound like, and it means the " +
                "computer could not feed the sound card fast enough — not a radio fault. Try: close other " +
                "heavy apps, turn off power-saving / battery-saver, make sure the right output device is " +
                "selected, and restart Zeus. If it keeps happening, include this report so we can dig in.",
            Severity: DiagnosticSeverity.Likely,
            DocRef: null);
    }

    private static bool TryParseLong(string? value, out long result) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    private static int TryParseInt(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;
}
