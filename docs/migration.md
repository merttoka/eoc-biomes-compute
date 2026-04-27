---
status: living
date: 2026-04-26
tags: [architecture, plan, memory]
related: [[INDEX]], [[sessions/2026-04-26-split-and-daemon-v0]]
---
# Migration & Memory Architecture Plan

Split `10.0 Metaesthetica` + `11.0 Biomes` from `edge-of-chaos-unity-compute` into this repo. Scaffold a memory system spanning installations, orchestrated in TouchDesigner, fed by Unity output (v1) and other modalities later.

**Status:** repo split done via plain copy. Memory daemon v0 (folder-watch → sqlite + OSC ping) shipped. v1 features (embeddings, retrieval, symbolic tags) pending.

---

## 1. Repo split — done

**Method used:** plain `rsync` + fresh `git init` (not `git filter-repo`, which wasn't installed at split time). History of source repo NOT preserved — first commit here starts fresh.

**Carried over:**
- `Assets/Workspace/{10.0 Metaesthetica, 11.0 Biomes, Includes}` (+ `.meta` siblings)
- `Assets/{EasyButtons, Editor, HDRPDefaultResources, Models, Presets, TextMesh Pro, UI Toolkit}` (shared infra)
- `Packages/` (klak.spout, manifest, lock)
- `ProjectSettings/`
- `.gitignore`, `.gitattributes`

**Excluded:** macOS Finder duplicates (` 2.meta`, ` 3.meta`, ` 2.asset`, ` 3.asset`), `_Recovery/`, `.DS_Store`. Pattern is gitignored to prevent re-introduction.

**To redo with history later (optional):**
```bash
brew install git-filter-repo
# clone source repo, filter to migrated paths, push to this remote (force or new branch)
```

**Includes drift policy:** copy now, single source of truth here. If both `edge-of-chaos-unity-compute` and this repo actively evolve `Includes/` for >~3 months, extract to a third repo and pull as UPM local git package.

**Known issue:** Unity's importer regenerates ` 2.meta` siblings on import (cause unknown — possibly Unity Cloud Drive / iCloud / Dropbox / VCS plugin). Gitignored as workaround. Investigate via `Edit > Project Settings > Editor > Version Control` if it returns under different filename patterns.

---

## 2. Memory architecture

### Topology (TD-centric)

```
   Sensors ─────► TouchDesigner ◄──Spout/Syphon──► Unity (biomes)
                       │                                │
                       │ OSC                            │ params snapshot
                       │                                │ (file or OSC)
                       ▼                                ▼
                  ┌────────── Memory Daemon ──────────┐
                  │  (Python, FastAPI + python-osc)   │
                  │                                   │
                  │  ├─ SQLite       (events, tags)   │
                  │  ├─ LanceDB      (embeddings)     │
                  │  └─ blob disk    (frames, audio)  │
                  └────────────────┬──────────────────┘
                                   │ scheduled sync
                                   ▼
                       Canonical archive (TBD)
```

**Why a separate daemon, not in TD:** TD owns real-time signal routing; daemon owns storage/embeddings/queries. Stable OSC contract between them — both sides churn independently.

### v0 — shipped (`memory/daemon/`)

- Folder-watch on `.asset` files via `watchdog`
- SHA256 content hash → SQLite insert (idempotent)
- Bootstrap-as-replay (first run indexes existing files)
- OSC server on `:9100` with `/memory/ping`, `/memory/count`
- Schema in `memory/daemon/src/memory_daemon/schema.sql`

### v1 scope: Unity output only — pending

**Inputs:**
- Unity param snapshots (already exists: `SaveParams` MFT action → timestamped `.asset`)
- **Daemon watches snapshot folder** (decided — see rationale below)
- **Pending:** parse `.asset` YAML, extract param values into `param_blob` JSON column (currently only filename + hash are stored)

**Why folder-watch (snapshot folder as event log):**
- Same shape as event-sourced pipelines (Kafka topic → consumer → vector index), with the filesystem as broker. Works here because: snapshots are low-frequency (human-triggered), single-consumer, local-disk writes are atomic enough.
- **Folder is source of truth**, daemon DB is a derived index. Nuke SQLite/LanceDB → replay folder → rebuild. `.asset` files are git-able, so memory is versioned for free.
- **Bootstrap = replay**: first daemon run walks folder and indexes everything. Same code path as steady-state.
- **Idempotency**: key entries by content hash or filename. Re-indexing a known file is a no-op.
- **One direction only**: daemon never writes back into the snapshot folder. Unity stays sole writer — no race conditions, no Unity asset-import side effects.
- Watcher: `watchdog` (Python), polling fallback for flaky volumes.
- Outgrow when: multi-consumer, sub-second event rates, or network-mounted storage with fuzzy write atomicity.

**Storage schema (initial):**
- `installations` — id, name, location, started_at, ended_at, hardware_notes
- `param_snapshots` — id, installation_id, ts, source, param_blob (jsonb), embedding_id, label
- `symbolic_tags` — id, target_kind, target_id, tag, note, author, ts
- LanceDB collection `embeddings` — id, vec, model_version

**OSC endpoints:**
- `/memory/snapshot` — ingest a snapshot
- `/memory/query/similar` — input: param vector or (installation_id, ts) → output: N similar past snapshots
- `/memory/tag` — write to symbolic_tags
- `/memory/preset/recall` — fetch a tagged snapshot by name

**Curated/symbolic layer:** small CLI or TD panel calls `/memory/tag`. Used to name preset moments ("Berlin opening pulse", "VISAP closing"), bridge to performances, and seed learning later.

### v2 extensions (later)

- **Brain organoid spike data** — ingest CSVs, embed activity windows, treat as modality alongside params
- **Plant biopotential** — TD reads sensor → OSC events to daemon
- **Viewer-derived signals** — TD computes motion energy / dwell / audio levels (no raw A/V stored), pushes OSC events
- **Feedback mechanisms** (deferred decision):
  - Seed-from-past — boot new installation from prior end-state, mutated
  - Live retrieval — current state queries memory, retrieved past moments modulate params via existing MFT OSC endpoints
  - Slow drift — nightly retrain small model on accumulated data, biases priors
- **Performance vs autonomous mode** — daemon runtime config flag; performance mode exposes more manual control surfaces, autonomous mode self-drives via feedback policy

### Operational notes

- **Privacy:** signage at venue entry announces data collection. No raw face/audio storage — derive at capture, store derived signals only. Opt-out: backburner.
- **Persistence across installations:** consecutive installations build on accumulated memory. Unit of "installation" = exhibition run (multi-day, single venue). Boot sessions within a run share installation_id.
- **Embedding versioning:** `model_version` on every embedding. Plan re-embed jobs when model changes.
- **Schema is additive-only.** JSONB for payload, typed indexes only on queried fields.

---

## 3. Tooling shortlist

| Layer | Pick |
|---|---|
| Daemon (v0) | Python 3.11+, `python-osc`, `watchdog`, `sqlite3` (stdlib) |
| Daemon (v1+) | add `lancedb`, embedding models — and `fastapi` if HTTP becomes needed |
| Local relational | SQLite (file-based) |
| Local vector | LanceDB (file-based, no server) |
| Embeddings | CLIP (image), CLAP (audio), sentence-transformers (text/symbolic) — local on Apple Silicon |
| Canonical archive | TBD: self-host Postgres+pgvector on VPS, or Supabase |
| TD ↔ daemon | OSC, schema-typed bundles |
| Unity ↔ daemon | OSC (same protocol) or HTTP for non-real-time |

---

## Unresolved questions

1. ~~New repo name?~~ → **`eoc-biomes-compute`** (private, on GitHub)
2. Canonical store: self-host Postgres+pgvector or Supabase?
3. v1 feedback mechanism — none, or a stub (e.g. preset recall only)?
4. ~~Unity → daemon: file-watch or OSC push?~~ → **folder-watch**
5. Symbolic tag UI: TD panel, tiny web dashboard, or CLI?
6. Daemon process lifecycle: launchd/systemd per installation, or TD spawns it?
7. Performance vs autonomous mode: same daemon, config flag — confirm?
8. Old repo cleanup later: leave workspaces as-is indefinitely, or revisit in N months?
9. Snapshot folder canonical path inside `Assets/Workspace/11.0 Biomes/` — needed to default the daemon `--snapshot-dir`.
10. TD MCP not present in current Claude Code session — server name + tool list needed if Claude should drive TD authoring.
