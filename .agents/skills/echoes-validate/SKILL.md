---
name: echoes-validate
description: "Plan and run Echoes changed-path checks, verify the server's bundled module and select relevant harness coverage."
---

# Validate an Echoes change

From the Echoes worktree use [the repository validator](../../../scripts/validate-change.py). `plan --paths <paths...>` previews an edit. `plan --base origin/unstable` inspects the committed PR. `status` checks the shared Windows validation lane. `run --base origin/unstable` validates a clean committed tree and writes a head-bound JSON receipt. The default gate classifies exact paths, checks affected source inputs and builds and verifies the server package for product changes. Policy-only changes have a no-build path. Resolve unclassified paths before claiming a pass.

Use the repository's existing [harness procedures](../../../README.md) for behavioral evidence. Where touched, run the disposable installer/provider/journal checks using `run --base origin/unstable --foundation --distro <prepared-distro>` with the game closed. It uses a synthetic Steam directory, disposable PostgreSQL data and a local provider. Reserve the shared lane. Select native Runtime, Interface, World, SocialBalance or the appropriate relationship/quest/voice suite for separately authorised game checks. Use a copy of the chosen slot and compare the original save and backup. The provider probe and production Talk suites use actual accounts when configured.

Report source head, merge base, exact paths, run ID, duration, assertions and remaining acceptance. Reuse a passing run for the same reviewed source, base and coverage. If the head or relevant inputs change, refresh the proof. Reproduce a suspected pre-existing failure on the pinned base in an isolated worktree before classifying it.
