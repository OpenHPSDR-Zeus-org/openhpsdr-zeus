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
/// Trace/Debug are intentionally dropped to keep the (capacity-bounded) ring
/// signal-dense. Scopes are no-ops. The underlying buffer is thread-safe, so the
/// provider holds no per-call lock of its own.
/// </summary>
public sealed class RingBufferLoggerProvider : ILoggerProvider
{
    private readonly DiagnosticLogBuffer _buffer;

    public RingBufferLoggerProvider(DiagnosticLogBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        _buffer = buffer;
    }

    public ILogger CreateLogger(string categoryName) =>
        new RingBufferLogger(_buffer, ShortCategory(categoryName));

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
        private readonly string _shortCategory;

        public RingBufferLogger(DiagnosticLogBuffer buffer, string shortCategory)
        {
            _buffer = buffer;
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

            _buffer.Add(Redaction.Scrub(line));
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
