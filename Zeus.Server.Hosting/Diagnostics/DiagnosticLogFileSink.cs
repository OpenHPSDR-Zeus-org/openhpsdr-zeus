// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text;

namespace Zeus.Server.Diagnostics;

/// <summary>
/// Rolling on-disk log file. Writes the same formatted+redacted lines the
/// in-memory <see cref="DiagnosticLogBuffer"/> holds, so the recent log persists
/// across a backend crash for the support sidecar to tail.
///
/// Design notes:
/// <list type="bullet">
/// <item>Auto-flush on every write — crash freshness beats throughput here; the
/// ring is Information+ only, so volume is modest and we want the last lines on
/// disk <i>before</i> the process dies.</item>
/// <item>Best-effort: any I/O failure is swallowed (the sink stays "down" until a
/// later write succeeds). A diagnostic sink must never crash the app or throw
/// into the logging pipeline.</item>
/// <item>Rolling: when the active file passes <c>maxBytes</c> it is rotated to
/// <c>{name}.1{ext}</c>, older rolls shift up, and the oldest beyond
/// <c>maxRolls</c> is dropped — bounding disk use without losing the freshest
/// lines.</item>
/// </list>
/// </summary>
public sealed class DiagnosticLogFileSink : IDiagnosticLogFileSink, IDisposable
{
    private const int DefaultMaxBytes = 2 * 1024 * 1024; // 2 MiB active file
    private const int DefaultMaxRolls = 3;               // keep .1 .2 .3 alongside the active file

    private readonly object _sync = new();
    private readonly string _path;
    private readonly string _dir;
    private readonly long _maxBytes;
    private readonly int _maxRolls;

    private StreamWriter? _writer;
    private long _written;
    private bool _disposed;

    public DiagnosticLogFileSink(string path, long maxBytes = DefaultMaxBytes, int maxRolls = DefaultMaxRolls)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        _maxBytes = maxBytes > 0 ? maxBytes : DefaultMaxBytes;
        _maxRolls = Math.Max(0, maxRolls);
    }

    public void Append(string line)
    {
        if (string.IsNullOrEmpty(line) || _disposed) return;
        lock (_sync)
        {
            if (_disposed) return;
            try
            {
                var w = EnsureWriter();
                if (w is null) return; // sink is down; drop this line rather than throw

                // +1 covers the newline; a byte estimate is fine for a roll threshold.
                long lineBytes = Encoding.UTF8.GetByteCount(line) + 1;

                // Roll BEFORE writing when this line would push the active file past
                // the cap, so the active file always exists afterwards and holds the
                // freshest line (a single oversized line on a fresh file is allowed
                // through rather than rolling to an empty file).
                if (_written > 0 && _written + lineBytes > _maxBytes)
                {
                    Roll();
                    w = EnsureWriter();
                    if (w is null) return;
                }

                w.WriteLine(line);
                _written += lineBytes;
            }
            catch
            {
                // Best-effort: a transient I/O error must not break logging. Tear
                // the writer down so the next Append re-opens from scratch.
                TryCloseWriter();
            }
        }
    }

    private StreamWriter? EnsureWriter()
    {
        if (_writer is not null) return _writer;
        try
        {
            if (!Directory.Exists(_dir))
                Directory.CreateDirectory(_dir);

            // Append + shared read so the sidecar (or a developer's `tail`) can read
            // the file while the backend keeps writing it.
            var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true,
            };
            _written = fs.Length;
            return _writer;
        }
        catch
        {
            TryCloseWriter();
            return null;
        }
    }

    private void Roll()
    {
        TryCloseWriter();
        try
        {
            var ext = Path.GetExtension(_path);                       // ".log"
            var stem = _path[..(_path.Length - ext.Length)];          // "...\zeus-app"

            if (_maxRolls == 0)
            {
                // No history retained — just truncate the active file.
                if (File.Exists(_path)) File.Delete(_path);
                return;
            }

            // Drop the oldest, then shift .{n-1} -> .{n} down to active -> .1.
            var oldest = $"{stem}.{_maxRolls}{ext}";
            if (File.Exists(oldest)) File.Delete(oldest);

            for (int i = _maxRolls - 1; i >= 1; i--)
            {
                var src = $"{stem}.{i}{ext}";
                var dst = $"{stem}.{i + 1}{ext}";
                if (File.Exists(src)) File.Move(src, dst, overwrite: true);
            }

            if (File.Exists(_path)) File.Move(_path, $"{stem}.1{ext}", overwrite: true);
        }
        catch
        {
            // If rotation fails the next EnsureWriter just keeps appending to the
            // (possibly oversized) active file — degraded, not fatal.
        }
        finally
        {
            _written = 0;
        }
    }

    private void TryCloseWriter()
    {
        try { _writer?.Dispose(); } catch { /* best effort */ }
        _writer = null;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            TryCloseWriter();
        }
    }
}
