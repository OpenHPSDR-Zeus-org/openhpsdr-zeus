// SPDX-License-Identifier: GPL-2.0-or-later
namespace Zeus.Server.Diagnostics;

/// <summary>
/// A durable sink for already-formatted, already-redacted log lines. Mirrors the
/// in-memory <see cref="DiagnosticLogBuffer"/> to disk so the recent log SURVIVES
/// a backend crash and can be tailed by the out-of-process support sidecar (the
/// in-memory ring dies with the process — useless for diagnosing the crash that
/// killed it). Implementations must be thread-safe and best-effort: a logging
/// sink must never throw into the logging pipeline or block the app on an I/O
/// hiccup.
/// </summary>
public interface IDiagnosticLogFileSink
{
    /// <summary>Append one formatted+redacted line. Cheap; safe from any thread; never throws.</summary>
    void Append(string line);
}
