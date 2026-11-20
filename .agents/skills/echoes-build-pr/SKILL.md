---
name: echoes-build-pr
description: "Implement an authorised Mirthwood: Echoes change in an isolated worktree and prepare its draft PR."
---

# Build an Echoes change

Read AGENTS.md, the current programme and the task's path ownership. Fetch origin, verify the repository and assign a clean worktree on a `codex/` branch from origin/unstable. Preserve the canonical checkout and other worktrees. Keep the module and its server consumer together when one changes the other's contract.

Plan exact paths, implement the agreed scope and run targeted development checks. Use `echoes-commit-scope` before committing named files. Run `echoes-validate` on the resulting commit and merge base. Publishing authority covers pushing that feature branch and opening its draft against unstable. Include the problem, player-visible effect, head, validation run, fixture/native coverage and outstanding checks.

Get `echoes-claude-review-ui` on the exact head. Fix verified issues, refresh changed evidence and return the PR link, reviewed SHA and receipt to the coordinator. Installation uses the server release through `echoes-local-deploy` when requested.
