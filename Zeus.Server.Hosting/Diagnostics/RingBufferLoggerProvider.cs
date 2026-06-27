// SPDX-License-Identifier: GPL-2.0-or-later
using Microsoft.Extensions.Logging;

namespace Zeus.Server.Diagnostics;

/// <summary>
/// An <see cref="ILoggerProvider"/> that mirrors every formatted log line (at
/// Information level and above) into a singleton <see cref="DiagnosticLogBuffer"/>
/// after running it through <see cref="Redaction.Scrub"/>. Attaching this to the
/// host's logging pipeline means the "Report a problem" button can snapshot the
/// recent log retroactively — the ring is always being filled.
///
/// When an <see cref="IDiagnosticLogFileSink"/> is supplied, the SAME redacted
/// line is also mirrored to disk (redaction is computed once and fanned out), so
/// the recent log survives a backend crash for the support sidecar to tail.
///
/// Trace/Debug are intentionally dropped to keep the (capacity-bounded) ring
/// signal-dense. Scopes are no-ops. The underlying buffer is thread-safe, so the
/// provider holds no per-call lock of its own.
/// </summary>
public sealed class RingBufferLoggerProvider : ILoggerProvider
{
    private readonly DiagnosticLogBuffer _buffer;
    private readonly IDiagnosticLogFileSink? _fileSink;

    public RingBufferLoggerProvider(DiagnosticLogBuffer buffer, IDiagnosticLogFileSink? fileSink = null)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        _buffer = buffer;
        _fileSink = fileSink;
    }

    public ILogger CreateLogger(string categoryName) =>
        new RingBufferLogger(_buffer, _fileSink, ShortCategory(categoryName));

    public void Dispose() { /* buffer is owned elsewhere (singleton); nothing to release */ }

    /// <summary>Last dotted segment of a logger category (e.g. "Zeus.Server.RadioService" → "RadioService").</summary>
    private static string ShortCategory(string category)
    {
        if (string.IsNullOrEmpty(category)) return category ?? string.Empty;
        int dot = category.LastIndexOf('.');
        return dot >= 0 && dot < category.Length - 1 ? category[(dot + 1)..] : category;
    }

    private sealed class RingBufferLogger : ILogger
    {
        private static readonly IDisposable NoopScope = new NoopDisposable();

        private readonly DiagnosticLogBuffer _buffer;
        private readonly IDiagnosticLogFileSink? _fileSink;
        private readonly string _shortCategory;

        public RingBufferLogger(DiagnosticLogBuffer buffer, IDiagnosticLogFileSink? fileSink, string shortCategory)
        {
            _buffer = buffer;
            _fileSink = fileSink;
            _shortCategory = shortCategory;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NoopScope;

        // Information and above only — keep the ring dense.
        public bool IsEnabled(LogLevel logLevel) =>
            logLevel >= LogLevel.Information && logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            ArgumentNullException.ThrowIfNull(formatter);

            string message = formatter(state, exception);
            if (string.IsNullOrEmpty(message) && exception is null) return;

            // "{HH:mm:ss.fff} {level} {category-short} {message}" — culture-invariant timestamp.
            string ts = DateTime.Now.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
            string line = exception is null
                ? $"{ts} {Level(logLevel)} {_shortCategory} {message}"
                : $"{ts} {Level(logLevel)} {_shortCategory} {message} {exception}";

            // Redact once, fan out to both the in-memory ring and (when present)
            // the on-disk sink so the same scrubbed line lands in both.
            string redacted = Redaction.Scrub(line);
            _buffer.Add(redacted);
            _fileSink?.Append(redacted);
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Information => "INFO",
            LogLevel.Warning => "WARN",
            LogLevel.Error => "ERROR",
            LogLevel.Critical => "CRIT",
            _ => level.ToString().ToUpperInvariant(),
        };

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}
