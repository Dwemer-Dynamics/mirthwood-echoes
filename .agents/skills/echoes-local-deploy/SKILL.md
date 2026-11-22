---
name: echoes-local-deploy
description: "Install an authorised, validated Mirthwood: Echoes server package and verify its server-managed Steam module."
---

# Deploy the Echoes server package

Use the exact candidate from the successful changed-path run. Record the worktree head, package inventory, manifest version and compiled module SHA-256. Recheck the entire package with `py -3 scripts/validate-change.py verify-package <receipt> <package-directory>`. A no-build receipt cannot qualify a deployment. Preserve the current verified rollback within the project's two-generation policy.

When the programme permits verifying an already-installed build after a policy-only merge, compare the complete source trees with `git diff --name-status <qualified-package-head> <merge-commit> --`. Check every changed path against the [package build](../../../scripts/Build-Server.ps1) and its build and publish inputs, including the root `README.md` copied into the package. Reuse the existing installation only when the package inputs match. Verify the retained package against its original receipt and the installed files against that package. Record both source SHAs, receipt and installed hashes in the ledger. A changed or unverified package input requires qualification of a package for the merged source before installation.

Confirm the current authority, shared-resource owner, selected Steam library, prepared WSL distro and visible companion state. The coordinator allocates the playable updater to one owner. Use the validated package's own `Update-Server.ps1 -Package <directory> -Distro <distro>`. It discovers Steam or accepts the selected `-GameDir`. The updater installs the server and companion. The **Echoes server** reconciles its bundled module into Steam using the manifest and its ownership ledger. Never copy the compiled plugin directly into the game. The installer's `waiting_for_game_exit` state defers replacement, and `conflict` preserves an independently changed file. Report these until reconciliation succeeds.

Compare the installed server release and Steam plugin hash with the tested package. When the visible companion owns a running session, check the Echoes health response and module status. An intentionally stopped backend is a valid lifecycle state. Preserve configuration, saves, database, credentials and other mods. Record actual installation separately from Steam launch, loaded-module heartbeat and copied-save gameplay acceptance. Leave game launch to the user's instruction.
