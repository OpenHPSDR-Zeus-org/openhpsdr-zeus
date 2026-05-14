# Contributing to Zeus

Welcome — and thanks for thinking about contributing. Zeus is built by hams,
for hams, in our spare time. We move at a measured pace and we like the code
to be honest about what it does. If you're here to scratch a real itch on
your own rig, you're in the right place.

Before you write any code, please skim this whole page. It's short. Most of
what's here is the actual mental model the maintainers use day-to-day, not
ceremonial gates.

---

## What Zeus is, in one paragraph

Zeus is a cross-platform, web-frontend HPSDR client for original-protocol
(Protocol 1) and Protocol-2 radios — Hermes, Mercury/Penelope/Metis,
ANAN-class boards, Hermes-Lite 2, and the OrionMkII / Saturn family. The
backend is .NET 8 (`Zeus.Server*`), the frontend is Vite + React
(`zeus-web/`), and the DSP engine is WDSP, loaded via P/Invoke. **Thetis is
the sole authoritative reference for protocol and DSP behaviour** — when
Zeus and Thetis disagree, Thetis is right by default unless there's a
documented reason otherwise (see `docs/lessons/`).

---

## Before you start

Three things to know before you write a line of code:

1. **Open an issue first** for anything bigger than a typo. Drive-by PRs that
   add features without prior discussion get sent back through the issue
   tracker. The issue is where we agree on the *what* and the *shape*; the
   PR is where we agree on the *details*. Saves everyone time.

2. **Read [`CLAUDE.md`](CLAUDE.md)** at the repo root. It's nominally for AI
   coding agents, but it's the same standards every human contributor follows
   too. The red-light / green-light list there is the most important thing on
   this page.

3. **Don't touch the recently-shipped audio dropout fix or the PureSignal
   path** without explicit approval. See [§ Hot paths](#hot-paths) below for
   the specific files. Both took significant on-air investigation to land
   correctly and we don't want regression-roulette.

---

## Setting up

The repo's README has full details; the short version:

```bash
# Backend (listens on :6060)
dotnet run --project Zeus.Server

# Frontend (Vite dev server on :5173, proxies /api and /ws to :6060)
npm --prefix zeus-web run dev
```

Or use the bundled skill if you're working in Claude Code: `/run` brings up
both in one shot. `/run fresh` runs the backend against a throw-away
`zeus-prefs.db` so testing doesn't pollute your real settings.

The native WDSP library is rebuilt by CI on tagged releases; for local dev
the binaries in `Zeus.Dsp/runtimes/<rid>/native/` are good enough. See
`native/` if you want to build WDSP from source.

---

## The contribution flow

For external contributors (anyone without write access to this repo):

1. **Fork** `Kb2uka/openhpsdr-zeus` to your own GitHub account.
2. **Branch** from `develop` on your fork. Name it something descriptive:
   `your-handle/cw-apf-filter`, `your-handle/fix-zoom-overflow`, etc.
3. **Build clean** before opening the PR — `dotnet build Zeus.slnx` must be
   0 warnings, 0 errors. Tests should pass: `dotnet test Zeus.slnx`
   (the 16 `Zeus.PluginHost.Tests` failures on macOS are pre-existing and
   unrelated to any contribution — anything else failing is on your change).
4. **Open the PR against `develop`** on `Kb2uka/openhpsdr-zeus`. Never `main`.
5. **Wait for review.** KB2UKA reviews backend wiring and most code; @brianbruff
   reviews UI/UX/defaults. Reviews are usually within a day, sometimes same day.
6. **Don't merge your own PR.** Reviewer merges after approval.

You don't need collaborator status on this repo to contribute — the fork + PR
pattern is standard and gives you everything you need. GitHub auto-lists you
under Contributors once anything merges.

---

## The branch model

| Branch | What it is | When you push to it |
|---|---|---|
| `develop` | Integration branch. Everything new lands here via PR. Stays green and shippable. | Always, via PR |
| `main` | Release branch. Only updated by periodic `develop → main` merges by the maintainers. | Never, as a contributor |
| `vX.Y.Z` tags | What end-users actually run. Triggered by maintainers from `main`. | Never |

**Always target `develop` with your PR.** `main` is the release-merge gate;
contributors don't touch it directly.

---

## Red-light vs green-light

Adapted from `CLAUDE.md`. **Red-light** items need maintainer approval *before*
you implement them — not after. If you're not sure which side something falls
on, ask in the issue.

### Green-light (implement and PR with confidence)

- **Bug fixes with a clear root cause** — null refs, missing guards, off-by-one,
  persistence/wiring bugs where the fix is obvious from the symptom.
- **Build / CI fixes** — missing NuGet refs, csproj typos, dotnet version bumps,
  workflow YAML breakage, Vite / npm config fixes.
- **Protocol / WDSP compliance fixes** — where Zeus's behaviour diverges from
  Thetis and Thetis source confirms Zeus is wrong. *Exception:* if the fix
  changes a default an operator will feel (TX power cap, filter bandwidth,
  AGC curve, meter scaling), that becomes red-light.
- **Docs and lessons updates** — additions to `docs/lessons/`, `docs/rca/`,
  `CHANGELOG.md`, this file. README *additions* are fine; restructuring is
  red-light.
- **New backend wiring for an issue-approved feature** — WDSP P/Invoke stubs,
  `RadioService` methods, command handlers, hosted services.

### Red-light (open an issue, get sign-off first)

- **Visual design** — colours, fonts, layout, spacing, typography. The Zeus
  aesthetic is faithful to the Hermes-Lite 2 hardware front panel and is
  actively maintained by @brianbruff. Use existing CSS token variables in
  `zeus-web/src/styles/tokens.css`; never raw hex.
- **UX behaviour** — what a click/drag/scroll does, keyboard shortcuts,
  panadapter/waterfall axis direction, VFO tuning feel.
- **Architecture** — new threads, new dependencies, new NuGet/npm packages,
  changes to `Zeus.Contracts` (wire format), signal-routing restructures.
- **Default values** — anything an operator notices on first connect: TX power,
  filter widths, AGC defaults, meter calibration, default band/mode, palette.
  One person hitting a bug isn't proof the default is wrong for everyone.
- **Feature scope creep** — if the issue says "fix meter," fix the meter. Don't
  add a new meter or refactor the meter pipeline along the way.

If you're certain about the right answer for a red-light item, write your case
in the issue and let the maintainer decide. We're reasonable; the gate is
about coordinating, not about saying no.

---

## Hot paths

Two regions of the codebase have shipped fixes that took significant
investigation to land correctly. **Do not modify these files without explicit
approval in an issue** — even for what looks like unrelated cleanup:

- **Frontend audio scheduling**: `zeus-web/src/audio/audio-client.ts`,
  `zeus-web/src/audio/frame.ts`. Touches the Web Audio path; recent fix
  (PR #304) addressed OBS-streaming dropouts.

- **PureSignal pipeline**: `Zeus.Dsp/Wdsp/WdspDspEngine.cs` (PS-related
  methods), `Zeus.Server.Hosting/PsAutoAttenuateService.cs`,
  `Zeus.Server.Hosting/DspPipelineService.cs` (PS knob-apply paths and the
  MOX guard). Three regressions were fixed across PR #292, #293; we are not
  litigating any of them again without strong evidence and a documented plan.

- **Backend send queue**: `Zeus.Server.Hosting/StreamingHub.cs` — the
  per-client bounded channel + drop-counter probe. Don't change the queue
  semantics or the drop attribution without an issue.

If your feature genuinely needs to touch one of these, open an issue describing
why and we'll figure out the right path together.

There are also load-bearing invariants in `docs/lessons/` (HL2 WDSP init,
drive-byte quantisation, etc.) — read the relevant lesson before touching
any DSP, protocol, or layout code that those lessons cover.

---

## Code expectations

- **One feature per PR.** Smaller PRs land faster and are easier to revert if
  something turns out wrong. Don't bundle "fix the meter + refactor the panel
  + add a new mode" into one diff.
- **Match existing patterns.** Grep for a recent similar feature and follow
  its wiring shape. Don't introduce a new abstraction for something that fits
  the existing one.
- **Don't add features, refactoring, or abstractions beyond what the task
  requires.** A bug fix doesn't need surrounding cleanup; a one-shot
  operation doesn't need a helper.
- **Don't add error handling, fallbacks, or validation for scenarios that
  can't happen.** Trust internal code. Validate at system boundaries (user
  input, external APIs).
- **Default to no comments.** Code with well-named identifiers is
  self-explanatory. Add a comment only when the *why* is non-obvious — a
  hidden constraint, a subtle invariant, a workaround for a specific bug.
- **No `// removed code` comments, no backwards-compatibility shims** for
  things that aren't a problem yet. If something's unused, delete it.

---

## Commit messages

- **Conventional prefixes** preferred (`feat:`, `fix:`, `docs:`, `chore:`,
  `refactor:`, `test:`) but not strictly enforced — match the style of recent
  `git log` output.
- **Subject line under 72 chars.** Body wrapped at ~80.
- **Never mention Anthropic, Claude, or any AI assistant** in commit messages.
  This is a hard rule (see CLAUDE.md). Tell the *story* of the change, not the
  *tools* you used to make it.
- **Reference the issue** if there is one: `fix(...): description (#NNN)`.
- **No `--no-verify`, no skipping hooks.** If a hook fails, fix the underlying
  issue.

---

## Tests

- Build clean: `dotnet build Zeus.slnx` → 0 warnings, 0 errors.
- Run tests: `dotnet test Zeus.slnx`. The 16 `Zeus.PluginHost.Tests` failures
  on macOS are pre-existing and unrelated to contributions (missing VST3
  sidecar binary). Anything else failing is on your change — fix it before
  PR.
- **Add tests for new behaviour** where it's reasonable to do so. The bar
  isn't every method; the bar is "if this regresses, would someone notice?"
  Persistence stores, protocol parsers, calibration math — yes. UI shape — no
  (covered by on-air operator testing).

---

## Reviews and merging

- **KB2UKA** (Doug, repo owner) reviews most code, backend wiring, build /
  CI, docs, and refactors.
- **@brianbruff** (Brian, project founder) reviews UI/UX, visual design,
  operator-felt defaults, architecture-level decisions.

Both reviewers may request changes or ask for clarifications. We try to be
helpful in review; if a piece of feedback isn't clear, ask.

Squash-vs-merge: the repo uses **merge commits** for PRs (preserves
contribution history clearly). Don't worry about squashing your own commits
unless asked.

---

## Releases and issue hygiene

- Releases happen periodically when develop has accumulated enough stuff
  worth shipping. They go: bump version → PR `develop → main` → merge → tag
  `vX.Y.Z` on main → push tag (CI builds release artifacts and creates the
  GitHub Release page).
- **Issues stay open after the fix-PR merges to `develop`.** They close when
  the release-merge ships the fix in a tagged version. This way the issue
  tracker reflects what's actually shipping to end users, not what's queued.

So if your PR fixes #N, your PR will land but #N will stay open until the
next release. We'll close it in a sweep then.

---

## On-air testing

Some bugs only show up on real hardware. If your contribution is in the
TX/RX/DSP/PA path, it's *strongly* preferred that you (or a maintainer) can
verify the change on a real radio before merge. Mentioning your test rig in
the PR description — "verified on ANAN-G2 + RF2K-S amp" or "couldn't bench
test, please verify on HL2 before merge" — saves a review round trip.

Pure backend wiring, build fixes, docs, refactors etc. don't need this.

---

## Where to get help

- **Filed an issue but stuck**: re-comment on the issue with what's blocked.
- **Mid-PR and stuck**: comment on the PR with what's going wrong. Tag the
  reviewer if a few days have passed silently.
- **General architectural question before writing code**: open a `question`
  issue or comment on the closest existing one. We'd rather you ask than
  spend a weekend on the wrong approach.

You can also `@kb2uka-agent` in any issue comment or PR review for a
personal-coding-bot opinion (it's a real automated agent run, not a search
shortcut — small CPU cost for non-trivial questions).

---

## Code of conduct

Be kind, be specific, assume good faith. Most contributors here are doing
this in their off hours after a day job; nobody owes anybody an instant
response. We expect technical disagreement; we don't expect personal sharpness.

Brian and Doug reserve the right to ask for changes or close PRs that don't
fit the project's direction; this isn't personal, it's stewardship.

---

## License

By contributing you agree your contributions are licensed under **GNU GPL
v2 or later**, the same as the rest of Zeus. See [LICENSE](LICENSE) for the
full text.

---

73 and welcome aboard.

— Doug (KB2UKA) and Brian (EI6LF)
