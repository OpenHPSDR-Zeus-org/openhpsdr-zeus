# PRD — Plugins System Architecture

**Status:** Draft (2026-04-28) — Architectural proposal for maintainer review
**Related:** [Issue #106](https://github.com/Kb2uka/openhpsdr-zeus/issues/106) (websocket host isolation)
**Authors:** Research and synthesis for Brian Keating (EI6LF) review

---

## 1. Problem statement

Zeus currently requires all features to be compiled into the main application. This approach has several limitations:

- **Maintenance complexity** — Every feature request adds to the core codebase, increasing build times, test surface, and cognitive load.
- **Distribution bloat** — Optional features (MIDI, FT8 integrations, logging assistants, remote control protocols, experimental DSP algorithms) force every user to download and install code they may never use.
- **Third-party contribution friction** — External developers who want to add value to Zeus must fork the entire repository, maintain compatibility with upstream changes, and convince maintainers to merge potentially experimental code.
- **Deployment inflexibility** — Operators running Zeus on resource-constrained systems (Raspberry Pi, older laptops) cannot selectively disable heavyweight features to save CPU/RAM.
- **Innovation pace** — Experimental features (AI voice processing, band-condition analysis, contest integrations) cannot be prototyped and shared without committing to long-term support in the core.

**Issue #106 context:** Brian's comment emphasizes keeping system requirements to an "absolute minimum" and suggests "even the host would be an addon to Zeus not something that ships by default." This implies a desire for an ultra-lean core with optional subsystems, including potentially heavyweight features like the websocket host for TCI/external protocol support.

## 2. Goals

Design and document a plugins system architecture that:

1. **Enables third-party extensibility** — Developers can create, distribute, and sideload plugins without modifying Zeus core.
2. **Maintains lean core** — Optional features move out of the main distribution; operators install only what they need.
3. **Cross-platform safety** — Works reliably on Windows, Linux, and macOS without platform-specific hacks.
4. **Resource governance** — Plugins cannot monopolize CPU, RAM, or crash the host radio application.
5. **Security boundaries** — Plugins have limited access to system resources and sensitive data (credentials, raw radio control).
6. **Developer experience** — Plugin authors have clear APIs, good documentation, and simple distribution paths.
7. **User experience** — Plugin discovery, installation, updates, and debugging are intuitive; "safe mode" recovery is available when plugins misbehave.

## 3. Research: Plugin architectures in similar software

### 3.1 VS Code — Managed extensions in isolated processes

**Architecture:**
- **Language Server Protocol (LSP)** — plugins run as separate Node.js processes communicating via JSON-RPC over stdio/sockets.
- **Extension Host** — a sandboxed Node process that loads extensions; crashes don't kill the editor.
- **Contribution points** — declarative manifest (`package.json`) defines commands, keybindings, menus, views, settings schema.
- **Activation events** — lazy loading (extensions load only when needed: `onLanguage:python`, `onCommand:foo.bar`).
- **Webviews** — plugins can render custom UI via isolated webview iframes with message-passing API.
- **Marketplace** — centralized discovery, versioning, auto-update, ratings, telemetry.

**Resource governance:**
- CPU/memory not enforced — trust-based.
- Crash isolation — extension host crashes → restart, editor stays up.
- Permissions — extensions declare required APIs in manifest (filesystem, network, terminal).

**Pros:** Rich API, excellent DX, proven scale (10,000+ extensions), strong isolation.
**Cons:** Heavy runtime (Node.js + V8 per extension host), complex IPC, overkill for Zeus's scale.

### 3.2 OBS Studio — Native shared libraries (C/C++)

**Architecture:**
- **Shared libraries (`.dll` / `.so` / `.dylib`)** — plugins are compiled native code loaded via `dlopen` / `LoadLibrary`.
- **Plugin API** — C header defines lifecycle hooks (`obs_module_load`, `obs_module_unload`) and callback registration.
- **Sources/Filters/Services** — plugins register new capture sources, video/audio filters, streaming services.
- **No isolation** — plugins run in-process; can crash the host.

**Resource governance:**
- None — plugins have full process access.
- Crash handling — OS-level crash reporting; plugin load failures logged.

**Distribution:**
- Manual install (user drops `.dll` into `obs-plugins/` folder).
- No official marketplace; third-party sites (OBS Forums, GitHub releases).

**Pros:** Minimal overhead, full OBS API access, native performance.
**Cons:** No isolation (crash → host crash), platform-specific compilation, security risk (arbitrary code execution), no versioning/dependency management.

### 3.3 GQRX — Compile-time integration only

**Architecture:**
- No plugin system. Optional features (remote control via TCP, audio output backends) are compile-time options (`cmake -DENABLE_FOO`).

**Pros:** Simple, no runtime overhead.
**Cons:** No third-party extensibility, users must rebuild from source to enable features.

**Relevance:** This is Zeus's current model. GQRX's user base tolerates it because the feature set is stable; Zeus aims for broader extensibility.

### 3.4 SDR++ — Native plugins via shared libraries

**Architecture:**
- **Shared libraries** — `.dll` / `.so` / `.dylib` loaded at startup from `plugins/` directory.
- **Plugin API** — C++ classes (`ModuleManager::Module`) with lifecycle hooks.
- **Types:** Source modules (radio backends), VFO color mappers, recorder formats, decoders.
- **No sandboxing** — in-process, can crash host.

**Distribution:**
- Manual install (user downloads `.so` from GitHub releases).
- No official marketplace.

**Pros:** Simple, native performance, proven in SDR domain.
**Cons:** No isolation, no dependency management, platform-specific builds, limited documentation.

### 3.5 Winamp — Win32 DLLs with exports

**Architecture:**
- **Native plugins** — Win32 DLLs with exported functions (`winampGetInModule2` for input plugins, `winampGetDSPHeader2` for DSP).
- **Types:** Input, output, visualization, DSP, general-purpose.
- **No isolation** — in-process.

**Distribution:**
- Manual `.dll` drop into `Plugins/` folder.
- Third-party sites (Winamp.com plugin gallery, defunct).

**Pros:** Simple Windows-only model, proven longevity (1997–2013 active plugin ecosystem).
**Cons:** No cross-platform support, no isolation, deprecated Win32 APIs.

### 3.6 GIMP — Script-Fu (Scheme) and Python-Fu

**Architecture:**
- **Scripting languages** — Scheme (TinyScheme) and Python (via `pygimp`).
- **Sandboxing** — Limited by language runtime; Python plugins can call arbitrary libraries (no real sandbox).
- **PDB (Procedural Database)** — declarative registry of GIMP functions; scripts call into PDB.
- **No native plugins** — all extensions are interpreted scripts (filters, batch processors, file formats).

**Distribution:**
- Manual `.scm` / `.py` drop into `plug-ins/` folder.
- No marketplace; third-party repositories (GIMP Plugin Registry, GitHub).

**Pros:** Cross-platform, easier than native plugins, no compilation.
**Cons:** Slow (interpreted), limited API (only PDB calls), Python dependency, no process isolation.

### 3.7 Managed Extensibility Framework (MEF) — .NET's plugin system

**Architecture:**
- **Managed assemblies** — plugins are .NET `.dll` files loaded via `AssemblyLoadContext` (ALC).
- **Attributes-based discovery** — `[Export]` / `[Import]` attributes mark plugin contracts and implementations.
- **Composition** — MEF scans assemblies, resolves dependencies, instantiates plugins via DI.
- **Isolation** — ALC allows unloading (with caveats: no static state, no leftover threads).

**Resource governance:**
- None by default — plugins run in-process.
- Can be wrapped in `AppDomain` (.NET Framework) or separate process (.NET Core+).

**Distribution:**
- Manual `.dll` drop, or NuGet packages.

**Pros:** Native to .NET, type-safe, good tooling, supports unloading.
**Cons:** No built-in sandboxing, complex ALC rules, unloading is tricky, no resource limits.

**Relevance:** This is the most natural fit for Zeus (already .NET 8), but requires thoughtful design to avoid pitfalls.

### 3.8 Dalamud (FFXIV plugin framework) — Managed plugins with isolation

**Architecture:**
- **.NET plugins** — C# assemblies loaded into game process.
- **API surface** — strongly-typed API for hooking game events, UI overlays, chat commands.
- **Sandboxing** — limited (plugins are trusted; no resource limits).
- **Distribution** — centralized repository with version checks, auto-update.

**Pros:** Excellent DX for .NET developers, proven in hostile environment (game anti-cheat).
**Cons:** In-process (no isolation), Windows-only via IL weaving.

### 3.9 OpenHPSDR ecosystem — Compile-time modularity

**Research:**
- **Thetis** — No plugin system. Optional features (PureSignal, MIDI, Diversity) are compile-time conditional (`#if PURESIGNAL`).
- **piHPSDR** — No plugin system. Feature flags at build time (`cmake -DGPIO=ON`).
- **Quisk** — No plugin system. Python-based, but extensions require modifying main script.

**Conclusion:** The OpenHPSDR ecosystem has not embraced runtime plugins. Zeus has an opportunity to lead here.

### 3.10 Comparison table

| System | Plugin model | Isolation | Resource limits | Distribution | Cross-platform | DX rating |
|--------|--------------|-----------|-----------------|--------------|----------------|-----------|
| **VS Code** | LSP (separate processes) | Strong (process boundary) | None (trust-based) | Marketplace | Yes | Excellent |
| **OBS Studio** | Native `.dll` / `.so` | None (in-process) | None | Manual | Yes | Medium |
| **GQRX** | Compile-time only | N/A | N/A | Source rebuild | Yes | Poor |
| **SDR++** | Native `.dll` / `.so` | None (in-process) | None | Manual | Yes | Medium |
| **Winamp** | Win32 DLL | None (in-process) | None | Manual | No | Poor (Win32) |
| **GIMP** | Scripting (Scheme, Python) | Limited (runtime) | None | Manual | Yes | Medium |
| **MEF** | .NET assemblies | Optional (ALC) | None | Manual / NuGet | Yes | Good |
| **Dalamud** | .NET in-process | None | None | Repo + auto-update | No | Good |

## 4. Architectural approaches evaluated

### 4.1 No plugin support (status quo)

**Model:** All features built into `Zeus.Server` and `zeus-web` at compile time.

**Pros:**
- Simple — no plugin infrastructure to maintain.
- Fast — no dynamic loading overhead.
- Secure — no third-party code execution.

**Cons:**
- Feature bloat — every request grows the core.
- No third-party innovation — external developers blocked.
- High maintenance — every feature is maintainer responsibility forever.

**Verdict:** Unacceptable per issue requirements.

---

### 4.2 Compile-time plugin integration (conditional compilation)

**Model:** Features gated by `#if FEATURE_MIDI` / `#if FEATURE_FT8`, selected at build time.

**Example:** Thetis's `#if PURESIGNAL`, piHPSDR's `cmake -DGPIO=ON`.

**Pros:**
- No runtime overhead.
- Type-safe (compiler enforces APIs).

**Cons:**
- Users must build from source to enable features.
- No third-party distribution (plugins must be upstreamed to Zeus repo).
- Binary distribution explodes (need separate builds for every feature combination, or ship "everything-enabled" builds → bloat).

**Verdict:** Rejected. Doesn't meet "third-party extensibility" or "lean core" goals.

---

### 4.3 Native plugin model (shared libraries: `.dll` / `.so` / `.dylib`)

**Model:** Plugins are compiled native libraries loaded via `NativeLibrary.Load()` (P/Invoke).

**API surface:**
```csharp
// Zeus.Plugins.Native/PluginApi.h (C exports)
typedef struct {
    const char* name;
    const char* version;
    void (*OnLoad)(void* context);
    void (*OnUnload)();
} ZeusPlugin;

extern "C" __declspec(dllexport) ZeusPlugin* GetPlugin();
```

**Pros:**
- Maximum performance (native code, no marshaling).
- Proven in OBS, SDR++, Winamp.
- Can wrap existing C/C++ libraries (WDSP, codec libraries) without extra runtime.

**Cons:**
- **Platform-specific builds** — plugin authors must compile for Windows x64, Linux x64/ARM, macOS x64/ARM (5 binaries minimum).
- **No isolation** — plugins run in-process; crash → host crash. Memory corruption from plugin → undefined behavior.
- **No managed API** — plugin authors can't use .NET features (LINQ, async/await, SignalR client, LiteDB, etc.). Must P/Invoke everything.
- **ABI fragility** — C struct layout, calling conventions, compiler versions must match. Update `PluginApi.h` → recompile all plugins.
- **No dependency management** — if plugin needs `libfoo.so.1`, user must install it system-wide.
- **Security risk** — arbitrary native code execution; malicious plugin can read memory, hook APIs, exfiltrate data.

**Verdict:** **Rejected** for Zeus core plugins. Too risky, poor DX, platform-specific builds are a support nightmare. *Could* be a fallback for specialized native integrations (e.g., wrapping vendor SDKs), but not the primary model.

---

### 4.4 Managed plugin model (MEF / AssemblyLoadContext)

**Model:** Plugins are .NET assemblies (`.dll`) loaded via `AssemblyLoadContext`. Zeus defines C# interfaces; plugins implement them.

**API surface:**
```csharp
// Zeus.Contracts/Plugins/IZeusPlugin.cs
public interface IZeusPlugin
{
    string Name { get; }
    string Version { get; }
    Task InitializeAsync(IPluginContext context);
    Task ShutdownAsync();
}

public interface IPluginContext
{
    ILogger Logger { get; }
    IServiceProvider Services { get; }  // Access to RadioService, StreamingHub, etc.
    Task<T> GetSettingAsync<T>(string key);
    Task SetSettingAsync<T>(string key, T value);
}
```

**Lifecycle:**
```csharp
// Zeus.Server/Plugins/PluginManager.cs
public class PluginManager : IHostedService
{
    private readonly List<AssemblyLoadContext> _contexts = new();
    private readonly List<IZeusPlugin> _plugins = new();

    public async Task StartAsync(CancellationToken ct)
    {
        var pluginDir = Path.Combine(AppContext.BaseDir, "plugins");
        foreach (var dll in Directory.EnumerateFiles(pluginDir, "*.dll"))
        {
            var alc = new AssemblyLoadContext(dll, isCollectible: true);
            var asm = alc.LoadFromAssemblyPath(dll);
            var pluginType = asm.GetTypes().FirstOrDefault(t => typeof(IZeusPlugin).IsAssignableFrom(t));
            if (pluginType != null)
            {
                var plugin = (IZeusPlugin)Activator.CreateInstance(pluginType)!;
                await plugin.InitializeAsync(_context);
                _plugins.Add(plugin);
                _contexts.Add(alc);
            }
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        foreach (var plugin in _plugins)
            await plugin.ShutdownAsync();
        foreach (var alc in _contexts)
            alc.Unload();
    }
}
```

**Pros:**
- **Native to Zeus's stack** — plugins written in C# using .NET 8, full framework support.
- **Type-safe** — compiler enforces API contracts; IntelliSense works.
- **Cross-platform** — single `.dll` runs on Windows/Linux/macOS (platform-specific code can be gated by `RuntimeInformation.IsOSPlatform`).
- **Good DX** — plugin authors use familiar ASP.NET patterns (DI, async/await, logging, SignalR).
- **Unloadable** — `AssemblyLoadContext` with `isCollectible: true` allows plugin unload (hot-reload, memory reclaim).
- **NuGet integration** — plugins can reference libraries; `deps.json` / `runtimeconfig.json` resolve dependencies.

**Cons:**
- **In-process by default** — no isolation; plugin crash → host crash. Plugin can call `Environment.Exit(0)` or corrupt shared state.
- **Unloading is tricky** — ALC unload requires no live object references, no static state, no leftover threads. Easy to leak.
- **No resource limits** — plugin can spin 100% CPU, allocate GBs of RAM, open 10,000 sockets.
- **Dependency conflicts** — if plugin refs `Newtonsoft.Json 12.0` and Zeus ships `13.0`, collision. ALC isolation helps but not foolproof.
- **Security** — plugin can reflect over Zeus internals, call `File.Delete`, exfiltrate credentials from `CredentialStore`.

**Mitigations (see §5):**
- Run plugins in separate process via .NET IPC (see §4.5).
- Wrap plugin calls in `try/catch` + timeout + cancellation token.
- Scan plugin assembly for dangerous APIs (reflection, filesystem, network) and warn user.
- Settings isolation: plugins get scoped `IPluginContext` with limited API surface, not raw `IServiceProvider`.

**Verdict:** **Primary recommendation** for Zeus. Balances DX, security, and cross-platform support. Requires careful API design to avoid footguns.

---

### 4.5 Process-isolated plugin model (managed plugins in separate process)

**Model:** Extend §4.4's MEF model but run each plugin in a separate `dotnet` process. Zeus communicates via gRPC / named pipes / SignalR.

**Architecture:**
```
Zeus.Server (main process, port 6060)
    ↓ gRPC / named pipe
Zeus.PluginHost (child process, one per plugin)
    └── MyPlugin.dll (IZeusPlugin impl)
```

**API surface (gRPC):**
```protobuf
service PluginHost {
    rpc Initialize(InitRequest) returns (InitResponse);
    rpc Shutdown(ShutdownRequest) returns (Empty);
    rpc HandleRadioEvent(RadioEvent) returns (Empty);
}

message InitRequest {
    string plugin_name = 1;
    map<string, string> settings = 2;
}

message RadioEvent {
    oneof event {
        FrequencyChanged freq_changed = 1;
        MoxStateChanged mox_changed = 2;
    }
}
```

**Lifecycle:**
```csharp
// Zeus.Server/Plugins/PluginProcess.cs
public class PluginProcess : IAsyncDisposable
{
    private Process? _process;
    private GrpcChannel? _channel;
    private PluginHost.PluginHostClient? _client;

    public async Task StartAsync(string pluginPath)
    {
        _process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"Zeus.PluginHost.dll {pluginPath}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });

        // Wait for gRPC server to start (plugin host binds on random port, writes to stdout)
        var port = await ReadPortFromStdout();
        _channel = GrpcChannel.ForAddress($"http://localhost:{port}");
        _client = new PluginHost.PluginHostClient(_channel);

        await _client.InitializeAsync(new InitRequest { PluginName = "MyPlugin" });
    }

    public async ValueTask DisposeAsync()
    {
        await _client.ShutdownAsync(new ShutdownRequest());
        _process?.Kill();
        _process?.Dispose();
    }
}
```

**Pros:**
- **Strong isolation** — plugin crash → child process crash, Zeus stays up.
- **Resource limits** — OS-level CPU/memory limits via cgroups (Linux), Job Objects (Windows), `ulimit` (macOS).
- **Security** — plugin cannot directly access Zeus's memory, `CredentialStore`, or radio control. Must go through gRPC API.
- **Hot-reload** — kill child process, restart with new `.dll`. No ALC unloading complexity.

**Cons:**
- **IPC overhead** — every plugin call crosses process boundary (serialize → deserialize). 50–200 µs latency per call.
- **Complexity** — requires gRPC / named pipe infrastructure, port management, process lifecycle (respawn on crash?).
- **Latency-sensitive paths** — if plugin processes audio samples (e.g., DSP plugin), IPC latency kills real-time performance.
- **Debugging** — harder to attach debugger, trace calls across processes.
- **Startup time** — spawning `dotnet` child process adds 100–500 ms per plugin.

**Use cases:**
- Long-running, coarse-grained plugins: FT8 decoder (subscribes to audio stream, publishes callsigns), logging assistant (QSO database), remote control server (listens on TCP, forwards commands to Zeus).
- NOT suitable for: tight-loop DSP filters, panadapter drawing, VFO tuning handlers (latency kills UX).

**Verdict:** **Recommended for high-risk / heavyweight plugins**. Use as opt-in isolation for untrusted or experimental code. Core plugins (MIDI, TCI, rotctld) stay in-process (§4.4) for performance.

---

### 4.6 Scripting model (Python / Lua / JavaScript)

**Model:** Plugins written in scripting language, executed by embedded interpreter (e.g., Python.NET, NLua, Jint for JavaScript).

**Example (Python via Python.NET):**
```python
# MyPlugin.py
class MyPlugin:
    def initialize(self, ctx):
        ctx.logger.info("Plugin loaded")
        ctx.radio.on_frequency_changed(self.on_freq_change)

    def on_freq_change(self, freq):
        print(f"Freq: {freq}")
```

**Pros:**
- **No compilation** — users edit `.py` / `.lua` files directly.
- **Hot-reload** — change script, restart, no rebuild.
- **Easier for non-C# developers** — lower barrier to entry.

**Cons:**
- **Performance** — interpreted languages 10–100× slower than .NET for CPU-heavy tasks.
- **Runtime dependency** — must bundle Python runtime (100+ MB) or require user install. Lua is lighter (~500 KB) but less popular.
- **Limited API** — interop with .NET objects is clunky (Python.NET marshaling overhead, type mismatches).
- **No strong typing** — runtime errors instead of compile-time checks.
- **Security** — Python's `eval`, `exec`, `__import__` allow arbitrary code execution. Sandboxing is hard.

**Verdict:** **Deferred**. Nice-to-have for simple automation ("change band → log to file"), but not the primary plugin model. Revisit if demand exists.

---

### 4.7 WebAssembly (WASM) plugins

**Model:** Plugins compiled to WASM, run in Wasmtime runtime.

**Pros:**
- Sandboxed by default (WASI capabilities model).
- Cross-platform (any language that compiles to WASM).

**Cons:**
- Immature .NET WASM interop.
- Limited WASI features (no raw sockets, no audio APIs).
- Poor DX for .NET developers (unfamiliar toolchain).

**Verdict:** **Rejected**. Too experimental for Zeus's 2026 timeline. Revisit in 2028+ if WASI matures.

---

## 5. Recommended architecture: Hybrid managed + process-isolated

### 5.1 Core design

**Model:** Managed .NET plugins (§4.4) with optional process isolation (§4.5) for high-risk plugins.

**Principles:**
1. **Default: in-process MEF plugins** — low latency, good DX, suitable for trusted plugins (first-party or vetted third-party).
2. **Opt-in: process-isolated** — for untrusted, experimental, or heavyweight plugins (user explicitly opts in via manifest flag).
3. **API surface: strongly-typed C# interfaces** — `IZeusPlugin`, `IPluginContext`, capability-based permissions.
4. **Resource governance: cooperative + enforcement** — plugins declare resource needs; Zeus monitors and throttles/kills violators.
5. **Distribution: GitHub + sideload** — no official marketplace initially; manual `.dll` install or Git clone + build. Marketplace is future work.

### 5.2 Plugin API layers

#### Layer 1: Core plugin interface

```csharp
// Zeus.Contracts/Plugins/IZeusPlugin.cs
public interface IZeusPlugin
{
    PluginMetadata Metadata { get; }
    Task InitializeAsync(IPluginContext context, CancellationToken ct);
    Task ShutdownAsync(CancellationToken ct);
}

public record PluginMetadata(
    string Id,              // "com.example.myplugin"
    string Name,            // "My Plugin"
    string Version,         // "1.0.0"
    string Author,
    string Description,
    Uri? HomepageUrl,
    PluginCapabilities Capabilities
);

[Flags]
public enum PluginCapabilities
{
    None = 0,
    ReadRadioState = 1 << 0,       // Subscribe to freq/mode/band changes
    ControlRadio = 1 << 1,          // Call RadioService methods (SetVfoA, SetMode, etc.)
    NetworkAccess = 1 << 2,         // Open sockets, HTTP clients
    FileSystemRead = 1 << 3,        // Read arbitrary files
    FileSystemWrite = 1 << 4,       // Write arbitrary files
    ProcessSpawn = 1 << 5,          // Spawn child processes
    AudioStream = 1 << 6,           // Subscribe to RX/TX audio
    ProcessIsolation = 1 << 7       // Run in separate process (plugin requests this)
}
```

#### Layer 2: Plugin context (sandbox boundary)

```csharp
// Zeus.Contracts/Plugins/IPluginContext.cs
public interface IPluginContext
{
    string PluginId { get; }
    ILogger Logger { get; }

    // Settings (scoped to plugin)
    Task<T?> GetSettingAsync<T>(string key);
    Task SetSettingAsync<T>(string key, T value);

    // Radio state (read-only unless ControlRadio capability)
    IRadioStateReader Radio { get; }

    // Conditional access (based on capabilities)
    IRadioController? RadioController { get; }  // null if ControlRadio not granted
    INetworkAccess? Network { get; }            // null if NetworkAccess not granted
    IFileSystemAccess? FileSystem { get; }      // null if FileSystem* not granted
}

public interface IRadioStateReader
{
    Task<RadioState> GetCurrentStateAsync();
    IObservable<FrequencyChanged> OnFrequencyChanged { get; }
    IObservable<ModeChanged> OnModeChanged { get; }
    IObservable<MoxStateChanged> OnMoxStateChanged { get; }
    // ... other events
}

public interface IRadioController
{
    Task SetVfoAAsync(long freqHz);
    Task SetModeAsync(DspMode mode);
    Task SetMoxAsync(bool enabled);
    // ... subset of RadioService's public surface
}
```

#### Layer 3: Extension points (optional interfaces)

```csharp
// Zeus.Contracts/Plugins/IPluginExtensions.cs

// Plugin provides REST endpoints
public interface IPluginHttpEndpoints
{
    void MapEndpoints(IEndpointRouteBuilder endpoints);
}

// Plugin contributes UI panels (web frontend)
public interface IPluginUi
{
    PluginUiManifest GetUiManifest();
}

public record PluginUiManifest(
    string[] JsModules,      // URLs to load: "/plugins/myplugin/ui.js"
    string[] CssFiles,       // "/plugins/myplugin/styles.css"
    PluginPanel[] Panels
);

public record PluginPanel(
    string Id,               // "myplugin.settings"
    string Title,            // "My Plugin Settings"
    string ComponentName,    // React component exported from ui.js
    string Icon              // Lucide icon name
);

// Plugin processes audio
public interface IPluginAudioProcessor
{
    Task ProcessRxAudioAsync(ReadOnlyMemory<float> samples, int sampleRate);
    Task ProcessTxAudioAsync(ReadOnlyMemory<float> samples, int sampleRate);
}

// Plugin handles SignalR messages (e.g., for custom protocols)
public interface IPluginSignalRHandler
{
    Task HandleMessageAsync(byte msgType, ReadOnlyMemory<byte> payload);
}
```

### 5.3 Plugin manifest

Each plugin ships with `plugin.json` alongside its `.dll`:

```json
{
  "id": "com.example.ft8decoder",
  "name": "FT8 Decoder",
  "version": "1.2.0",
  "author": "Jane Operator <jane@example.com>",
  "description": "Decodes FT8 signals from RX audio and logs to WSJT-X UDP protocol",
  "homepage": "https://github.com/janeoperator/zeus-ft8",
  "assembly": "ZeusFt8Decoder.dll",
  "capabilities": [
    "AudioStream",
    "NetworkAccess",
    "FileSystemWrite"
  ],
  "resourceLimits": {
    "maxCpuPercent": 50,
    "maxMemoryMB": 512
  },
  "isolation": "process",  // or "inprocess"
  "dependencies": [
    "System.Text.Json >= 8.0.0",
    "NAudio >= 2.0.0"
  ]
}
```

### 5.4 Plugin lifecycle

```csharp
// Zeus.Server/Plugins/PluginManager.cs (simplified)
public class PluginManager : IHostedService
{
    private readonly List<PluginInstance> _plugins = new();
    private readonly IServiceProvider _services;
    private readonly ILogger<PluginManager> _logger;

    public async Task StartAsync(CancellationToken ct)
    {
        var pluginDir = Path.Combine(AppContext.BaseDir, "plugins");
        if (!Directory.Exists(pluginDir))
        {
            Directory.CreateDirectory(pluginDir);
            return;
        }

        foreach (var manifestPath in Directory.EnumerateFiles(pluginDir, "plugin.json", SearchOption.AllDirectories))
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<PluginManifest>(
                    await File.ReadAllTextAsync(manifestPath, ct));

                if (!ValidateManifest(manifest))
                    continue;

                var instance = manifest.Isolation == "process"
                    ? await LoadProcessIsolatedPluginAsync(manifest, ct)
                    : await LoadInProcessPluginAsync(manifest, ct);

                _plugins.Add(instance);
                _logger.LogInformation("Loaded plugin: {Name} v{Version}", manifest.Name, manifest.Version);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load plugin from {Path}", manifestPath);
            }
        }
    }

    private async Task<PluginInstance> LoadInProcessPluginAsync(PluginManifest manifest, CancellationToken ct)
    {
        var alc = new AssemblyLoadContext(manifest.Id, isCollectible: true);
        var asmPath = Path.Combine(Path.GetDirectoryName(manifest.AssemblyPath)!, manifest.Assembly);
        var asm = alc.LoadFromAssemblyPath(asmPath);

        var pluginType = asm.GetTypes().FirstOrDefault(t => typeof(IZeusPlugin).IsAssignableFrom(t));
        if (pluginType == null)
            throw new InvalidOperationException($"No IZeusPlugin implementation found in {manifest.Assembly}");

        var plugin = (IZeusPlugin)Activator.CreateInstance(pluginType)!;
        var context = new PluginContext(manifest, _services);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));  // init timeout

        await plugin.InitializeAsync(context, cts.Token);

        return new PluginInstance(manifest, plugin, alc, context);
    }

    private async Task<PluginInstance> LoadProcessIsolatedPluginAsync(PluginManifest manifest, CancellationToken ct)
    {
        // Spawn Zeus.PluginHost.exe with gRPC
        // See §4.5 for details
        throw new NotImplementedException("Process isolation not yet implemented");
    }

    public async Task StopAsync(CancellationToken ct)
    {
        foreach (var instance in _plugins)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await instance.Plugin.ShutdownAsync(cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Plugin {Name} shutdown failed", instance.Manifest.Name);
            }
            finally
            {
                instance.LoadContext?.Unload();
            }
        }
    }
}
```

### 5.5 Resource governance

#### 5.5.1 Cooperative limits (manifest-declared)

Plugins declare expected resource usage in `plugin.json`:

```json
"resourceLimits": {
  "maxCpuPercent": 25,      // 25% of one core
  "maxMemoryMB": 256,       // 256 MB RAM
  "maxThreads": 10,         // 10 threads max
  "maxNetworkKBps": 1000    // 1 MB/s network I/O
}
```

**Enforcement:**
- **In-process plugins** — monitored but not hard-enforced. Zeus logs warnings if plugin exceeds limits, shows UI warning, offers "disable plugin" button.
- **Process-isolated plugins** — OS-level enforcement via:
  - Linux: cgroups v2 (`cpu.max`, `memory.max`)
  - Windows: Job Objects (`SetInformationJobObject`)
  - macOS: `setrlimit` (partial; no cgroup equivalent)

#### 5.5.2 Crash isolation

**In-process:**
- Wrap plugin calls in `try/catch`.
- If plugin throws unhandled exception during `InitializeAsync` → skip loading, log error.
- If plugin throws during event handler (e.g., `OnFrequencyChanged`) → log, continue. If 3 consecutive exceptions → disable plugin, notify user.

**Process-isolated:**
- Child process crash → Zeus detects via `Process.Exited` event.
- Options:
  1. Auto-restart (with exponential backoff: 1s, 2s, 4s, ...).
  2. Mark as "faulted," require user intervention to re-enable.

#### 5.5.3 Timeout enforcement

All plugin calls use `CancellationToken` with timeout:

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
await plugin.SomeMethodAsync(cts.Token);
```

If plugin doesn't respect `ct.IsCancellationRequested`, it hangs. Mitigation:
- **In-process:** Limited options (can't kill thread). Log warning, mark plugin as "unresponsive," offer "restart Zeus" button.
- **Process-isolated:** Kill child process after timeout.

#### 5.5.4 Safe mode

**User-initiated:** Hold `Shift` during Zeus startup → skip all plugins, show "Safe Mode" banner in UI.

**Auto-triggered:** If Zeus crashes 3 times within 60 seconds → next start is safe mode (heuristic: corrupted plugin, infinite crash loop).

**UI:** Safe mode banner shows:
- "Plugins disabled. [View plugin diagnostics] [Enable plugins] [Report issue]"
- Plugin diagnostics page lists:
  - Last 10 plugin exceptions
  - Resource usage stats
  - "Disable this plugin" / "Reinstall plugin" buttons

### 5.6 Security model

**Threat model:**
- **Malicious plugin** — intentionally exfiltrates data, deletes files, DoS Zeus.
- **Buggy plugin** — accidentally crashes host, leaks memory, monopolizes CPU.
- **Supply-chain attack** — user downloads `FakeFt8Decoder.dll` from phishing site; contains keylogger.

**Mitigations:**

#### 5.6.1 Capability-based permissions

Plugins declare required capabilities in manifest. Zeus shows permission prompt on first load:

```
"FT8 Decoder" plugin requests:
  ☑ Read radio state (frequency, mode)
  ☑ Access network (UDP to WSJT-X)
  ☑ Write files (logs to ~/.local/share/zeus/ft8-logs/)

[Allow] [Deny] [Always allow]
```

Denied capabilities → plugin's `IPluginContext.Network` is `null`, calls throw `UnauthorizedAccessException`.

#### 5.6.2 Assembly scanning (static analysis)

Before loading plugin, scan assembly for dangerous APIs:

```csharp
private bool IsSuspicious(Assembly asm)
{
    var suspiciousApis = new[]
    {
        "System.Diagnostics.Process.Start",      // Spawn arbitrary processes
        "System.IO.File.Delete",                  // Delete files
        "System.Reflection.Assembly.Load",        // Load arbitrary code
        "System.Runtime.InteropServices.Marshal", // P/Invoke to native
        "System.Environment.Exit"                 // Kill host
    };

    foreach (var type in asm.GetTypes())
    {
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
        foreach (var method in methods)
        {
            var il = method.GetMethodBody()?.GetILAsByteArray();
            if (il != null && ContainsSuspiciousCalls(il, suspiciousApis))
                return true;
        }
    }
    return false;
}
```

If suspicious → warn user, require explicit "I trust this plugin" confirmation.

**Limitation:** Reflection-based calls (`MethodInfo.Invoke`) can bypass static analysis. Not foolproof, but deters casual abuse.

#### 5.6.3 Code signing (future)

- **Short-term:** No signing requirement. Trust is user responsibility (GitHub reputation, maintainer-vetted list).
- **Long-term:** Optional Authenticode (Windows) / codesign (macOS) / GPG signatures. Zeus warns if unsigned. Official "Zeus Plugins" GitHub org publishes signed builds.

#### 5.6.4 Sandboxing (advanced, future)

- **Process isolation + seccomp/AppArmor (Linux)** — restrict syscalls (no `exec`, no raw sockets).
- **gVisor** — run plugin host in lightweight VM.
- **WASM** — migrate to WASI plugins when tooling matures.

**Verdict:** Not for v1. Process isolation (§4.5) is sufficient initial boundary.

### 5.7 Distribution model

#### Phase 1: Sideload only (v1.0)

- **Manual install:** User downloads `MyPlugin.zip` from GitHub releases, extracts to `~/.local/share/zeus/plugins/MyPlugin/`.
- **Structure:**
  ```
  ~/.local/share/zeus/plugins/
    MyPlugin/
      plugin.json
      MyPlugin.dll
      (optional deps: NAudio.dll, etc.)
  ```
- **Discovery:** Zeus scans `plugins/` on startup, shows "Plugins" settings page with enable/disable toggles.

#### Phase 2: Git clone + build (v1.1)

Zeus includes "Install from Git" UI:
1. User pastes GitHub URL: `https://github.com/janeoperator/zeus-ft8`.
2. Zeus runs: `git clone <url> ~/.local/share/zeus/plugin-src/zeus-ft8 && dotnet build`.
3. Copies build output to `plugins/zeus-ft8/`.

**Pros:** Supports open-source plugins without maintainer involvement.
**Cons:** Requires .NET SDK installed on user machine. Security risk (arbitrary `dotnet build` runs untrusted code).

#### Phase 3: Centralized registry (v2.0, future)

- **GitHub repo:** `brianbruff/zeus-plugins` — curated list of vetted plugins.
- **Metadata file:** `registry.json` lists plugins:
  ```json
  {
    "plugins": [
      {
        "id": "com.example.ft8decoder",
        "name": "FT8 Decoder",
        "latestVersion": "1.2.0",
        "downloadUrl": "https://github.com/.../releases/download/v1.2.0/ft8-plugin.zip",
        "sha256": "abc123...",
        "author": "Jane Operator",
        "stars": 42,
        "verified": true
      }
    ]
  }
  ```
- **Zeus UI:** "Browse plugins" tab fetches `registry.json`, shows gallery, one-click install.
- **Auto-update:** Periodically checks for new versions, shows "Update available" notification.

**Maintenance:** Maintainer (Brian) reviews PR submissions to `zeus-plugins`, runs basic security checks (static analysis, malware scan), merges to registry.

### 5.8 Developer experience

#### 5.8.1 Plugin template

Provide `dotnet new` template:

```bash
dotnet new install Zeus.Plugin.Template
dotnet new zeus-plugin -n MyPlugin
cd MyPlugin
dotnet build
```

Generates:
```
MyPlugin/
  MyPlugin.csproj
  Plugin.cs            // Skeleton IZeusPlugin impl
  plugin.json
  README.md            // "How to install this plugin"
```

#### 5.8.2 Documentation site

`docs.zeus-sdr.org/plugins/` includes:
- **Quickstart** — 5-minute "Hello World" plugin.
- **API reference** — Generated from XML docs (via DocFX).
- **Cookbook** — Common recipes:
  - "Subscribe to frequency changes"
  - "Add a REST endpoint"
  - "Render a React settings panel"
  - "Process RX audio samples"
- **Security guide** — Best practices for avoiding dangerous APIs.
- **Packaging guide** — How to publish to GitHub releases.

#### 5.8.3 Sample plugins (in-repo)

`samples/plugins/` directory:
- `HelloWorld/` — Minimal plugin that logs "Hello" on init.
- `FrequencyLogger/` — Logs VFO changes to file.
- `SimpleHttpApi/` — Exposes `/plugins/simple/ping` REST endpoint.
- `PanadapterOverlay/` — Draws custom annotations on panadapter (demonstrates UI extension).

### 5.9 Impact on Zeus architecture

#### 5.9.1 New projects

```
Zeus.Contracts/Plugins/       — Plugin interfaces, DTOs
Zeus.Server/Plugins/          — PluginManager, PluginContext, loader
Zeus.PluginHost/              — Separate process host (for isolated plugins)
Zeus.Plugin.Template/         — dotnet new template
```

#### 5.9.2 Program.cs changes

```csharp
// Add PluginManager as hosted service
builder.Services.AddSingleton<IPluginManager, PluginManager>();
builder.Services.AddHostedService<PluginManager>();

// Map plugin HTTP endpoints
app.MapPluginEndpoints();  // Extension method iterates plugins, calls IPluginHttpEndpoints.MapEndpoints
```

#### 5.9.3 StreamingHub changes

Plugins can subscribe to hub events:

```csharp
// Zeus.Server/StreamingHub.cs
public event Action<DisplayFrame>? DisplayFrameReceived;
public event Action<FrequencyChanged>? FrequencyChanged;
// ... existing events become public for plugins
```

**Alternative:** `IPluginContext.Radio.OnFrequencyChanged` wraps these events, hides hub internals.

#### 5.9.4 Frontend changes (zeus-web)

**Plugin UI loading:**
```typescript
// zeus-web/src/plugins/PluginLoader.tsx
export function PluginLoader() {
  const [plugins, setPlugins] = useState<PluginManifest[]>([]);

  useEffect(() => {
    fetch('/api/plugins')
      .then(r => r.json())
      .then(setPlugins);
  }, []);

  return (
    <>
      {plugins.map(p => (
        p.ui?.jsModules.map(url => (
          <script key={url} src={url} type="module" />
        ))
      ))}
    </>
  );
}
```

**Plugin panels in layout:**
```typescript
// Plugin registers a panel: "myplugin.settings"
// zeus-web dynamically adds tab to settings dock
```

**Security:** Plugin JavaScript runs in same origin as Zeus frontend → can access `fetch('/api/radio/...')`, localStorage, etc. This is acceptable for v1 (trust-based model); future: iframe sandboxing.

#### 5.9.5 Settings storage

Each plugin gets scoped LiteDB collection:

```csharp
// Zeus.Server/Plugins/PluginSettingsStore.cs
public class PluginSettingsStore
{
    private readonly ILiteDatabase _db;

    public T? Get<T>(string pluginId, string key)
    {
        var coll = _db.GetCollection<PluginSetting>($"plugin_{pluginId}_settings");
        var doc = coll.FindOne(x => x.Key == key);
        return doc != null ? JsonSerializer.Deserialize<T>(doc.Value) : default;
    }

    public void Set<T>(string pluginId, string key, T value)
    {
        var coll = _db.GetCollection<PluginSetting>($"plugin_{pluginId}_settings");
        coll.Upsert(new PluginSetting
        {
            Key = key,
            Value = JsonSerializer.Serialize(value)
        });
    }
}
```

**Isolation:** Plugin A cannot read Plugin B's settings.

## 6. Phased implementation plan

### Phase 1: Foundation (4–6 weeks)

**Goals:**
- Basic plugin loading (in-process, MEF-based).
- Skeleton API (`IZeusPlugin`, `IPluginContext`).
- Settings page ("Installed plugins" list, enable/disable toggles).

**Deliverables:**
- `Zeus.Contracts/Plugins/` — interfaces.
- `Zeus.Server/Plugins/PluginManager.cs` — loads `.dll` from `plugins/` dir.
- Sample plugin: `HelloWorldPlugin` (logs "Hello from plugin!" on init).
- REST API: `GET /api/plugins` (list), `POST /api/plugins/{id}/enable`.
- zeus-web: `/settings/plugins` page.

**Non-goals:** Process isolation, resource limits, UI extensions. Just prove plugins can load and call Zeus APIs.

### Phase 2: Capabilities & safety (3–4 weeks)

**Goals:**
- Capability-based permissions (`PluginCapabilities`).
- Crash isolation (wrap plugin calls in `try/catch`).
- Safe mode (skip plugins on startup with `--no-plugins` flag).

**Deliverables:**
- `PluginManifest` with `capabilities` field.
- Permission prompt UI on plugin first load.
- Static analysis: scan for dangerous APIs, warn user.
- Safe mode banner + diagnostics page.

### Phase 3: Extension points (4–6 weeks)

**Goals:**
- Plugins can subscribe to radio events (`OnFrequencyChanged`, etc.).
- Plugins can register REST endpoints (`IPluginHttpEndpoints`).
- Plugins can contribute UI panels (`IPluginUi`).

**Deliverables:**
- `IRadioStateReader` + `IRadioController` in `IPluginContext`.
- `MapPluginEndpoints()` in `Program.cs`.
- Frontend plugin loader (`PluginLoader.tsx`).
- Sample plugin: `FrequencyLoggerPlugin` (logs VFO to file).
- Sample plugin: `SimpleHttpApiPlugin` (exposes `/plugins/ping`).

### Phase 4: Process isolation (6–8 weeks, optional for v1)

**Goals:**
- Run high-risk plugins in separate process.
- gRPC / named pipe communication.
- Resource limits via OS (cgroups, Job Objects).

**Deliverables:**
- `Zeus.PluginHost` console app.
- `PluginProcess` wrapper in `PluginManager`.
- `plugin.json` field: `"isolation": "process"`.
- Sample plugin: `UntrustedPlugin` (marks itself as needing isolation).

**Risk:** Significant complexity. Defer to v2.0 if Phase 1–3 slip.

### Phase 5: Distribution & ecosystem (ongoing)

**Goals:**
- Plugin template (`dotnet new zeus-plugin`).
- Documentation site.
- Sample plugins gallery.
- (Future) Centralized registry.

**Deliverables:**
- `Zeus.Plugin.Template` NuGet package.
- `docs.zeus-sdr.org/plugins/` site (via DocFX).
- 5–10 sample plugins in `samples/plugins/`.
- (v2.0) `brianbruff/zeus-plugins` registry repo.

## 7. Alternatives considered (pros/cons summary)

| Alternative | Pros | Cons | Verdict |
|-------------|------|------|---------|
| **No plugin support** | Simple, no overhead | Feature bloat, no third-party innovation | Rejected (doesn't meet goals) |
| **Compile-time only** | No runtime overhead, type-safe | Users rebuild from source, no distribution | Rejected (poor UX) |
| **Native plugins (.dll/.so)** | Max performance, proven in OBS/SDR++ | Platform-specific builds, no isolation, poor DX | Rejected for core (specialty use only) |
| **Managed plugins (MEF)** | Native to .NET, cross-platform, good DX | In-process (crash risk), no resource limits | **Recommended** (with mitigations) |
| **Process-isolated** | Strong isolation, OS resource limits | IPC overhead, complexity | **Recommended** (opt-in for high-risk) |
| **Scripting (Python/Lua)** | No compilation, hot-reload | Slow, runtime dependency, limited API | Deferred (future nice-to-have) |
| **WebAssembly (WASM)** | Sandboxed, cross-language | Immature .NET interop, limited WASI | Rejected (too early) |

## 8. Risks & open questions

### 8.1 Risks

1. **Plugin crashes Zeus** (in-process model) → Mitigation: extensive `try/catch`, safe mode, diagnostics.
2. **ALC unloading leaks memory** → Mitigation: test hot-reload thoroughly, document ALC rules for plugin authors.
3. **Dependency conflicts** (plugin needs `Newtonsoft.Json 12.0`, Zeus ships `13.0`) → Mitigation: ALC isolation, document "use same versions as Zeus" guideline.
4. **Malicious plugins** → Mitigation: capability permissions, static analysis, code signing (future).
5. **Slow adoption** (no one writes plugins) → Mitigation: excellent docs, templates, sample plugins, maintainer writes first 3 plugins (MIDI, FT8, logging) to prove ecosystem.
6. **Process isolation adds 6–8 weeks** → Mitigation: defer to v2.0 if schedule slips; v1.0 ships in-process only.

### 8.2 Open questions (for maintainer decision)

1. **Is process isolation required for v1.0?** Or can we ship in-process only, defer isolation to v2.0?
2. **Which extension points are must-have for v1.0?** (Radio events? HTTP endpoints? UI panels? Audio processing?)
3. **Plugin directory location:**
   - `~/.local/share/zeus/plugins/` (Linux FHS)
   - `%APPDATA%/Zeus/plugins/` (Windows)
   - `~/Library/Application Support/Zeus/plugins/` (macOS)
   - Or next to `appsettings.json` (portable, but clutters install dir)?
4. **Permission model: opt-in or opt-out?** Should plugins start with zero capabilities (must request each), or full access (user must deny)?
5. **UI framework for plugin panels:** React (matches zeus-web), or web components (plugin-agnostic)?
6. **Auto-update:** Should Zeus phone home to check for plugin updates, or manual-only?

## 9. Success metrics

**Short-term (6 months post-launch):**
- ≥3 third-party plugins published to GitHub.
- ≥50 users install at least one plugin.
- <5% of bug reports are plugin-related crashes (indicates good isolation).

**Long-term (18 months):**
- ≥15 plugins in ecosystem (including experimental: AI DSP, logging, remote control, contest integrations).
- ≥30% of Zeus users have ≥1 plugin installed.
- Maintainer-vetted registry published (`brianbruff/zeus-plugins`).
- Plugin developers report "good DX" (survey or GitHub Discussions feedback).

## 10. Conclusion & recommendation

**Recommended architecture:** Hybrid managed + process-isolated plugin system (§5).

**Key points:**
- **Default: in-process .NET plugins** via MEF for trusted, low-latency use cases (MIDI, TCI, rotctld equivalents).
- **Opt-in: process-isolated** for untrusted or heavyweight plugins (FT8 decoder, AI voice processing).
- **API: strongly-typed C# interfaces** with capability-based permissions.
- **Distribution: sideload first** (manual `.dll` install), registry later (v2.0).
- **Developer experience: prioritize simplicity** — `dotnet new` template, clear docs, sample plugins.

**Phased plan:**
- **Phase 1 (v1.0):** Basic in-process loading, skeleton API, settings UI. (4–6 weeks)
- **Phase 2 (v1.1):** Capabilities, crash isolation, safe mode. (3–4 weeks)
- **Phase 3 (v1.2):** Extension points (radio events, HTTP, UI). (4–6 weeks)
- **Phase 4 (v2.0, optional):** Process isolation. (6–8 weeks)
- **Phase 5 (ongoing):** Templates, docs, registry.

**Next steps:**
1. Maintainer review this PRD, answer open questions (§8.2).
2. Create tracking issue: "Implement plugins system" with sub-issues for each phase.
3. Begin Phase 1: spike `PluginManager` + `HelloWorldPlugin`.

---

**Document history:**
- 2026-04-28: Initial draft (AI research + synthesis).
- Awaiting maintainer (Brian, EI6LF) review.
