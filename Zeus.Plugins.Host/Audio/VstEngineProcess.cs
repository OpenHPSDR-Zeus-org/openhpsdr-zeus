using System.Diagnostics;
using System.Text.Json;

namespace Zeus.Plugins.Host.Audio;

/// <summary>
/// Launches and supervises the headless VSTHost engine
/// (<c>VSTHostEngine.exe --zeus-bridge</c>) and carries the control plane:
/// newline-delimited JSON over the child's stdin/stdout. The audio plane lives
/// in <see cref="VstEngineBridge"/>. See
/// <c>docs/designs/vst-engine-bridge-protocol.md</c>.
///
/// <para>Not realtime — runs on the control thread. The engine is the
/// externally-installed upstream binary (KlayaR/VSTHost); Zeus never bundles
/// it.</para>
/// </summary>
internal sealed class VstEngineProcess : IDisposable
{
    private readonly Process _process;
    private readonly object _writeLock = new();
    private readonly TaskCompletionSource<JsonElement> _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    /// <summary>Raised on the reader thread for every parsed engine event.</summary>
    public event Action<JsonElement>? EngineEvent;

    /// <summary>Raised on the reader thread for every stderr line (diagnostics).</summary>
    public event Action<string>? StdErrLine;

    /// <summary>Raised when the supervised engine process exits.</summary>
    public event Action<VstEngineProcess>? Exited;

    /// <summary>Completes when the engine emits its <c>ready</c> handshake.</summary>
    public Task<JsonElement> Ready => _ready.Task;

    public bool HasExited => _process.HasExited;
    public int ExitCode => _process.ExitCode;

    private VstEngineProcess(Process process) => _process = process;

    /// <summary>
    /// Resolve the engine exe: explicit override → default install location →
    /// PATH. Returns null if none found (caller surfaces "Get VSTHost").
    /// </summary>
    public static string? FindEngineExe()
    {
        var overridePath = Environment.GetEnvironmentVariable("ZEUS_VST_ENGINE_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return overridePath;

        // Zeus-managed engine location — where the "Get VSTHost" provisioning
        // flow stages the bridge-capable engine. Preferred over a system-wide
        // VSTHost install, which may be an older, non-bridge release.
        var managed = ManagedEnginePath();
        if (managed is not null && File.Exists(managed)) return managed;

        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     })
            {
                if (string.IsNullOrEmpty(root)) continue;
                var p = Path.Combine(root, "VSTHost", "engine", "VSTHostEngine.exe");
                if (File.Exists(p)) return p;
            }
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = Path.Combine(dir.Trim(), OperatingSystem.IsWindows() ? "VSTHostEngine.exe" : "VSTHostEngine");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>
    /// The Zeus-managed engine location
    /// (<c>%LOCALAPPDATA%\Zeus\vst-engine\VSTHostEngine.exe</c> on Windows), or
    /// null on non-Windows. Where Zeus stages a downloaded/provisioned engine so
    /// VST mode doesn't depend on a system-wide VSTHost install.
    /// </summary>
    public static string? ManagedEnginePath()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local)) return null;
        return Path.Combine(local, "Zeus", "vst-engine", "VSTHostEngine.exe");
    }

    /// <summary>Launch the engine in --zeus-bridge mode against an existing SHM.</summary>
    public static VstEngineProcess Launch(string enginePath, string shm,
                                          int frames, int rate, int channels)
    {
        var psi = new ProcessStartInfo
        {
            FileName = enginePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(enginePath) ?? Environment.CurrentDirectory,
        };
        psi.ArgumentList.Add("--zeus-bridge");
        psi.ArgumentList.Add("--shm");      psi.ArgumentList.Add(shm);
        psi.ArgumentList.Add("--frames");   psi.ArgumentList.Add(frames.ToString());
        psi.ArgumentList.Add("--rate");     psi.ArgumentList.Add(rate.ToString());
        psi.ArgumentList.Add("--channels"); psi.ArgumentList.Add(channels.ToString());

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var self = new VstEngineProcess(process);

        process.OutputDataReceived += (_, e) => self.OnStdOut(e.Data);
        process.ErrorDataReceived  += (_, e) => { if (e.Data != null) self.StdErrLine?.Invoke(e.Data); };
        process.Exited += (_, _) =>
        {
            self._ready.TrySetException(new InvalidOperationException(
                $"VST engine exited (code {SafeExitCode(process)}) before ready."));
            self.Exited?.Invoke(self);
        };

        if (!process.Start())
            throw new InvalidOperationException("Failed to start VST engine process.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return self;
    }

    private static int SafeExitCode(Process p)
    {
        try { return p.HasExited ? p.ExitCode : -1; } catch { return -1; }
    }

    private void OnStdOut(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(line);
            root = doc.RootElement.Clone();
        }
        catch (JsonException) { return; } // non-JSON noise — ignore

        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("event", out var evt)
            && evt.ValueKind == JsonValueKind.String)
        {
            if (evt.GetString() == "ready")
                _ready.TrySetResult(root);
            EngineEvent?.Invoke(root);
        }
    }

    /// <summary>Send a control-plane command object as one JSON line on stdin.</summary>
    public void Send(object command)
    {
        var json = JsonSerializer.Serialize(command);
        lock (_writeLock)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try
            {
                if (!_process.HasExited)
                    _process.StandardInput.WriteLine(json);
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
            catch (IOException) { }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            // Graceful: close stdin → the engine's IPC sees EOF and quits.
            if (!_process.HasExited)
            {
                lock (_writeLock) { _process.StandardInput.Close(); }
                if (!_process.WaitForExit(2000))
                    _process.Kill(entireProcessTree: true);
            }
        }
        catch { /* best-effort teardown */ }
        finally { _process.Dispose(); }
    }
}
