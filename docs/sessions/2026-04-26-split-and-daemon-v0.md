---
status: closed
date: 2026-04-26
tags: [session, repo-split, daemon, scaffolding]
related: [[../migration]], [[../adr/0001-rsync-over-filter-repo]], [[../adr/0002-folder-as-event-log]]
---
# Split + memory daemon v0

## Shipped

- New repo `eoc-biomes-compute` (private). Plain rsync from `edge-of-chaos-unity-compute`, fresh `git init`. No history preserved.
- Carried: `Assets/Workspace/{10.0 Metaesthetica, 11.0 Biomes, Includes}`, shared `Assets/{EasyButtons, HDRP, TMP, ...}`, `Packages/`, `ProjectSettings/`. Verified: Unity opens, `11.0 Biomes` works.
- `memory/daemon/` v0:
  - Python package, `pip install -e .`, entry point `memory-daemon`
  - `watchdog` folder-watch on `.asset` → SHA256 hash → SQLite `param_snapshots` row (idempotent on hash)
  - Bootstrap-as-replay (first run indexes existing files)
  - `python-osc` server on `:9100` with `/memory/ping`, `/memory/count`
  - Schema: `installations`, `param_snapshots`, `symbolic_tags` (see `memory/daemon/src/memory_daemon/schema.sql`)
- `memory/docs/osc-contract.md` with v1 endpoints planned
- `docs/migration.md` updated to reflect what was actually done
- Top-level `README.md` and this session log

## Decided

See ADRs for details:
- [[../adr/0001-rsync-over-filter-repo]] — split method
- [[../adr/0002-folder-as-event-log]] — Unity → daemon transport
- [[../adr/0003-local-first-storage]] — SQLite + LanceDB local, canonical archive deferred
- [[../adr/0004-td-as-orchestration-hub]] — TD owns real-time signal routing
- [[../adr/0005-includes-copied-verbatim]] — no submodule/UPM yet

Other decisions:
- v0 = metadata only, no embeddings yet (deferred until model decisions)
- macOS-style ` 2.meta` / ` 3.meta` duplicates gitignored (Unity import keeps regenerating them; root cause unknown)

## Open / next session

1. Snapshot folder canonical path inside `Assets/Workspace/11.0 Biomes/` — needed to default `--snapshot-dir`
2. `.asset` parser: extract param values from the Unity YAML format into `param_blob` JSON column
3. Embedding strategy for v1 (CLIP for image-derived state? sentence-transformer for symbolic tags?)
4. Symbolic tag UI: TD panel vs tiny web dashboard vs CLI
5. Daemon process lifecycle: launchd/systemd vs TD-spawned
6. Canonical store decision: self-host Postgres+pgvector vs Supabase
7. TD MCP — user has unofficial Derivative-CEO TD MCP, not currently exposed to Claude Code session
8. `git filter-repo` redo if history of biome work is wanted
