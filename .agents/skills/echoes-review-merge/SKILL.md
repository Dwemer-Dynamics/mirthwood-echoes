---
name: echoes-review-merge
description: "Review and, with explicit authority, merge an exact Mirthwood: Echoes PR head and verify the resulting GitHub state."
---

# Merge a reviewed Echoes PR

Read the Echoes PR's base and head, entire diff, comments, review threads, checks and dependencies. Verify the validator receipt matches the head and current merge base and the independent Claude review covers this exact head. Resolve actionable findings or record a demonstrated pre-existing failure under the batch decision. For promotion verify the source belongs to this repository and follows unstable → dev → mirthwood-echoes.

With explicit authority for this PR or batch, verify the repository, current target tip and reviewed head. If the target advanced beyond the receipt's merge base, inspect the proposed merge against both parents before merging. Return conflicts to the source owner and refresh evidence for any affected inputs. A new PR head needs fresh validation and review. Record the expected parents and tree. Inspect the merge message and mark the draft ready. Use `gh pr merge <number> --repo Dwemer-Dynamics/mirthwood-echoes --merge --match-head-commit <reviewed-sha>`. A changed head or blocked check returns to the owner. Preserve branch protections and commit history.

Verify MERGED, actual head, merge commit, parents and tree on GitHub. Reconcile any drift from the inspected merge before accepting it for deployment or promotion. Record the result in the ledger and fast-forward the clean shared checkout. Verify retained evidence and confirm active chats and test consumers have usable paths before removing a clean released worktree with `git -c core.longpaths=true worktree remove <path>`. Retire a merged task branch after checking its tip. Apply any separately recorded installation and release authority.
