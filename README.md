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
- **Termite simulation.** `TermiteSim` (11.0 `src/components/Sim/`) — a neuron-coupled pheromone-stigmergy swarm ported from `PDE_Nefeli_Termites`. Optional per-agent "firing" (131 neurons, agent `i`→neuron `i%131`) drives speed + bright trails, read from a preprocessed float16 blob in `StreamingAssets/biomes11/` (`tools/firing_csv_to_f16.py` converts the 729 MB source CSV). Builds permeability mounds via the Biome/Umwelt. Design: `docs/superpowers/specs/2026-06-07-termite-sim-design.md`.
- **Shared chemical field (Biome).** A 10-channel GPU field (nutrient, three species pheromones, oxygen, temperature, waste, permeability, flow x/y) that every sim reads and writes through its per-species `UmweltMapping`. The field runs a live PDE each step — temperature gradients generate flow, flow advects the chemicals, waste decomposes into nutrient, then everything diffuses and decays. Architecture: `docs/ARCHITECTURE.md` §3.3.

See `docs/migration.md` for the full architecture plan and open questions, `docs/SESSION-NOTES.md` for current state.

## Roadmap

- **Agent life/death + respawn** — `UmweltMapping` exposes the lifecycle params (oxygen/permeability death thresholds, corpse→waste amount/decay) but mortality is not yet executed; parked for this version. Planned as an aesthetic bloom/collapse mechanism rather than homeostasis.
- **Parameter literature grounding** — map the exposed sim/biome parameters onto real slime-mold / termite / flocking biology. Brief: `Assets/Workspace/11.0 Biomes/docs/RESEARCH_BRIEF.md`.
