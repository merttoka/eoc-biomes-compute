---
status: accepted
date: 2026-11-04
tags: [adr, data, repo, firing]
related: [[../ARCHITECTURE]], [[../sessions/2026-11-04-mit-brain-scene]], [[0018-presentation-clock-frame-locked-takes]]
---
# ADR-0019: Datasets without confirmed public release stay out of git

## Context
This repo is public. The 11.5 scene runs on a third-party organoid recording. Public release of that
data had not been confirmed when the branch was ready to merge, and a public push is effectively
irreversible.

Options:
- Push everything. Simple, but publishes the data without confirmation.
- Hold the whole merge until release is confirmed. Blocks engine work that has nothing to do with the data.
- Merge the code and keep the data local, as 11.2's media already is.

## Decision
A dataset whose public release isn't confirmed stays out of git history:
- **Data:** the firing blob, its sidecar and the positions CSV are gitignored. All three derive from
  the recording, including the synthetic layout and backbone labels.
- **Text:** measured results and researcher names stay out of code, comments, docs and commit
  messages. Describe behaviour, and keep the numbers with the local sidecar.
- **History:** this must hold for every pushed commit, not only the tip. Git LFS uploads any object a
  pushed commit references, so removing a file in a later commit doesn't help.

The branch was rewritten before its first push. A local backup branch,
`local/mit-brain-ketamine-with-data-DO-NOT-PUSH`, keeps the original commits.

## Consequences
- 11.5 doesn't play from a fresh clone. `NeuronFiringSource` logs "blob not found" and the positions
  reference is empty. Restore the files with their `.meta`, because the scene references the CSV by
  GUID, or regenerate them with `tools/spikes_npy_to_f16.py`.
- Push named refs only: `git push --all` or `--mirror` would publish the backup branch.
- If release is confirmed, remove the ignore rules and commit the files. The LFS rule for
  `StreamingAssets/biomes11/` already covers the blob.
- This doesn't change the status of `organoid_firing.f16`, already public on `main`.

## Related
[[../ARCHITECTURE]] §2 · [[../sessions/2026-11-04-mit-brain-scene]] · `tools/README.md`
