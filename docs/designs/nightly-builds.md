# Nightly builds

## Goal

Give end users a stable URL to pick up the latest `develop` tip without waiting
for a tagged release. Run unattended each night and on-demand from the Actions
tab.

## Shape

One new workflow file (`nightly.yml`), one additive change to `release.yml`.
No code outside `.github/workflows/`.

### `release.yml` — additive

Two additions:

1. **`workflow_call:` trigger** with two inputs:
   - `mode` — string, default `dryrun`, values `release | dryrun | nightly`.
     Only the nightly path is new; the existing tag-push and `workflow_dispatch`
     paths keep working unchanged because they don't enter the `workflow_call`
     branch.
   - `ref` — string, default `''`. When non-empty, every `actions/checkout@v4`
     step in the workflow uses `ref: ${{ inputs.ref }}`. When empty, behaviour
     matches today (checks out the triggering SHA).

2. **`determine-version` gains a nightly branch**:
   - Version string: `<VersionPrefix>-nightly.YYYYMMDD.<shortsha>` (e.g.
     `0.7.6-nightly.20260521.abc1234`). `VersionPrefix` stays numeric so
     `AssemblyVersion` keeps validating; the `nightly.YYYYMMDD.sha` text rides
     on `VersionSuffix` and lands in `InformationalVersion`, mirroring the
     existing dry-run path.
   - New output `is-nightly=true`.
   - `is-release` stays `false`, so the existing `create-release` job does not
     fire.

3. **New `create-nightly-release` job** gated on `is-nightly == 'true'`:
   - `gh release delete nightly --yes --cleanup-tag || true` — wipe the rolling
     tag + release so the next step can recreate it pointing at the new SHA.
   - `softprops/action-gh-release@v1` with `tag_name: nightly`,
     `prerelease: true`, `name: 'Openhpsdr Zeus nightly — <YYYY-MM-DD> (<shortsha>)'`.
   - Release notes: short banner ("development build, not a release —
     uninstall before installing a tagged release"), commit list since the most
     recent `v*.*.*` tag, link to the source SHA.
   - Same artifact set as a tagged release: Windows x64/arm64 installers, Linux
     tarball, Linux desktop + server AppImages, macOS arm64 DMG.

### `nightly.yml` — new

```yaml
name: Nightly

on:
  schedule:
    - cron: '0 3 * * *'   # 03:00 UTC
  workflow_dispatch:

permissions:
  contents: write           # required so the called workflow can
                            # create/update the nightly tag + release

concurrency:
  group: nightly            # manual dispatch waits behind cron run
  cancel-in-progress: false

jobs:
  nightly:
    uses: ./.github/workflows/release.yml
    with:
      mode: nightly
      ref: develop
    permissions:
      contents: write
```

## Why `ref: develop` is explicit

GitHub fires `schedule:` triggers only on the **default branch**
(`main` per `gh repo view`). When the cron fires, `github.ref` is
`refs/heads/main` and a plain `actions/checkout@v4` would build main, not
develop. Passing `ref: develop` through the workflow_call input forces every
checkout in `release.yml` to use develop regardless of which branch the trigger
came from.

Same mechanism covers manual `workflow_dispatch`: it always builds develop tip,
not whatever branch the user dispatched from.

## Operational caveats

- **Schedule activation lag.** GitHub only honours `schedule:` for workflow
  files on the default branch. `nightly.yml` lands on develop in this PR and
  starts firing on cron only after the next develop → main merge train.
  Manual `workflow_dispatch` works immediately from develop.
- **Rolling tag means git tag history is lossy.** The `nightly` tag is force-
  recreated each run. If a user references a specific nightly by tag they need
  to use the dated artifact name embedded in the version string, not the tag.
- **Concurrency.** `cancel-in-progress: false` means if cron fires while a
  manual dispatch is still building, the cron run queues behind it rather than
  killing it. Avoids producing a half-baked nightly when a maintainer is
  iterating.

## Rollback

Revert the PR. The dangling `nightly` tag + release can be removed manually:

```bash
gh release delete nightly --cleanup-tag
```

## Out of scope

Red-light per `CLAUDE.md` — propose, don't ship in this PR:

- Code-signing nightly installers (macOS notarisation, Windows Authenticode).
- Auto-posting nightly availability to Slack/Discord/email.
- README/wiki link to the nightly URL (visible UX change, maintainer call).
- Pruning old nightly assets older than N days (current design overwrites the
  single rolling release so there's nothing to prune).
