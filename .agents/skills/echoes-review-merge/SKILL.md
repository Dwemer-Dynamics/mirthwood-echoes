---
name: echoes-review-merge
description: "Review and, with explicit authority, merge an exact Mirthwood: Echoes PR head and verify the resulting GitHub state."
---

# Merge a reviewed Echoes PR

Read the Echoes PR's base and head, entire diff, comments, review threads, checks and dependencies. Verify the validator receipt matches the head and current merge base and the independent Claude review covers this exact head. Resolve actionable findings or record a demonstrated pre-existing failure under the batch decision. For promotion verify the source belongs to this repository and follows unstable → dev → mirthwood-echoes.

With explicit authority for this PR or batch, verify repository and target, recheck head, inspect the merge message and mark the draft ready. Use `gh pr merge <number> --repo Dwemer-Dynamics/mirthwood-echoes --merge --match-head-commit <reviewed-sha>`. A changed head or blocked check returns to the owner. Preserve branch protections and commit history.

Verify MERGED, actual head, merge commit and its tested tree on GitHub. If the target advanced, check the integrated tree and refresh affected evidence before claiming equivalence. Record the result in the ledger and fast-forward the clean shared checkout. Verify retained evidence and confirm active chats and test consumers have usable paths before removing a clean released worktree with `git -c core.longpaths=true worktree remove <path>`. Retire a merged task branch after checking its tip. Apply any separately recorded installation and release authority.
