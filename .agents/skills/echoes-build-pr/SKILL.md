---
name: echoes-build-pr
description: "Implement an authorised Mirthwood: Echoes change in an isolated worktree and prepare its draft PR."
---

# Build an Echoes change

Read AGENTS.md, the current programme and the task's path ownership. Fetch origin, verify the repository and check for open PR overlap. Use the isolated worktree supplied at dispatch. If one is needed, the build owner creates it with Codex's managed worktree action where available. Verify the physical path, `codex/` branch and assigned base from fetched `origin/unstable`. Preserve the canonical checkout and other worktrees. Keep the module and its server consumer together when one changes the other's contract.

Plan exact paths, implement the agreed scope and run targeted development checks. Use `echoes-commit-scope` before committing named files. Run `echoes-validate` on the resulting commit and merge base. Publishing authority covers pushing that feature branch and opening its draft against unstable. Include the problem, player-visible effect, head, validation run, fixture/native coverage and outstanding checks.

Use `echoes-claude-review-ui` on the exact head. Fix verified issues and refresh changed evidence. Return the draft and the evidence listed under Delivery in the [worker brief](../../../docs/command-centre/templates/WORKER.md). The coordinator controls integration and applies the batch's merge, promotion and installation authority.
