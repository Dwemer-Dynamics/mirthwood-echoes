# Mirthwood: Echoes development command centre

For substantial Echoes work, the coordinator assigns one separate user-visible build chat per independent outcome by default. Give one owner the whole change across the module, server, journal, companion and installer when they share a contract. Those components live in [one repository](https://github.com/Dwemer-Dynamics/mirthwood-echoes). A small status or management action can stay with the coordinator.

| Place | What it contains |
| --- | --- |
| Development | The source checkout and temporary feature worktrees on the development PC. Building writes a server package under the worktree's `.work`. |
| Playable test installation | The Windows companion, installed Echoes WSL server and the server-managed module in Steam's Mirthwood. A source edit, build or Git merge leaves this installation at its previous revision. |
| GitHub | `unstable` is the shared development branch. Promote by PR from `unstable` to `dev`, then from `dev` to `mirthwood-echoes`. A branch is a source revision. Creating a release or updating the installed game is a separate operation. |

## Start or resume

Use the [coordinator prompt](templates/COORDINATOR.md) to start a new Echoes command-centre chat or resume a batch from its saved records.

Read the [project rules](../../AGENTS.md) and [build and harness guide](../../README.md). Verify the checkout's physical path, origin, branch and HEAD, working changes, linked worktrees and open Echoes PRs. Check active Echoes chats for overlapping paths or a shared game, companion, database fixture or validation run. Resume the assigned owner when appropriate. An idle chat can still own a handoff.

Keep the live [programme, ledger and checklist](templates) together **outside** the checkout. On Windows, the default is the current user's Documents folder under `Codex/Mirthwood-Echoes-Command-Centre`. The coordinator maintains the user's decisions and assignments in the programme, PR and evidence links in the ledger, and observable done conditions in the checklist. Reuse these Echoes records across restarts.

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

## Assign and follow a build

Select the Echoes project and give the owner the [worker brief](templates/WORKER.md), including the exact base, paths, dependencies, authority and acceptance. Set `model: gpt-6.2-astra` and `thinking: max` in the Codex call for each new visible Echoes coding or coordination worker and each follow-up assignment. Continue in the same chat when its ownership carries forward. Record its ID and verify the requested settings against the dispatch and the executing turn when available. Changing a saved setting during a turn takes effect on a subsequent turn. If the active turn differs, preserve the work and resume the owner on the selected settings. Independent Claude review and the player's conversation model use their own settings.

The coordinator follows the assigned chats with bounded waits and saved status cursors. Check the owner's evidence when an outcome, dependency or blocker changes. Before handing off shared paths, identify the commit, released paths, acceptance already verified and any continuing ownership. Allocate the validation lane, copied-save harness and playable updater separately as needed. Recheck each resource just before use and record its restoration or any exception before passing it on.

The build owner hands back the draft PR, exact head, changed-path receipt, independent review verdict and remaining checks. The coordinator checks the evidence and controls integration. Use the maintainer's existing permissions for an authorised PR or batch. A blocked action goes back with its reason and evidence.

## Git and validation

Fetch the shared branch before assigning a job. A worktree is another working folder for the same Git history. Its branch holds one job's commits. In Codex desktop, create the managed worktree from the fetched Echoes `origin/unstable`, use the returned folder, and create the job's `codex/<task>` branch there if it starts detached. Verify the physical path, branch and base. For a command-line checkout, use the job's own path and name.

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

A push uploads a branch. A draft PR presents its commits for review. Record an independent Claude verdict on the exact head. A merge puts that reviewed change on the target branch after user authorisation. The coordinator verifies the remote head, merge commit, measured outcome and open acceptance items.

Retain completed receipts and required packages under `E:\CodexStorage\Projects\MirthwoodEchoes`. If deployment, tester distribution or a handoff needs the candidate, retain the complete run directory, including the server package. Verify the destination against the receipt, record both locations and the receipt's SHA-256 in the ledger, and keep the last verified copy. Fast-forward a clean shared checkout. Before removing a released worktree with Git, check that its active chats and harness consumers have usable working directories. Retire its verified merged branch. When archiving a finished chat, follow the current storage policy for full available history, exact native originals and project-scoped lessons on E:.

## How the module reaches the player

`scripts/Build-Server.ps1` compiles the BepInEx module, publishes the server and launcher, and writes a versioned manifest and file hashes into `app/bundle` inside `Mirthwood-Echoes-Server`. Use a fresh worktree for an installation or distribution candidate to exclude stale build outputs. The validator prints the path to `receipt.json`. The validated package is the sibling `Mirthwood-Echoes-Server` directory. For an authorised installation, select that run (or its verified retained copy) and pass the package's absolute path to its own updater.

```powershell
$run = Get-Item -LiteralPath ".work/validation/<run-id>"
$receipt = Join-Path $run.FullName 'receipt.json'
$package = Join-Path $run.FullName 'Mirthwood-Echoes-Server'
py -3 scripts/validate-change.py verify-package $receipt $package
if ($LASTEXITCODE) { throw 'The selected package failed verification.' }
& (Join-Path $package 'Update-Server.ps1') -Package $package
```

The installed server checks the manifest and installs `BepInEx/plugins/MirthwoodEchoes/Mirthwood.Echoes.dll` in the discovered Steam game directory. Module changes ship this way too. Compare the compiled, bundled and installed module hashes and distinguish a pending game exit or foreign-file conflict from a completed install.

The prepared WSL distro can host other products. Operate only the Echoes unit, console and database. Honour the companion's start, minimize and close behavior. For approved gameplay checks, use the copied-save harness, verify the original save and backup afterward, and distinguish native observation, fixture output and live-provider results. Keep a release candidate's package receipt until its deployment or handoff is complete.

For a requested unstable tester distribution, retain one `Mirthwood-Echoes-Server-<version>.zip` in the Echoes project folder on E:. Include the qualified server, bundled module and loader, companion, updater, player instructions and the package's required assets. Extract the ZIP to an empty directory and run `verify-package` on the extracted package with the retained receipt. Record the source SHA, inventory, ZIP SHA-256 and verified local path in the ledger. External distribution requires separate user authority for the destination. Exclude worktrees, saves, provider configuration, databases and diagnostics.
