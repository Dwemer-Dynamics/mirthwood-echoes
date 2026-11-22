---
name: echoes-build-pr
description: "Implement an authorised Mirthwood: Echoes change in an isolated worktree and prepare its draft PR."
---

# Build an Echoes change

Read AGENTS.md, the current programme and the task's path ownership. Fetch origin, verify the repository and check for open PR overlap. Use the chat's assigned clean worktree and verify its `codex/` branch starts from fetched `origin/unstable`. For new worktrees use Codex's managed worktree action where available and verify the resulting physical path, branch and base. Preserve the canonical checkout and other worktrees. Keep the module and its server consumer together when one changes the other's contract.

Plan exact paths, implement the agreed scope and run targeted development checks. Use `echoes-commit-scope` before committing named files. Run `echoes-validate` on the resulting commit and merge base. Publishing authority covers pushing that feature branch and opening its draft against unstable. Include the problem, player-visible effect, head, validation run, fixture/native coverage and outstanding checks.

Use `echoes-claude-review-ui` on the exact head. Fix verified issues and refresh changed evidence. Return the PR link and target, base, reviewed SHA, validation run ID and receipt, actual reviewer and verdict, coverage limits and any released paths or test resources. The draft is the normal build-owner handoff. The coordinator controls integration and applies the batch's merge, promotion and installation authority.
