# eoc-biomes-compute

Unity GPU-compute biomes + a memory architecture spanning installations.

Successor to the biomes/Metaesthetica work in [`edge-of-chaos-unity-compute`](https://github.com/merttoka/edge-of-chaos-unity-compute), split off so the workshop legacy code stays separate. Built around a TouchDesigner-orchestrated installation pipeline (sensors + Unity output mixed in TD), with a Python memory daemon that gives consecutive installations a persistent, queryable history.

## Layout

```
eoc-biomes-compute/
├── Assets/
│   ├── Workspace/
│   │   ├── 10.0 Metaesthetica/   Unity scenes + sims
│   │   ├── 11.0 Biomes/          active biomes work (neuron firing, MFT, snapshots)
│   │   └── Includes/             shared compute helpers / shaders
│   └── {EasyButtons, HDRPDefaultResources, TextMesh Pro, ...}  Unity infra
├── Packages/                     UPM (klak.spout)
├── ProjectSettings/
├── memory/
│   ├── daemon/                   Python: folder-watch → sqlite + OSC
│   ├── td/                       TouchDesigner files (placeholder)
│   └── docs/                     OSC contract, schema notes
└── docs/
    ├── migration.md              architecture + plan
    └── SESSION-NOTES.md          handoff log per session
```

## Getting started

**Unity:** open the project in Unity Hub (HDRP). First import regenerates `Library/`. Test scenes in `11.0 Biomes/` and `10.0 Metaesthetica/`.

**Memory daemon:**
```bash
cd memory/daemon
python3 -m venv .venv && source .venv/bin/activate
pip install -e .
memory-daemon --snapshot-dir "../../Assets/Workspace/11.0 Biomes/Snapshots" -v
```
OSC listens on `:9100`. `/memory/ping` and `/memory/count` are the v0 endpoints.

## Concepts

- **Memory across installations.** Each exhibition run writes snapshots/events; future installations read from accumulated memory, creating a slow dialogue across time and venues.
- **Folder as event log.** Unity's `SaveParams` writes `.asset` files; daemon watches and indexes. Folder is source of truth, DB is a derived index — replay-from-folder always works.
- **TD as orchestration hub.** Sensors, Unity Spout/Syphon output, and termite/biome sims composite in TouchDesigner. Daemon talks OSC to TD; both decoupled from each other.
- **Biological substrates as peer modality.** Brain organoid spike data (CSV) and live plant biopotential (planned) feed into memory alongside Unity params and viewer-derived signals.
- **Slow parameter crossfades.** `ParameterInterpolator` (11.0 `src/components/utils/`) eases a sim's live params from current state through a queue of preset `.asset` waypoints, sim-step driven, per-param enable toggles, shortest-arc hue — for long-running installations. Design: `docs/superpowers/specs/2026-06-07-parameter-interpolator-design.md`.

See `docs/migration.md` for the full architecture plan and open questions, `docs/SESSION-NOTES.md` for current state.
