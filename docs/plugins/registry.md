# Plugin registry

Openhpsdr-Zeus pulls its plugin catalog from a separate repository at
**[OpenHPSDR-Zeus-org/openhpsdr-zeus-plugins](https://github.com/OpenHPSDR-Zeus-org/openhpsdr-zeus-plugins)**.

The curator (Brian Keating, EI6LF) will transfer the repo to KB2UKA at
a later date.

## Where Zeus fetches from

Default catalog URL:

```
https://raw.githubusercontent.com/OpenHPSDR-Zeus-org/openhpsdr-zeus-plugins/main/registry.json
```

This is hard-coded as `HttpRegistryClient.DefaultUrl` in
`Zeus.Plugins.Host/Registry/HttpRegistryClient.cs`. Operators can
override by injecting a `RegistryClientOptions` with a custom
`SourceUrl` (useful for a self-hosted fork or a private mirror).

The catalog is fetched on demand from **Settings → Plugins → Browse**
and cached for five minutes; the **Reload** button invalidates the
cache.

## Bring-your-own-plugin

The registry is **not** the only install path. Operators who want a
plugin that isn't (yet) listed can use:

- **Settings → Plugins → Install from URL…** — paste any HTTPS URL
  pointing at a Zeus plugin zip. An optional SHA-256 is verified
  before extraction.
- `POST /api/plugins/install` body `{"source":"file","filePath":"/abs/path/to/plugin.zip"}`
  — sideload from a local zip.

The validation pipeline (manifest schema check + ABI compatibility +
zip-slip guards) runs the same in both registry and BYOP paths.

## What the registry validates on every PR

Three CI jobs in the registry repo's
`.github/workflows/validate.yml`:

1. **schema** — `registry.json` validates against
   `schema/registry.schema.json`.
2. **checksums** — every `downloadUrl` is downloaded and its SHA-256
   compared against the declared `sha256`.
3. **manifest** — the zip is extracted, the embedded `plugin.json` is
   validated against `schema/manifest.schema.json`, and the embedded
   `id` + `version` must match the registry entry.

Failed CI blocks the PR. The curator reviews + merges only green PRs.

## What you can ship in the registry

- Any FSF/OSI-approved license, declared in `plugin.json`.
- Native-bundling plugins must declare correct RIDs in
  `platforms` (`linux-x64`, `osx-arm64`, `win-x64`, etc.) so Zeus's
  catalog filter shows them on compatible hosts only.
- Plugins built against the **current** SDK ABI (currently `1`).
  Plugins targeting older or newer ABIs will not load on Zeus
  clients with this version of the host.

See the [author guide](author-guide.md) for the full plugin shape.
