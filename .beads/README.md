# Beads - AI-Native Issue Tracking

Welcome to Beads! This repository uses **Beads** for issue tracking - a modern, AI-native tool designed to live directly in your codebase alongside your code.

## What is Beads?

Beads is issue tracking that lives in your repo, making it perfect for AI coding agents and developers who want their issues close to their code. No web UI required - everything works through the CLI and integrates seamlessly with git.

**Learn more:** [github.com/steveyegge/beads](https://github.com/steveyegge/beads)

## Quick Start

### Essential Commands

```bash
# Create new issues
bd create "Add user authentication"

# View all issues
bd list

# View issue details
bd show <issue-id>

# Update issue status
bd update <issue-id> --claim
bd update <issue-id> --status done

# Sync with Dolt remote
bd dolt push
```

### Working with Issues

Issues in Beads are:
- **Git-native**: Stored in Dolt database with version control and branching
- **AI-friendly**: CLI-first design works perfectly with AI coding agents
- **Branch-aware**: Issues can follow your branch workflow
- **Always in sync**: Auto-syncs with your commits

## Why Beads?

✨ **AI-Native Design**
- Built specifically for AI-assisted development workflows
- CLI-first interface works seamlessly with AI coding agents
- No context switching to web UIs

🚀 **Developer Focused**
- Issues live in your repo, right next to your code
- Works offline, syncs when you push
- Fast, lightweight, and stays out of your way

🔧 **Git Integration**
- Automatic sync with git commits
- Branch-aware issue tracking
- Dolt-native three-way merge resolution

## Get Started with Beads

Try Beads in your own projects:

```bash
# Install Beads
curl -sSL https://raw.githubusercontent.com/steveyegge/beads/main/scripts/install.sh | bash

# Initialize in your repo
bd init

# Create your first issue
bd create "Try out Beads"
```

## Learn More

- **Documentation**: [github.com/steveyegge/beads/docs](https://github.com/steveyegge/beads/tree/main/docs)
- **Quick Start Guide**: Run `bd quickstart`
- **Examples**: [github.com/steveyegge/beads/examples](https://github.com/steveyegge/beads/tree/main/examples)

---

## Zeus team sync (project-specific)

> Moved here from `CLAUDE.md` to keep the agent context file lean. This is the
> authoritative Zeus-specific bd setup; `CLAUDE.md` carries only a pointer.

Zeus does **not** use the `refs/dolt/data`-on-git-remote sync path that bd's
default integration block mentions. Zeus syncs its bd Dolt database to a
dedicated public DoltHub repo:

> **DoltHub:** https://www.dolthub.com/repositories/kb2uka/openhpsdr-zeus

### One-time teammate setup

```bash
dolt login                                                                   # browser flow → associate a key
bd dolt remote add origin https://doltremoteapi.dolthub.com/kb2uka/openhpsdr-zeus
bd dolt pull origin main                                                     # fetch the team's issues
```

### Day-to-day

```bash
bd dolt pull origin main      # before starting work
# ... bd create / bd update / bd close ...
bd dolt push origin main      # after edits, before signing off
```

### What's tracked in git vs. DoltHub

- **`.beads/config.yaml`** (in git) — team-wide bd config including `sync.remote`. Edit here for team-wide defaults.
- **`.beads/issues.jsonl`** / **`interactions.jsonl`** (in git) — passive exports for human grep and zero-dependency reading. **Do not edit by hand** — bd regenerates them. **Do not commit them inside a feature-branch PR** — the merge-conflict tax is real once more than one person uses bd. Snapshot commits to `develop` are fine.
- **`.beads/metadata.json`** (gitignored) — per-clone `project_id` + local `dolt_database` name. Local state, never committed.
- **`.beads/embeddeddolt/`** (gitignored) — the actual Dolt DB. Never committed; this is what `bd dolt push` ships.

---

*Beads: Issue tracking that moves at the speed of thought* ⚡
