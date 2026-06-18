using System.Text.Json;

namespace Zeus.Plugins.Host.Audio;

/// <summary>
/// The control-plane surface of a launched VST engine process that
/// <see cref="VstEngineController"/> drives. Extracted from
/// <see cref="VstEngineProcess"/> so the controller's self-heal supervisor
/// (relaunch / backoff / crash-loop / hang watchdog) can be unit-tested against
/// a fake engine without spawning a real child process.
/// </summary>
internal interface IVstEngineProcess : IDisposable
{
    /// <summary>Raised for every parsed engine event (control thread).</summary>
    event Action<JsonElement>? EngineEvent;

    /// <summary>Raised for every stderr line (diagnostics).</summary>
    event Action<string>? StdErrLine;

    /// <summary>Raised when the supervised engine process exits.</summary>
    event Action<IVstEngineProcess>? Exited;

    /// <summary>Completes when the engine emits its <c>ready</c> handshake.</summary>
    Task<JsonElement> Ready { get; }

    /// <summary>True once the process has exited.</summary>
    bool HasExited { get; }

    /// <summary>Send a control-plane command as one JSON line on stdin.</summary>
    void Send(object command);

    /// <summary>
    /// Force-terminate the process WITHOUT disposing it, so its natural
    /// <see cref="Exited"/> event still fires and the supervisor's relaunch path
    /// runs. Used by the hang watchdog to recycle an alive-but-unresponsive
    /// engine. Best-effort; a no-op if already exited.
    /// </summary>
    void Kill();
}
