# memory

Memory architecture for the biomes installation network. See `../docs/migration.md` (in source repo) for the conceptual plan.

## Layout

```
memory/
├── daemon/      Python service: folder-watch → sqlite/lancedb, OSC API
├── td/          TouchDesigner project files (orchestration hub)
└── docs/        OSC contract, schema notes
```

## v1 scope

- Watch Unity snapshot folder (`.asset` files from `SaveParams` MFT action)
- Index into local SQLite (metadata) — embeddings come in v2
- OSC API for query/recall/tag — endpoints in `docs/osc-contract.md`

## Run

```
cd daemon
python -m venv .venv && source .venv/bin/activate
pip install -e .
memory-daemon --snapshot-dir ../../Assets/Workspace/11.0\ Biomes/Snapshots
```
