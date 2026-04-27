---
status: accepted
date: 2026-04-26
tags: [adr, repo, dependencies]
related: [[../migration]], [[../adr/0001-rsync-over-filter-repo]]
---
# ADR-0005: `Assets/Workspace/Includes/` copied verbatim, not vendored

## Context

`Assets/Workspace/Includes/` holds shared compute helpers / shaders used by both `10.0 Metaesthetica` and `11.0 Biomes`. The original repo also depends on it for workshop-era workspaces. Three options for the new repo:

1. Copy verbatim (drift over time, fine if one side stops evolving Includes)
2. Vendor as a UPM local git package — single source of truth, both repos pull
3. Git submodule — same idea, more ceremony

## Decision

Copy verbatim. Defer vendoring until pain materializes.

## Consequences

- **KISS for now**: no submodule init steps, no UPM package authoring, no separate repo to maintain.
- **Drift risk**: if both repos actively edit `Includes/` for >~3 months, manual reconciliation gets painful. Revisit then.
- **Trigger for revisit**: a bug fix made in one repo's `Includes/` that should propagate to the other.
- Once vendored, both repos remove `Assets/Workspace/Includes/` from working tree and pull via UPM `file:` or `git+ssh:` URL.

## Related

- [[../sessions/2026-04-26-split-and-daemon-v0]]
