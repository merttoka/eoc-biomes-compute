# eoc-biomes-compute

Unity GPU-compute biomes + a memory architecture spanning installations.

Successor to the biomes/Metaesthetica work in [`edge-of-chaos-unity-compute`](https://github.com/merttoka/edge-of-chaos-unity-compute), split off so the workshop legacy code stays separate. Built around a TouchDesigner-orchestrated installation pipeline (sensors + Unity output mixed in TD), with a Python memory daemon that gives consecutive installations a persistent, queryable history.

## Layout

```
eoc-biomes-compute/
├── Assets/
│   ├── Workspace/
│   │   ├── 10.0 Metaesthetica/   earlier Unity scenes + sims
│   │   ├── 11.0 Biomes/          shared biome engine (src/, docs/, TestScene)
│   │   ├── 11.1 CURRENTS Scene/  CURRENTS show — scene + curated assets/snapshots
│   │   ├── 11.2 SIGGRAPH Scene/  SIGGRAPH show — scene + leaner assets
│   │   └── Includes/             shared compute helpers / shaders
│   ├── Settings/                 Unity 6 Build Profiles (macOS)
│   └── {EasyButtons, HDRPDefaultResources, TextMesh Pro, ...}  Unity infra
├── Packages/                     UPM (klak.spout)
├── ProjectSettings/
├── memory/
│   ├── daemon/                   Python: folder-watch → sqlite + OSC
│   ├── td/                       TouchDesigner files (placeholder)
│   └── docs/                     OSC contract, schema notes
└── docs/
    ├── ARCHITECTURE.md           Unity runtime + memory system reference
    ├── migration.md              memory architecture plan
    ├── adr/                      architecture decision records
    └── sessions/                 per-session handoff logs
```

## Getting started

**Unity:** open the project in Unity Hub (HDRP). First import regenerates `Library/`. One folder per show: `11.1 CURRENTS Scene/Scene_CURRENTS.unity` (the active build scene) and `11.2 SIGGRAPH Scene/Scene_SIGGRAPH.unity`; quick validation in `11.0 Biomes/TestScene.unity`. The shared sim engine (`src/`, `docs/`) lives in `11.0 Biomes/`; per-show scenes carry only their curated assets/materials.

**Memory daemon:**
```bash
cd memory/daemon
python3 -m venv .venv && source .venv/bin/activate
pip install -e .
memory-daemon --snapshot-dir "../../Assets/Workspace/11.1 CURRENTS Scene/assets/Snapshots" -v
```
OSC listens on `:9100`. `/memory/ping` and `/memory/count` are the v0 endpoints.

## Concepts

- **Memory across installations.** Each exhibition run writes snapshots/events; future installations read from accumulated memory, creating a slow dialogue across time and venues.
- **Folder as event log.** Unity's `SaveParams` writes `.asset` files; daemon watches and indexes. Folder is source of truth, DB is a derived index — replay-from-folder always works.
- **TD as orchestration hub.** Sensors, Unity Spout/Syphon output, and termite/biome sims composite in TouchDesigner. Daemon talks OSC to TD; both decoupled from each other.
- **Biological substrates as peer modality.** Brain organoid spike data (CSV) and live plant biopotential (planned) feed into memory alongside Unity params and viewer-derived signals.
- **Slow parameter crossfades.** `ParameterInterpolator` (11.0 `src/components/utils/`) eases a sim's live params from current state through a queue of preset `.asset` waypoints, sim-step driven, per-param enable toggles, shortest-arc hue — for long-running installations. Design: `docs/superpowers/specs/2026-06-07-parameter-interpolator-design.md`.
- **Termite simulation.** `TermiteSim` (11.0 `src/components/Sim/`) — a neuron-coupled pheromone-stigmergy swarm ported from `PDE_Nefeli_Termites`. Builds permeability mounds via the Biome/Umwelt. Runs at **131 agents (1:1 with neurons)**: each termite keeps a coherent heading and its own **fixed per-neuron-group turn-angle** (`turnAngleSpread`), so the 131 streams each curve with a distinct character. Designs: `docs/superpowers/specs/2026-06-07-termite-sim-design.md`, `docs/superpowers/specs/2026-06-11-termite-biome-features-design.md`.
- **Shared neuron firing (externally driven).** All three sims seed `agent i → neuron i%131` at the same `labels_positions.csv` positions and read one shared firing signal each step (faster movement + brighter trails). The firing *values* are a preprocessed float16 blob (131 neurons × 180000 frames; `tools/firing_csv_to_f16.py` converts the 729 MB source CSV); the **playhead is external** — another patch sends OSC `/index <int>` to scrub which frame fires (`NeuronFiringSource`), holding + decaying to quiet when silent. A count-independent **firing-ring overlay** marks each firing neuron on the composite so no sim's firing is lost in another's density. Design: `docs/superpowers/specs/2026-06-08-osc-neuron-firing-design.md`; decision: `docs/adr/0006-osc-neuron-firing.md`.
- **Shared chemical field (Biome).** An 11-channel GPU field (nutrient, three species pheromones, oxygen, temperature, waste, permeability, flow x/y, dispersal) that every sim reads and writes through its per-species `UmweltMapping`. The field runs a live PDE each step — temperature gradients generate flow, flow advects the chemicals, waste decomposes into nutrient, then everything diffuses and decays. Architecture: `docs/ARCHITECTURE.md` §3.3.
- **Dispersal channel + firing scatter.** A transient, fast-decay **Dispersal** field scatters all sims: they flee its gradient (negative chemotaxis) and accelerate out of it (a `SpeedBoost` Umwelt effect → `dispersalSpeedMode`, constant or multiplier). Neuron firing injects expanding Dispersal pulses via `BiomeInjector` — at fixed neuron positions or at the **live termite positions** — so a firing neuron blows its swarm outward; an expanding shockwave ring marks each pulse. Design: `docs/superpowers/specs/2026-06-11-termite-biome-features-design.md`.
- **Stream-safe resets.** Sim/biome resets clear GPU state **in place** instead of reallocating, so the composite (and per-sim) Syphon/NDI/Spout streams keep a stable texture and aren't torn down mid-show — no downstream reconnect/flash. OSC reset commands are marshalled to the main thread. Decision: `docs/adr/0008-clear-in-place-reset.md`.

See `docs/ARCHITECTURE.md` for the system reference and `docs/INDEX.md` for the doc map (ADRs, sessions, specs).

## Roadmap

- **Agent life/death + respawn** — `UmweltMapping` exposes the lifecycle params (oxygen/permeability death thresholds, corpse→waste amount/decay) but mortality is not yet executed; parked for this version. Planned as an aesthetic bloom/collapse mechanism rather than homeostasis.
- **Parameter literature grounding** — map the exposed sim/biome parameters onto real slime-mold / termite / flocking biology. Brief: `Assets/Workspace/11.0 Biomes/docs/RESEARCH_BRIEF.md`.
- **Layer & external-input integration** — Q10 decomposition fronts, homeostatic channel equilibria (O₂/Temperature relax to baseline, `docs/adr/0007`), and injector raw-sensor calibration are live in the show scene. Still planned: diurnal forcing, humidity, topographic stigmergy, texture-valued injector sources. Design: `Assets/Workspace/11.0 Biomes/docs/INTEGRATION_DESIGN.md`.
