---
name: echoes-commit-scope
description: "Inspect the exact staged changes and commit or merge message for publication to the Echoes repository."
---

# Check an Echoes commit

Verify the Echoes origin, target, ownership and intended message. Inspect `git diff --cached --name-status` and the **entire** `git diff --cached --unified=0` in bounded file groups. For binaries check provenance, content type, size and digest. The two named archives in voices/ are intentional build inputs. Preserve their notices and inspect member names when they change. Git outputs and release packages belong outside source.

Inspect for credentials, private account paths, unrelated data, player saves, databases, raw logs, compiled outputs and third-party game or loader files. Retain necessary upstream attribution. Stage explicit files and commit only the reviewed index with the exact reviewed message. Recheck after any restage or message change, including squash and merge messages. Use the same check for a worker's publication.
