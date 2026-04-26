# Session Notes

Append-only log of session handoffs. Newest first. Each entry: what shipped, what was decided, what's next.

---

## 2026-04-26 — repo split + memory daemon v0

**Shipped**
- New repo `eoc-biomes-compute` (private). Plain rsync from `edge-of-chaos-unity-compute`, fresh `git init`. No history preserved (filter-repo not installed at split time).
- Carried: `Assets/Workspace/{10.0 Metaesthetica, 11.0 Biomes, Includes}`, shared `Assets/{EasyButtons, HDRP, TMP, ...}`, `Packages/`, `ProjectSettings/`. Verified: Unity opens, `11.0 Biomes` works.
- `memory/daemon/` v0:
  - Python package, `pip install -e .`, entry point `memory-daemon`
  - `watchdog` folder-watch on `.asset` → SHA256 hash → SQLite `param_snapshots` row (idempotent on hash)
  - Bootstrap-as-replay (first run indexes existing files)
  - `python-osc` server on `:9100` with `/memory/ping`, `/memory/count`
  - Schema: `installations`, `param_snapshots`, `symbolic_tags` (see `memory/daemon/src/memory_daemon/schema.sql`)
- `memory/docs/osc-contract.md` with v1 endpoints planned
- `docs/migration.md` updated to reflect what was actually done

**Decided**
- Snapshot folder is source of truth, DB is derived index (replayable)
- Unity → daemon: folder-watch (no OSC push)
- v0 = metadata only, no embeddings yet (deferred until model decisions)
- `Includes/` copied verbatim, drift policy = revisit in 3+ months
- macOS-style ` 2.meta` / ` 3.meta` duplicates are gitignored (Unity import keeps regenerating them; root cause unknown)

**Open / next session**
1. Snapshot folder canonical path inside `Assets/Workspace/11.0 Biomes/` — needed to default `--snapshot-dir` and stop requiring CLI flag
2. `.asset` parser: extract param values from the Unity YAML format into `param_blob` JSON column
3. Embedding strategy for v1 (which model? CLIP for image-derived state? sentence-transformer for symbolic tags?)
4. Symbolic tag UI: TD panel vs tiny web dashboard vs CLI
5. Daemon process lifecycle: launchd/systemd vs TD-spawned
6. Canonical store decision: self-host Postgres+pgvector vs Supabase
7. TD MCP — user has unofficial Derivative-CEO TD MCP, not currently exposed to Claude Code session. If wired, can author `.toe` files; otherwise `memory/td/` stays manual
8. `git filter-repo` redo if history of biome work is wanted (`brew install git-filter-repo` then re-clone source repo, filter, force-push to this remote on a `with-history` branch)
