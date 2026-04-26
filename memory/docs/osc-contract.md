# OSC contract

UDP, default port `9100`. Daemon listens; TD/Unity send.

## v1 endpoints (implemented)

| Address | Args | Behavior |
|---|---|---|
| `/memory/ping` | any | log + reply on `/memory/pong` (TBD reply path) |
| `/memory/count` | none | log current snapshot count |

## v2 planned

| Address | Args | Returns (via reply addr) |
|---|---|---|
| `/memory/snapshot` | `(filename:str)` | manual ingest trigger |
| `/memory/query/similar` | `(snapshot_id:int, k:int)` | top-k similar snapshot ids |
| `/memory/tag` | `(target_kind:str, target_id:int, tag:str, note:str)` | ack |
| `/memory/preset/recall` | `(name:str)` | snapshot id matching named preset |

## Reply convention (TBD)

Two options under consideration:
1. Daemon replies on a fixed address `/memory/reply/<endpoint>` to a configured client host:port
2. Caller embeds reply address in args (`/memory/query/similar (snapshot_id, k, "/td/memory/result")`)

Lean toward (2) — keeps the daemon stateless about clients.
