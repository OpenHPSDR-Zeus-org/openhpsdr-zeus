# Plugin Author Guide

This guide takes you from zero to a loaded Openhpsdr-Zeus plugin in about ten minutes. For the full architecture, see `docs/proposals/plugin-system-v2.md`.

## 1. What a plugin can do

A plugin is a .NET 10 class library that ships as a single zip containing:
- `plugin.json` — manifest (see `docs/plugins/plugin.schema.json`)
- `YourPlugin.dll` — your assembly + any dependencies
- `ui/*.es.js` (optional) — frontend ESM modules
- `vst3/*.vst3` (optional) — bundled VST3 plugin

Every plugin implements `IZeusPlugin`. Three optional extension interfaces let it do more:

| Extension | What it adds |
|---|---|
| `IBackendPlugin` | HTTP endpoints under `/api/plugins/{your-id}/...` |
| `IUiPlugin` | Gate UI module loading on runtime state (most plugins don't need this — declare `ui` in manifest instead) |
| `IAudioPlugin` | Real-time RX/TX audio processing in C# |

For VST3 hosting without writing audio code, declare `audio.vst3Path` in your manifest; the host wraps it automatically.

## 2. Minimum viable plugin

### Project file

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Openhpsdr.Zeus.Plugins.Contracts" Version="1.0.*" />
  </ItemGroup>
</Project>
```

### Plugin code

```csharp
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;

namespace Example;

public sealed class HelloWorldPlugin : IZeusPlugin
{
    private IPluginContext? _ctx;

    public Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _ctx = context;
        context.Logger.LogInformation("Hello, Zeus!");
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken ct)
    {
        _ctx?.Logger.LogInformation("Goodbye");
        return Task.CompletedTask;
    }
}
```

### Manifest (`plugin.json`)

```json
{
  "schemaVersion": 1,
  "id": "com.example.helloworld",
  "name": "Hello World",
  "version": "1.0.0",
  "author": "Your Name",
  "license": "GPL-2.0-or-later",
  "sdk": { "abi": 1, "minVersion": "1.0.0" },
  "entrypoint": { "assembly": "HelloWorld.dll", "type": "Example.HelloWorldPlugin" }
}
```

### Build + install

```bash
dotnet publish -c Release -o publish/
cd publish/
zip -r helloworld-1.0.0.zip plugin.json HelloWorld.dll
```

In Zeus: **Settings → Plugins → Install from file…** point at the zip. The plugin loads, the log line appears in the server output, and `GET /api/plugins` lists it.

## 3. Adding HTTP endpoints

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Extensions;

public sealed class AmpPlugin : IZeusPlugin, IBackendPlugin
{
    private int _powerWatts = 0;

    public Task InitializeAsync(IPluginContext ctx, CancellationToken ct) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Final URL: /api/plugins/com.example.amp/status
        endpoints.MapGet("status", () => Results.Ok(new { powerWatts = _powerWatts }));
        endpoints.MapPost("power", (PowerRequest r) => { _powerWatts = r.Watts; return Results.NoContent(); });
    }

    private record PowerRequest(int Watts);
}
```

The endpoint route builder you receive is already scoped under your plugin id, so `MapGet("status", ...)` lands at `/api/plugins/{your-id}/status`. Do NOT prefix your routes with `/api/...` yourself.

## 4. Adding a UI panel

```json
"ui": {
  "modules": ["ui/amp.es.js"],
  "panels": [
    { "id": "amp.main", "title": "Amplifier", "icon": "Zap", "slot": "workspace.amplifier" }
  ]
}
```

```ts
// ui/amp.es.js — built from a tsx source with `vite build --target esnext --format es`
import { AmpPanel } from './AmpPanel';

export default function register(api) {
  api.registerPanel({ id: 'amp.main', component: AmpPanel });
}
```

The host's `ZeusPluginApi` (passed to your `register` function) exposes:
- `registerPanel({ id, component })` — attach a React component to a panel id from your manifest
- `useRadioState()` — React hook subscribing to frequency / mode / MOX
- `callBackend(method, path, body?)` — call into your plugin's own HTTP endpoints
- `subscribe(eventName, handler)` — listen for plugin-scoped events

Frontend modules run in the same origin as Zeus and can `import { tokens } from '/styles/tokens.css'` for theming.

## 5. Capabilities

Declare what your plugin needs in the manifest:

```json
"capabilities": ["ReadRadioState", "ControlRadio", "NetworkAccess"]
```

Zeus prompts the user on first load. Ungranted capabilities surface as `null` on `IPluginContext`:

```csharp
if (ctx.RadioController is null)
{
    ctx.Logger.LogWarning("ControlRadio not granted — read-only mode");
    return;
}
await ctx.RadioController.SetFrequencyAsync(14_250_000, ct);
```

Settings persistence (`PluginCapabilities.PersistSettings`) is granted automatically to every plugin. The other capabilities are opt-in.

## 6. VST3 hosting (no audio code needed)

Bundle a `.vst3` file inside your zip and reference it from the manifest:

```json
"audio": {
  "vst3Path": "vst3/SuperEffect.vst3",
  "slot": "tx.post-leveler",
  "channels": 1,
  "sampleRate": 48000
}
```

Zeus loads the VST3 in-process via the native bridge. The plugin appears in the chain UI under your plugin name. Parameter sliders are generated from the VST3 controller introspection.

If you need custom DSP instead, implement `IAudioPlugin` — see `docs/plugins/audio-plugins.md`.

## 7. Publishing to the registry

PR your plugin entry to `github.com/OpenHPSDR-Zeus-org/openhpsdr-zeus-plugins`:

```json
{
  "id": "com.example.amp",
  "name": "Amplifier Control",
  "description": "...",
  "license": "GPL-2.0-or-later",
  "homepage": "https://github.com/example/zeus-amp",
  "categories": ["amplifier"],
  "verified": false,
  "versions": [
    {
      "version": "1.0.0",
      "sdkAbi": 1,
      "sdkMinVersion": "1.0.0",
      "platforms": ["any"],
      "downloadUrl": "https://github.com/example/zeus-amp/releases/download/v1.0.0/amp-1.0.0.zip",
      "sha256": "<output of sha256sum>"
    }
  ]
}
```

A GitHub Action validates the schema and checksums on every PR.

Authors who want to skip the registry can ship a direct URL and instruct operators to use **Install from URL…** with the SHA256.

## 8. Versioning rules

- Bump **patch** (`1.0.0 → 1.0.1`) for bug fixes — operators auto-update.
- Bump **minor** (`1.0.0 → 1.1.0`) for additive features — operators see "Update available" notification.
- Bump **major** (`1.0.0 → 2.0.0`) for breaking changes — operators must opt-in.
- The host's SDK ABI is bumped only when the contracts assembly changes shape. Your plugin's `sdk.abi` must equal the host's value or it will not load.

## 9. Debugging tips

- Logs from your plugin are tagged `[plugin/your-id]` and emitted to the same log sink as Zeus.
- Add `?--no-plugins` to the server command line or hold **Shift** during startup to enter safe mode — every plugin is skipped, including yours.
- Tests can construct an in-memory `PluginContext` from `Zeus.Plugins.Host.Testing` (added in iter 9).

## 10. Where to go next

- `docs/plugins/manifest-spec.md` — every manifest field
- `docs/plugins/capabilities.md` — what each capability gates
- `docs/plugins/audio-plugins.md` — `IAudioPlugin` deep dive
- `docs/plugins/registry.md` — registry contract and PR flow
- [`OpenHPSDR-Zeus-org/openhpsdr-zeus-plugins/samples/`](https://github.com/OpenHPSDR-Zeus-org/openhpsdr-zeus-plugins/tree/main/samples) — `HelloWorld` and `Amplifier` reference implementations in the plugin registry repo
