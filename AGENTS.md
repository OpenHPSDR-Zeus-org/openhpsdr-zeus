# Agent Instructions

This project uses **bd** (beads) for issue tracking. Run `bd prime` for full workflow context.

> **Architecture in one line:** Issues live in a local Dolt database
> (`.beads/dolt/`); cross-machine sync uses `bd dolt push/pull` (a
> git-compatible protocol), stored under `refs/dolt/data` on your git
> remote — separate from `refs/heads/*` where your code lives.
> `.beads/issues.jsonl` is a passive export, not the wire protocol.
>
> See [SYNC_CONCEPTS.md](https://github.com/gastownhall/beads/blob/main/docs/SYNC_CONCEPTS.md)
> for the one-screen overview and anti-patterns (don't treat JSONL as the
> source of truth; don't `bd import` during normal operation; don't
> reach for third-party Dolt hosting before trying the default).

## Quick Reference

```bash
bd ready              # Find available work
bd show <id>          # View issue details
bd update <id> --claim  # Claim work atomically
bd close <id>         # Complete work
bd dolt push          # Push beads data to remote
```

## Automatic Development Workflow

- Fast local-trunk mode is available when you want the speed of committing
  locally and testing the combined latest code immediately:
  `pwsh scripts/start-local-dev.ps1` creates or updates `local/develop` from
  `OpenHPSDR-Zeus-org/develop`. Commit logical changes there, then export a
  clean PR branch with
  `pwsh scripts/export-pr.ps1 -Commit HEAD -Name "<short name>" -Issue <id>`.
  Use `-Range "<base>..HEAD"` to export multiple local commits as one PR.
- For each new feature, bug fix, or similarly scoped development task,
  automatically work in a dedicated worktree from `develop` unless you are
  already in a task-specific worktree/branch created for that task.
- Start new work with `pwsh scripts/start-work.ps1 feature "<short name>"` or
  `pwsh scripts/start-work.ps1 fix "<short name>"`. Pass `-Issue <id>` when a
  beads issue exists or was just created.
- For parallel agent lanes, use `pwsh scripts/take-work.ps1 -Count <n>` to
  atomically claim ready beads work and create one worktree per claimed issue.
  Narrow the queue with filters such as `-Type bug`, `-Priority 1`, or
  `-Unassigned` when needed.
- Use branch names such as `feature/<short-descriptive-name>` or
  `fix/<short-descriptive-name>`.
- Worktrees live beside the primary checkout under
  `openhpsdr-zeus.Worktrees/<branch_with_underscores>/`.
- Finish with `pwsh scripts/finish-work.ps1 -Message "<commit subject>"`. The
  script commits, rebases onto `OpenHPSDR-Zeus-org/develop`, runs the standard
  test gates, pushes the branch, pushes beads data, and opens a PR into
  `develop` when `gh` is available. It leaves passive beads JSONL exports out
  of feature-branch commits.
- Add `-TakeNext` to `finish-work.ps1` when a session should push its PR and
  immediately allocate the next ready issue into a fresh worktree.
- Finish agent lanes with `pwsh scripts/finish-work.ps1 -Message "<subject>"
  -Issue <id> -Autopilot`. Autopilot requires a PR, merges the finished branch
  into `local/develop` for immediate combined local testing, removes the
  completed worktree, and allocates the next ready issue.
- The workflow scripts configure `core.hooksPath=.githooks`; the hooks block
  direct commits on `main` and `develop`.
- After the PR merges, run `pwsh scripts/cleanup-merged-worktrees.ps1` to
  fast-forward the primary checkout and remove clean merged worktrees.
- Never copy files manually from a worktree into the primary checkout. Git
  branch + PR + pull is the consolidation path.

## Non-Interactive Shell Commands

**ALWAYS use non-interactive flags** with file operations to avoid hanging on confirmation prompts.

Shell commands like `cp`, `mv`, and `rm` may be aliased to include `-i` (interactive) mode on some systems, causing the agent to hang indefinitely waiting for y/n input.

**Use these forms instead:**
```bash
# Force overwrite without prompting
cp -f source dest           # NOT: cp source dest
mv -f source dest           # NOT: mv source dest
rm -f file                  # NOT: rm file

# For recursive operations
rm -rf directory            # NOT: rm -r directory
cp -rf source dest          # NOT: cp -r source dest
```

**Other commands that may prompt:**
- `scp` - use `-o BatchMode=yes` for non-interactive
- `ssh` - use `-o BatchMode=yes` to fail instead of prompting
- `apt-get` - use `-y` flag
- `brew` - use `HOMEBREW_NO_AUTO_UPDATE=1` env var

<!-- BEGIN BEADS INTEGRATION v:1 profile:minimal hash:ccf33ec3 -->
## Beads Issue Tracker

This project uses **bd (beads)** for issue tracking. Run `bd prime` to see full workflow context and commands.

### Quick Reference

```bash
bd ready              # Find available work
bd show <id>          # View issue details
bd update <id> --claim  # Claim work
bd close <id>         # Complete work
```

### Rules

- Use `bd` for ALL task tracking — do NOT use TodoWrite, TaskCreate, or markdown TODO lists
- Run `bd prime` for detailed command reference and session close protocol
- Use `bd remember` for persistent knowledge — do NOT use MEMORY.md files

**Architecture in one line:** issues live in a local Dolt DB; sync uses `refs/dolt/data` on your git remote; `.beads/issues.jsonl` is a passive export. See https://github.com/gastownhall/beads/blob/main/docs/SYNC_CONCEPTS.md for details and anti-patterns.

## Session Completion

**When ending a work session**, you MUST complete ALL steps below. Work is NOT complete until `git push` succeeds.

**MANDATORY WORKFLOW:**

1. **File issues for remaining work** - Create issues for anything that needs follow-up
2. **Run quality gates** (if code changed) - Tests, linters, builds
3. **Update issue status** - Close finished work, update in-progress items
4. **PUSH TO REMOTE** - This is MANDATORY:
   ```bash
   git pull --rebase
   bd dolt push
   git push
   git status  # MUST show "up to date with origin"
   ```
5. **Clean up** - Clear stashes, prune remote branches
6. **Verify** - All changes committed AND pushed
7. **Hand off** - Provide context for next session

**CRITICAL RULES:**
- Work is NOT complete until `git push` succeeds
- NEVER stop before pushing - that leaves work stranded locally
- NEVER say "ready to push when you are" - YOU must push
- If push fails, resolve and retry until it succeeds
<!-- END BEADS INTEGRATION -->
