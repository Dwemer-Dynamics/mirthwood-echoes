# Mirthwood: Echoes development command centre

Use one coordinator for a batch and one owner per independent player-facing outcome. The module, server, journal, companion and installer live in [one repository](https://github.com/Dwemer-Dynamics/mirthwood-echoes). A change spanning them can travel in one PR.

| Place | What it contains |
| --- | --- |
| Development | The source checkout and temporary feature worktrees on the development PC. Building writes a server package under the worktree's `.work`. |
| Playable test installation | The Windows companion, installed Echoes WSL server and the server-managed module in Steam's Mirthwood. A source edit, build or Git merge leaves this installation at its previous revision. |
| GitHub | `unstable` is the shared development branch. Promote by PR from `unstable` to `dev`, then from `dev` to `mirthwood-echoes`. A branch is a source revision. Creating a release or updating the installed game is a separate operation. |

## Start or resume

Read the [project rules](../../AGENTS.md) and [build and harness guide](../../README.md). Verify the checkout's physical path, origin, branch and HEAD, working changes, linked worktrees and open Echoes PRs. Check active Echoes chats for overlapping paths or a shared game, companion, database fixture or validation run. Resume the assigned owner when appropriate. An idle chat can still own a handoff.

Keep the live [programme, ledger and checklist](templates) together **outside** the checkout. On Windows, the default is the current user's Documents folder under `Codex/Mirthwood-Echoes-Command-Centre`. Keep source and portable templates here and local task IDs, decisions and receipts in that folder. Record the user's current scope, authority, owners, dependencies, last evidence and next deliverable. Reuse existing records across restarts. Give each worker its own brief and exact released paths. Check new handoffs when reported.

For a fresh checkout, use Windows PowerShell 7, Python 3.11 or later, Node, Git, GitHub CLI and the .NET 10 SDK. Discover the licensed Mirthwood Steam installation and obtain the loader archive specified by the build script. The WSL fixtures use a prepared Echoes account, .NET 10 and PostgreSQL. Set the validator's `--game-dir`, `--loader-zip` and `--distro` when their defaults differ. Codex discovers the nine skills in this project's `.agents/skills` folder.

| Operation | Project skill |
| --- | --- |
| Lead the batch and verify completion | [Command centre](../../.agents/skills/echoes-command-centre/SKILL.md) |
| Divide a feature into independently owned work and coordinate shared resources | [Feature coordination](../../.agents/skills/echoes-feature-command-centre/SKILL.md) |
| Trace a request and define acceptance | [Planning](../../.agents/skills/echoes-plan/SKILL.md) |
| Implement and open a draft PR | [Build and PR](../../.agents/skills/echoes-build-pr/SKILL.md) |
| Select and run checks for changed paths | [Validation](../../.agents/skills/echoes-validate/SKILL.md) |
| Review staged contents before publication | [Commit scope](../../.agents/skills/echoes-commit-scope/SKILL.md) |
| Review the exact head or brief a bounded UI change | [Claude review and UI](../../.agents/skills/echoes-claude-review-ui/SKILL.md) |
| Merge a reviewed PR under recorded authority | [Review and merge](../../.agents/skills/echoes-review-merge/SKILL.md) |
| Install and verify the server package | [Local deployment](../../.agents/skills/echoes-local-deploy/SKILL.md) |

## Git and validation

Fetch the shared branch before assigning a job. A worktree is another working folder for the same Git history. Its branch holds one job's commits. Run these commands from the clean development checkout with the job's own path and name.

```powershell
git fetch --prune origin
git worktree add -b codex/example ../echoes-wt-example origin/unstable
```

In the new worktree, plan before editing. Stage named files, review the full index and commit the agreed change. Run the final gate against the same fetched base. The gate records the merge base, exact head, changed paths, checks, duration and any package hashes under `.work/validation`. Adding `--foundation` exercises the disposable PostgreSQL and model fixtures with Mirthwood closed. See the [validation skill](../../.agents/skills/echoes-validate/SKILL.md) for native and live evidence.

```powershell
py -3 scripts/validate-change.py plan --paths server/ModuleInstaller.cs
py -3 scripts/validate-change.py status
py -3 scripts/validate-change.py run --base origin/unstable
```

A push uploads a branch. A draft PR presents its commits for review. Record an independent Claude verdict on the exact head. A merge puts that reviewed change on the target branch after user authorisation. The coordinator verifies the remote head, merge commit, measured outcome and open acceptance items. Fast-forward a clean shared checkout, remove the finished worktree with Git, and retire only verified merged branches. Archive finished chats under the applicable storage policy when authorised.

## How the module reaches the player

`scripts/Build-Server.ps1` compiles the BepInEx module, publishes the server and launcher, and writes a versioned manifest and file hashes into `app/bundle` inside `Mirthwood-Echoes-Server`. For an authorised installation, pass the **same verified package** to its `Update-Server.ps1`. The installed server checks the manifest and installs `BepInEx/plugins/MirthwoodEchoes/Mirthwood.Echoes.dll` in the discovered Steam game directory. Module changes ship this way too. Compare the compiled, bundled and installed module hashes and distinguish a pending game exit or foreign-file conflict from a completed install.

The prepared WSL distro can host other products. Operate only the Echoes unit, console and database. Honour the companion's start, minimize and close behavior. For approved gameplay checks, use the copied-save harness, verify the original save and backup afterward, and distinguish native observation, fixture output and live-provider results. Keep a release candidate's package receipt until its deployment or handoff is complete.

For a requested unstable tester distribution, deliver one `Mirthwood-Echoes-Server-<version>.zip` containing the qualified server, bundled module and loader, companion, updater, player instructions and the package's required assets. Verify the ZIP's contents against the package receipt. Record the source SHA, package SHA-256 and verified distribution link in the ledger. When the batch includes the user's Google Drive, use the Echoes destination and keep its sharing settings. Exclude worktrees, saves, provider configuration, databases and diagnostics.
