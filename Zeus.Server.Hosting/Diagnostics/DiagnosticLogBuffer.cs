// SPDX-License-Identifier: GPL-2.0-or-later
namespace Zeus.Server.Diagnostics;

/// <summary>
/// Thread-safe in-memory ring buffer of recent formatted log lines. Registered
/// as a singleton and fed by <c>RingBufferLoggerProvider</c>, which is attached
/// to the host's logging pipeline so the main log is ALWAYS being captured —
/// you cannot collect logs retroactively, so the button just snapshots whatever
/// is already here.
///
/// Capacity is generous (<see cref="Capacity"/>) so a report can always include
/// the most recent <see cref="ReportTailLines"/> (100) lines the operator asked
/// for, plus headroom for recipe-specific filtering.
/// </summary>
public sealed class DiagnosticLogBuffer
{
    /// <summary>How many lines the ring retains.</summary>
    public const int Capacity = 1000;

    /// <summary>How many trailing lines the report ships (operator requirement).</summary>
    public const int ReportTailLines = 100;

    private readonly object _sync = new();
    private readonly string[] _ring = new string[Capacity];
    private int _next;   // index of next write
    private int _count;  // number of valid entries (<= Capacity)

    /// <summary>Append one formatted log line. Cheap; safe from any thread.</summary>
    public void Add(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        lock (_sync)
        {
            _ring[_next] = line;
            _next = (_next + 1) % Capacity;
            if (_count < Capacity) _count++;
        }
    }

    /// <summary>
    /// Snapshot up to <paramref name="maxLines"/> of the most recent lines,
    /// oldest first. Defaults to <see cref="ReportTailLines"/>.
    /// </summary>
    public IReadOnlyList<string> Snapshot(int maxLines = ReportTailLines)
    {
        if (maxLines <= 0) return [];
        lock (_sync)
        {
            int take = Math.Min(maxLines, _count);
            var result = new string[take];
            // The oldest of the `take` lines sits `take` slots behind _next.
            int start = ((_next - take) % Capacity + Capacity) % Capacity;
            for (int i = 0; i < take; i++)
                result[i] = _ring[(start + i) % Capacity];
            return result;
        }
    }
}
