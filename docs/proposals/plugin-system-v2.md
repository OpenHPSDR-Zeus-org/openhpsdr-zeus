# Plugin System v2 — Unified Architecture Decision Record

**Status:** Accepted 2026-05-17 (maintainer: Brian, EI6LF)
**Supersedes:** `docs/prds/plugins-system-architecture.md`, `docs/proposals/vst-host.md`, `docs/proposals/vst-host-phase2-wire.md`
**Worktree:** `OPENHPSDR-Zeus.Worktrees/feature_plugin_system` on branch `feature/plugin-system`
**Registry repo:** `github.com/OpenHPSDR-Zeus-org/openhpsdr-zeus-plugins`

---

## 1. Why a rebuild

Two prior workstreams produced incompatible architectures:

- **`docs/prds/plugins-system-architecture.md`** — managed .NET in-process plugins (MEF/`AssemblyLoadContext`) with optional process isolation. Targeted backend extensibility (MIDI, TCI bridges, FT8). Never landed past skeleton on `feature/plugins-foundation`.
- **`docs/proposals/vst-host.md` + `vst-host-phase2-wire.md`** — out-of-process C++ sidecar for VST3/VST2/CLAP audio plugins. Landed on `develop` through Phase 3a (8-slot chain, editor windows, TX wiring, /api/capabilities) but never integrated with a browsable plugin system.

Neither workstream produced a "plugin" the Zeus client could browse and install. Visual + backend extensions (e.g. a new amplifier panel) had no home. The maintainer requirement — *one* unified plugin SDK with a browsable registry and BYOP — needs a clean restart.

## 2. Locked decisions

| Decision | Choice | Rationale |
|---|---|---|
| Plugin runtime | **In-process** .NET via `AssemblyLoadContext` (collectible) | Native to Zeus stack, low latency, cross-platform. Unload support for hot-reload. |
| Audio (VST) hosting | **In-process** via small C++ bridge library (P/Invoke from .NET) | Maintainer accepted 64-bit-only constraint and dropping VST2. Removes sidecar IPC complexity. VST3 SDK (MIT) + CLAP SDK (MIT) only. |
| Plugin packaging | Single zip with `plugin.json` + assembly + optional `ui/*.es.js` + optional `vst3/*.vst3` | One artifact, one drag-and-drop install. |
| Registry | **Separate repo, HTTPS-fetched**: `OpenHPSDR-Zeus-org/openhpsdr-zeus-plugins` | Lightweight; community contributions via PR to that repo, not Zeus core. |
| Bring-your-own-plugin | First-class: install by URL or local zip with optional SHA256 pinning | No registry round-trip required. |
| Versioning | Plugin declares `sdk.abi` (exact match) + `sdk.minVersion` (SemVer) | ABI version for breaking changes; SemVer for additive. |
| Settings storage | Scoped LiteDB collection per plugin id | One LiteDB seam, isolation between plugins. |
| Permissions | Manifest-declared capability flags; first-load prompt; runtime null-services for ungranted caps | User-visible, no surprise behavior. |
| Crash isolation | `try/catch` + `CancellationToken` timeout around every plugin call; 3-strikes auto-disable; safe-mode startup | Acceptable for v1; process isolation deferred. |
| Editor windows (VST GUI) | **Desktop mode only** (Photino host can host native windows); browser mode shows parameters only | Reparenting native windows into a browser is intractable; parameter sliders cover 90% of use. |

## 3. Architecture

### 3.1 Project layout (server side)

```
Zeus.Plugins.Contracts/          # public surface — referenced by plugin authors
  IZeusPlugin.cs                 # required interface
  IPluginContext.cs              # services exposed to plugins
  PluginManifest.cs              # plugin.json POCO + JSON schema
  PluginCapabilities.cs          # capability flags + permission model
  AbiVersion.cs                  # static SDK ABI version
  Extensions/
    IBackendPlugin.cs            # adds HTTP endpoints / event handlers
    IUiPlugin.cs                 # declares JS modules + panel contributions
    IAudioPlugin.cs              # processes RX/TX float32 blocks
  Audio/
    AudioBlock.cs                # passed to IAudioPlugin
    IAudioHost.cs                # what IAudioPlugin sees from the host
  Registry/
    RegistryCatalog.cs           # registry.json POCO
    PluginEntry.cs

Zeus.Plugins.Host/               # internal — Zeus's plugin manager
  PluginManager.cs               # IHostedService, top-level orchestrator
  PluginLoader.cs                # ALC + manifest validation + activation
  PluginContext.cs               # IPluginContext impl
  PluginSettingsStore.cs         # scoped LiteDB
  Registry/
    IRegistryClient.cs
    HttpRegistryClient.cs        # GET registry.json over HTTPS
    PluginInstaller.cs           # BYOP: unzip + verify + place
  Audio/
    VstHostBridge.cs             # P/Invoke wrapper around native bridge
    VstHostAudioPlugin.cs        # IAudioPlugin impl driven by manifest's vst3Path
  Endpoints.cs                   # GET /api/plugins, POST /api/plugins/install, etc.
  ServiceCollectionExtensions.cs

native/zeus-vst-bridge/          # C++ native library, in-process VST3/CLAP host
  CMakeLists.txt
  src/bridge.cpp                 # C ABI exports (zvst_*)
  third_party/                   # vst3sdk (MIT), clap (MIT) as submodules / fetched
```

### 3.2 Project layout (frontend)

```
zeus-web/src/plugins/
  PluginBrowser.tsx              # /settings/plugins/browse
  InstalledPlugins.tsx           # /settings/plugins
  InstallFromUrl.tsx             # BYOP modal
  PluginPanelHost.tsx            # mounts plugin-contributed panels into slots
  plugin-api.ts                  # the api object plugin JS modules see
  registry-store.ts              # zustand store for registry catalog + installed
  api/plugins.ts                 # /api/plugins client
```

### 3.3 Manifest schema (`plugin.json`)

```json
{
  "schemaVersion": 1,
  "id": "com.example.amplifier",
  "name": "Amplifier Control",
  "version": "1.0.0",
  "author": "Jane Operator <jane@example.com>",
  "description": "Monitors and controls an external HF amplifier.",
  "homepage": "https://github.com/example/zeus-amp",
  "license": "GPL-2.0-or-later",
  "sdk": { "abi": 1, "minVersion": "1.0.0" },
  "entrypoint": { "assembly": "Amplifier.dll", "type": "Example.AmplifierPlugin" },
  "capabilities": ["ReadRadioState", "ControlRadio"],
  "permissions": { "network": false, "fileSystemRead": false, "fileSystemWrite": false },
  "ui": {
    "modules": ["ui/amplifier.es.js"],
    "panels": [
      { "id": "amp.main", "title": "Amplifier", "icon": "Zap", "slot": "workspace.amplifier" }
    ]
  },
  "audio": null
}
```

`audio` example for a VST-hosting plugin (no C# code required):

```json
"audio": {
  "vst3Path": "vst3/MyEffect.vst3",
  "slot": "tx.post-leveler",
  "channels": 1,
  "sampleRate": 48000
}
```

### 3.4 SDK ABI + version compatibility

- `Zeus.Plugins.Contracts.AbiVersion.Current = 1` (integer constant in the contracts assembly).
- `Zeus.Plugins.Contracts.SdkVersion.Current = "1.0.0"` (SemVer string).
- A plugin loads iff:
  - `plugin.sdk.abi == host.AbiVersion` (exact)
  - SemVer(`plugin.sdk.minVersion`) `<=` SemVer(`host.SdkVersion`)
  - AND major(`plugin.sdk.minVersion`) `==` major(`host.SdkVersion`)
- Breaking change to any interface in `Zeus.Plugins.Contracts` ⇒ bump ABI to 2 ⇒ old plugins rejected at load.
- Additive change (new optional method via default-interface-impl, new manifest field) ⇒ bump minor of `SdkVersion`. Plugins built against 1.0.0 keep working.

### 3.5 Registry format (`registry.json`)

```json
{
  "schemaVersion": 1,
  "generated": "2026-05-17T12:00:00Z",
  "plugins": [
    {
      "id": "com.example.amplifier",
      "name": "Amplifier Control",
      "description": "Monitors and controls an external HF amplifier.",
      "author": "Jane Operator",
      "license": "GPL-2.0-or-later",
      "homepage": "https://github.com/example/zeus-amp",
      "categories": ["amplifier", "control"],
      "verified": false,
      "versions": [
        {
          "version": "1.0.0",
          "sdkAbi": 1,
          "sdkMinVersion": "1.0.0",
          "platforms": ["any"],
          "downloadUrl": "https://github.com/example/zeus-amp/releases/download/v1.0.0/amplifier-1.0.0.zip",
          "sha256": "abcdef0123..."
        }
      ]
    }
  ]
}
```

`platforms` is `any` for managed-only plugins; `linux-x64` / `win-x64` / `osx-arm64` for plugins that bundle a native VST. Zeus filters to the host's RID.

### 3.6 Bring-your-own-plugin

`POST /api/plugins/install` body variants:

```json
{ "source": "url", "url": "https://...", "sha256": "..." }
{ "source": "file", "filePath": "/abs/path/to/plugin.zip" }
{ "source": "registry", "id": "com.example.amplifier", "version": "1.0.0" }
```

Installer:
1. Fetch zip (HTTPS only, no `http://`).
2. Verify SHA256 if supplied. Reject mismatch.
3. Validate `plugin.json` against schema + SDK ABI.
4. Extract to `{pluginRoot}/{plugin-id}/`. Overwrite if same id exists.
5. Call `PluginManager.ReloadAsync()`. Idempotent.

### 3.7 Plugin storage

| Platform | Root |
|---|---|
| Linux | `${XDG_DATA_HOME:-$HOME/.local/share}/zeus/plugins/` |
| macOS | `~/Library/Application Support/Zeus/plugins/` |
| Windows | `%APPDATA%/Zeus/plugins/` |

One subdirectory per plugin id. Override via `appsettings.json:Plugins.RootPath`.

### 3.8 Lifecycle

```
StartAsync(IHostedService) →
  scan plugin root →
    for each plugin.json:
      validate schema, ABI, capabilities →
      check user's allow-list (LiteDB) →
      create ALC(collectible=true) →
      load assembly, find IZeusPlugin →
      build IPluginContext (capability-scoped) →
      call IZeusPlugin.InitializeAsync(ctx, ct=10s) under try/catch →
      register in _running list
  expose REST API

StopAsync →
  for each running:
    plugin.ShutdownAsync(ct=5s) under try/catch
    alc.Unload()
    GC.Collect (best-effort)
```

### 3.9 Audio extension (in-process VST hosting)

```csharp
public interface IAudioPlugin
{
    string DisplayName { get; }
    AudioPluginRequirements Requirements { get; }   // sample rate, channels, block size
    Task InitializeAudioAsync(IAudioHost host, CancellationToken ct);
    void Process(Span<float> input, Span<float> output, AudioBlockContext ctx);
    Task ShutdownAudioAsync(CancellationToken ct);
}
```

A plugin can either implement `IAudioPlugin` directly (custom DSP in C#) **or** declare `audio.vst3Path` in `plugin.json` to get a `VstHostAudioPlugin` synthesized for it. The native bridge (`native/zeus-vst-bridge/`) is a small C library:

```c
// C ABI — versioned by header constant ZVST_ABI = 1
int32_t zvst_init(uint32_t abi);
int32_t zvst_load_vst3(const char* path, int32_t channels, int32_t sampleRate, int32_t blockSize, void** outHandle);
int32_t zvst_process(void* handle, const float* in, float* out, int32_t frames);
int32_t zvst_set_param(void* handle, uint32_t paramId, double normalized);
int32_t zvst_unload(void* handle);
int32_t zvst_shutdown();
```

The .NET wrapper marshals these via `LibraryImport`. Audio thread takes no locks beyond a single `Volatile.Read` of a "chain enabled" flag — bit-identical pass-through when disabled.

### 3.10 UI extension (frontend plugin contract)

Plugin ships one or more ESM modules. Frontend dynamically imports them; module's default export receives a `ZeusPluginApi` object:

```ts
// ui/amplifier.es.js
import { AmplifierPanel } from './AmplifierPanel.tsx';

export default function register(api) {
  api.registerPanel({
    id: 'amp.main',
    component: AmplifierPanel,
  });
}
```

`ZeusPluginApi` surface (versioned by `ZEUS_PLUGIN_API_VERSION` exported from the host shell):
- `registerPanel(spec)`
- `registerSlotComponent(slot, component)`
- `useRadioState()` — React hook
- `callBackend(method, path, body)` — calls `/api/plugins/{thisPluginId}{path}`
- `subscribe(eventName, handler)`

CSP/sandbox: same origin for v1 (trust-based). Future: iframe + postMessage.

### 3.11 Capabilities

```csharp
[Flags]
public enum PluginCapabilities {
    None             = 0,
    ReadRadioState   = 1 << 0,
    ControlRadio     = 1 << 1,
    AudioStream      = 1 << 2,
    NetworkAccess    = 1 << 3,
    FileSystemRead   = 1 << 4,
    FileSystemWrite  = 1 << 5,
    PersistSettings  = 1 << 6,   // granted by default to all plugins
}
```

`IPluginContext.RadioController` is null unless `ControlRadio` granted. Ungranted capabilities surface as null services + throw `PluginPermissionException` if the plugin reflects them.

## 4. Iteration plan

Tracked in the team's task list. Each iteration ⇒ one or more commits on `feature/plugin-system`, push notification, no PR until maintainer review.

| Iter | Deliverable |
|---|---|
| **1** | Drift cleanup. This ADR. `Zeus.Plugins.Contracts` skeleton compiling green with stub tests. |
| **2** | `Zeus.Plugins.Host` with ALC loader + manifest validator + REST endpoints. |
| **3** | `HelloWorld` + `Amplifier` sample plugins (backend + frontend). |
| **4** | `Zeus.Plugins.Registry` HTTP client + BYOP installer. |
| **5** | Frontend Plugin Browser UI + Playwright integration tests. |
| **6** | `IAudioPlugin` contract + `zeus-vst-bridge` C skeleton + stub P/Invoke. |
| **7** | Native bridge implements VST3 load + process; integration test passes. |
| **8** | Bootstrap `OpenHPSDR-Zeus-org/openhpsdr-zeus-plugins` repo with schema, CI, seed entries. |
| **9** | Docs (`docs/plugins/`), final polish, all tests green. |

## 5. Risks + open questions

1. **ALC unload leaks** — well-known .NET pitfall. Mitigation: explicit `IDisposable` discipline in `IZeusPlugin.ShutdownAsync`, no static state, document the rules for plugin authors. Unload is best-effort; the host doesn't depend on it for correctness.
2. **Native bridge build complexity** — vst3sdk requires CMake + a C++17 toolchain. Mitigation: vendor the SDK as a submodule; ship prebuilt bridge binaries in Zeus releases; CI matrix builds them.
3. **Editor windows in browser mode** — out of scope for v1 (parameter sliders only).
4. **Permission model UX** — first-load prompt vs deny-by-default. Default to "prompt on capability grant" matching mobile-app patterns.
5. **Registry trust** — `verified: true` flag is honor-system for v1. Code signing deferred.
6. **Performance budget** — plugin call latency target: <50 µs per audio block for in-process audio plugins (no IPC). Measured in iter 7.

## 6. Non-goals (out of scope for this workstream)

- WebAssembly plugins
- Python / Lua scripting
- Process isolation
- AU on macOS
- LV2 on Linux
- 32-bit VST loading (use `Carla` or a separate bridge process if needed; not a Zeus problem)
- Code signing
- Auto-update beacons

## 7. Compatibility with develop's existing surface

This workstream removes:
- `Zeus.PluginHost/` project (sidecar host)
- `tools/Zeus.PluginScanner/`
- `tests/Zeus.PluginHost.Tests/`
- `Zeus.Server.Hosting/VstHost*.cs`, `VstChainStore.cs`, `VstHostHostedService.cs`
- `Zeus.Contracts/VstHostEventFrame.cs` + related msg-types
- All `zeus-web/src/components/VstHost*.tsx` and `state/vst-host-store.ts`

`/api/capabilities` survives but the `vstHost` node is removed. Existing TX path is unchanged: no VST seam until iter 7 lands a new one through the new SDK.
