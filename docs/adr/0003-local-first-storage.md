---
status: accepted
date: 2026-04-26
tags: [adr, memory, storage]
related: [[../migration]], [[../adr/0002-folder-as-event-log]]
---
# ADR-0003: Local-first storage — SQLite + LanceDB per node

## Context

Memory must persist across installations worldwide. Galleries have unreliable network. Where does memory live at runtime?

Options:
1. Centralized cloud DB at runtime (every read/write hits remote)
2. Federated peer-to-peer (IPFS-like)
3. Local-first per installation, scheduled sync to a canonical archive

## Decision

Local-first. Each installation node owns its data.

- **Relational** — SQLite (file-based, stdlib)
- **Vectors** (v1+) — LanceDB (file-based, no server)
- **Blobs** — local disk
- **Sync** — scheduled push to a canonical archive when online (canonical store TBD: self-host Postgres+pgvector vs Supabase)

## Consequences

- Works offline. Installation degrades gracefully if network drops mid-run.
- No request-latency dependency on remote infra during the actual show.
- Sync becomes its own concern (eventual consistency, conflict resolution if multi-master).
- Canonical store decision can be deferred — local nodes work without it.
- DB migrations need to roll across all installation nodes when schema changes; plan migration strategy before v1 ships.

## Related

- [[../sessions/2026-04-26-split-and-daemon-v0]]
- `memory/daemon/src/memory_daemon/store.py`
