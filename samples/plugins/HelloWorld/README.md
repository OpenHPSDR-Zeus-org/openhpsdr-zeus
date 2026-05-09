# HelloWorld — Zeus plugin sample

Minimal `IZeusPlugin` implementation. Use this as the starting point for new plugins and as the canonical "does my install work?" check.

## Build

From the repo root:

```bash
dotnet build samples/plugins/HelloWorld/HelloWorld.csproj
```

The build output lands in `samples/plugins/HelloWorld/bin/<Configuration>/net10.0/`.

## Install

Copy the build output into the per-platform Zeus plugin directory:

| Platform | Plugin directory |
|----------|------------------|
| Linux    | `~/.local/share/zeus/plugins/HelloWorld/`           |
| macOS    | `~/Library/Application Support/Zeus/plugins/HelloWorld/` |
| Windows  | `%APPDATA%\Zeus\plugins\HelloWorld\`                |

The directory must contain at minimum:

- `plugin.json` — the manifest
- `HelloWorld.dll` — the assembly

When Zeus starts, it scans the plugin directory and loads each manifest. On success you'll see a log line like:

```
info: Plugin.com.openhpsdr.zeus.helloworld[0] Hello from com.openhpsdr.zeus.helloworld v1.0.0!
```

## Disable plugins (safe mode)

Pass `--no-plugins` to Zeus, or set `ZEUS__Plugins__Disabled=true` in the environment. Useful for diagnosing whether a plugin is the cause of an issue.
