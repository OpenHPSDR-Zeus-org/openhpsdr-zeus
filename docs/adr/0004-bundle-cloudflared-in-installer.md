# Bundle `cloudflared` in the Zeus installer

The `cloudflared` binary ships inside the Zeus desktop installer (~30 MB per platform/arch) rather than being downloaded on first use or installed by the user separately. The desktop spawns it as a subprocess and captures the random `*.trycloudflare.com` URL from its output.

## Considered options

- **Download `cloudflared` on first "Go Online"** — rejected. First-run reliability matters more than installer size, and a "downloading cloudflared… network error" experience on first connect breaks the dream UX. It also adds Gatekeeper / SmartScreen trust questions for a binary delivered post-install.
- **Require the user to install `cloudflared` via Homebrew / Scoop / apt** — rejected. Highest user friction; eliminates non-technical operators entirely.

## Consequences

- Installer grows by ~30 MB per platform/arch — acceptable given Zeus already ships .NET runtime, WDSP, and miniaudio.
- The macOS .app bundle must notarize `cloudflared` along with the rest of the binary. Cloudflare ships unsigned `.tgz` releases, so the Zeus build pipeline must re-sign and notarize. `~/zeus-sign-notarize.sh` and `create-macos-app.sh` need to learn about the embedded binary.
- The Windows installer must Authenticode-sign the embedded `cloudflared.exe` as part of the existing `release.yml` flow.
- CVE response cadence becomes "ship a Zeus point release with bumped `cloudflared`." Acceptable given `cloudflared`'s low CVE frequency.
- The bundled `cloudflared` version is pinned in the build pipeline (downloaded at build time, not committed to the repo).
