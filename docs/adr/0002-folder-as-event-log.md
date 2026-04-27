---
status: accepted
date: 2026-04-26
tags: [adr, memory, daemon, transport]
related: [[../migration]], [[../adr/0003-local-first-storage]]
---
# ADR-0002: Snapshot folder is the source of truth (event log)

## Context

Unity's `SaveParams` MFT action writes timestamped `.asset` files to a snapshot folder. Memory daemon needs to ingest these. Two options:

1. **Folder-watch** — daemon watches the folder, ingests on file-create event
2. **OSC push** — Unity sends an OSC message to the daemon at save time

## Decision

Folder-watch. Snapshot folder is the source of truth; daemon's SQLite/LanceDB is a derived index.

## Consequences

- **Replayable**: nuke daemon DBs, re-run, walk the folder, rebuild. Same code path as steady-state ingestion.
- **Idempotent**: keyed on content hash. Re-indexing a known file is a no-op.
- **One-direction**: daemon never writes back into the snapshot folder. Unity stays the sole writer — no race conditions, no Unity asset-import side effects.
- **Decoupled lifecycle**: daemon can be offline without breaking Unity. Snapshots queue on disk.
- **Outgrowth points**: multi-consumer, sub-second event rates, or network-mounted storage with fuzzy write atomicity → migrate to a real broker (Redis Streams / NATS / Kafka).

## Related

- [[../sessions/2026-04-26-split-and-daemon-v0]]
- `memory/daemon/src/memory_daemon/watcher.py`
