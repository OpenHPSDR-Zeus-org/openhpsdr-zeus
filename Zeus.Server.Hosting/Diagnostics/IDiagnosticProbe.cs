// SPDX-License-Identifier: GPL-2.0-or-later
namespace Zeus.Server.Diagnostics;

/// <summary>
/// A read-only collector for one subsystem (board, connection, DSP/audio,
/// TX/PS, environment). Each probe contributes a <see cref="DiagnosticSection"/>
/// to the report. Probes are registered in DI and discovered by the
/// <c>DiagnosticReportBuilder</c> via <c>IEnumerable&lt;IDiagnosticProbe&gt;</c>;
/// the active symptom's recipe decides which probe ids run.
///
/// Contract: probes MUST be side-effect free. Resolve services from
/// <see cref="DiagnosticContext.Services"/>, read state, return values.
/// Redaction is applied centrally by the builder, but a probe should still
/// avoid placing obvious secrets (passwords, tokens) into its items.
/// </summary>
public interface IDiagnosticProbe
{
    /// <summary>Stable id used by recipes (e.g. "board", "connection", "dsp", "tx-ps", "environment").</summary>
    string Id { get; }

    /// <summary>Collect this subsystem's snapshot. Never throws out — return a section noting the failure instead.</summary>
    DiagnosticSection Collect(DiagnosticContext ctx);
}
