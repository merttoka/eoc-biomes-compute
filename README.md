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
- **Termite simulation.** `TermiteSim` (11.0 `src/components/Sim/`) — a neuron-coupled pheromone-stigmergy swarm ported from `PDE_Nefeli_Termites`. Builds persistent permeability mounds via a dedicated firing-gated build kernel (agent-authored topography — see Habitat partitioning below). Runs at **131 agents (1:1 with neurons)**: each termite keeps a coherent heading and its own **fixed per-neuron-group turn-angle** (`turnAngleSpread`), so the 131 streams each curve with a distinct character. Designs: `docs/superpowers/specs/2026-06-07-termite-sim-design.md`, `docs/superpowers/specs/2026-06-11-termite-biome-features-design.md`.
- **Shared neuron firing (externally driven).** All three sims seed `agent i → neuron i%131` at the same `labels_positions.csv` positions and read one shared firing signal each step (faster movement + brighter trails). The firing *values* are a preprocessed float16 blob (131 neurons × 180000 frames; `tools/firing_csv_to_f16.py` converts the 729 MB source CSV); the **playhead is external** — another patch sends OSC `/index <int>` to scrub which frame fires (`NeuronFiringSource`), holding + decaying to quiet when silent. A count-independent **firing-ring overlay** marks each firing neuron on the composite so no sim's firing is lost in another's density. Design: `docs/superpowers/specs/2026-06-08-osc-neuron-firing-design.md`; decision: `docs/adr/0006-osc-neuron-firing.md`.
- **Shared chemical field (Biome).** A 12-channel GPU field (nutrient, three species pheromones, oxygen, temperature, waste, permeability, flow x/y, dispersal, humidity) that every sim reads and writes through its per-species `UmweltMapping`. The field runs a live PDE each step — temperature gradients generate flow, flow advects the chemicals, waste decomposes into nutrient, temperature evaporates humidity, then everything diffuses and decays. Architecture: `docs/ARCHITECTURE.md` §3.3.
- **Habitat partitioning (permeability mounds).** Permeability (ch7) is no longer static terrain — it starts uniform-open and is authored entirely by termites, who lower it probabilistically (pulsing with neuron firing) into persistent **walls** that heal back only very slowly. Each species is then confined to its own `preferredPermeabilityMin/Max` band — Boids to open ground, Physarum to the mid transition at wall edges, Termites to the solid interiors — via a perception habitat gate (out-of-band → steer-back + a floored speed penalty). Because termites build the very structure that confines every species, habitat is emergent and successional; `ResetTermites` melts the walls and frees everyone. A post-composite overlay paints the mounds. Design: `docs/adr/0010-permeability-agent-built-topography.md`.
- **Humidity channel.** A high-diffusion, flow-advected **Humidity** field that relaxes toward an ambient baseline (renewable) and is driven off by heat — `humidity -= temperatureToEvaporation·max(0, temp−0.5)` each step. Hot zones leave a drying wake; the steep `|∇Humidity|` edge they carve is the termite build cue, and the renewable moisture is a depletable resource sims can compete for via their `UmweltMapping` reads. Design: `Assets/Workspace/11.0 Biomes/docs/INTEGRATION_DESIGN.md` (Tier 2).
- **Dispersal channel + firing scatter.** A transient, fast-decay **Dispersal** field scatters all sims: they flee its gradient (negative chemotaxis) and accelerate out of it (a `SpeedBoost` Umwelt effect → `dispersalSpeedMode`, constant or multiplier). Neuron firing injects expanding Dispersal pulses via `BiomeInjector` — at fixed neuron positions or at the **live termite positions** — so a firing neuron blows its swarm outward; an expanding shockwave ring marks each pulse. The live-position path reads agent positions back via **non-blocking `AsyncGPUReadback`** (`useAsyncReadback`, default on) — removes the per-frame CPU↔GPU sync stall (the serialization barrier, not just the copy), a large win on both discrete GPUs *and* Apple Silicon; positions lag 1-2 frames. Design: `docs/superpowers/specs/2026-06-11-termite-biome-features-design.md`.
- **Stream-safe resets.** Sim/biome resets clear GPU state **in place** instead of reallocating, so the composite (and per-sim) Syphon/NDI/Spout streams keep a stable texture and aren't torn down mid-show — no downstream reconnect/flash. OSC reset commands are marshalled to the main thread. Decision: `docs/adr/0008-clear-in-place-reset.md`.
- **Diurnal sun (environmental pump).** A procedural `BiomeInjector` source sweeps a warm zone across **Temperature** L→R, phased off the neuron playhead so one firing-blob playthrough (`/index` 0→last) is one "day" and the `index=0` loop reset lands on sunrise. Kept **indirect**: no sim reads Temperature directly — the moving heat re-aims flow, drives the humidity drying-wake, sharpens the Q10 fertility front, and bends permeability, so the whole ecosystem breathes on a diurnal rhythm through the channels sims already read. Tuning is about *headroom* (agent metabolic heat vs Temperature relax) and *gradient* (Temperature `diffuseRate`), not a direct chemotaxis leash. Design: `Assets/Workspace/11.0 Biomes/docs/INTEGRATION_DESIGN.md` (Tier 1, row 6).
- **FPS-independent sim.** `Step()` runs on a fixed clock in `FixedUpdate` (`Time.fixedDeltaTime = 1/simRate`, default **60 Hz**) so sim speed is identical across installs regardless of render FPS; the composite `Render()` is decoupled to `LateUpdate` (one composite per rendered frame). `maxAllowedTimestep` (Unity's `Time.maximumDeltaTime`) caps catch-up — weak installs run *long*, never *fast* (sim slows uniformly under load, no burst/stutter). Shader RNG seeds from a monotonic per-sim step, not `Time.frameCount`, so agents advance deterministically when multiple steps run per frame. 60 Hz = legacy per-frame feel, so no content re-tuning. Architecture: `docs/ARCHITECTURE.md` §3.1; design: `docs/superpowers/specs/2026-07-11-fps-independent-sim-design.md`.
- **MIDI piano composition mixer.** `MidiPianoMixer` turns a USB MIDI piano (Clavinova) into a live layer mixer over the composite. It opens its **own Minis device connection** (self-contained, like `MidiFighterTwister` — no `MIDIMapping` needed): white keys from A0 up auto-assign to the sims in `SimulationManager.simulations` order, and **note velocity sets each sim's `compositeWeight`** (chords crossfade; weights ease via `smoothingSeconds`; `weightMax` allows boost past 1, note-off holds the level). Command zone (top octave): **C8** → full `Reset()`, **B7** → `ResetSimsOnly()` (both require the **sustain/damper pedal held**), **C7** → pause (`stepsPerTick` 0↔1). Coexists with the Midi Fighter Twister — disjoint controls, the piano owns `compositeWeight`. The custom Inspector shows the live key→sim map. Spec/plan: `docs/superpowers/specs/2026-07-11-clavinova-composition-mixer-design.md`, `docs/superpowers/plans/2026-07-11-clavinova-composition-mixer.md`.
- **MFT LED legibility.** `MidiFighterTwister` LEDs encode *which type* and *which bank* at a glance: SimParam knobs keep a **flat family hue** (physarum blue, boid orange, termite yellow) with a **per-type brightness ramp** (mid→full across type index, `typeBrightnessMid` tunable; hue gradients tried first but read as confusing on device), non-sim knobs run full brightness, and every soft/HW bank switch triggers a ~0.7s **bank flash** (top row counts the soft bank in its identity color, bottom row counts the HW bank in white). Spec: `docs/superpowers/specs/2026-07-19-mft-led-feedback-design.md`.
- **Temporal Composer (11.2 SIGGRAPH show).** A Unity Timeline (`PlayableDirector`)-driven show sequencer: track mixers push per-frame draw state into `CompositeSequencer`, which owns its own `composerOutTex` (rez independent of sim rez, stable RT per `docs/adr/0008-clear-in-place-reset.md`) and composites sim output + cells + patches each `LateUpdate`. Five track types: **Biome Cell** (2–4 self-paced `BiomeCellRig` sub-sims, Overlay/Replace onto a `dstRect`), **Patch Scatter** (Anadol-style scattered patches crossfading raw sim → diffusion return, deterministic per seed — pure logic lives in the engine-free `Biomes.Sequencer.Core` assembly, unit-tested outside play mode), **Param Snapshot** (eased live-param morph to a snapshot asset), **Routing** (overrides a sim's external-influence source), and reset `SignalEmitter`s. `ScreenLayout` and the outbound sender read `composerOutTex` once wired per the manual-setup doc (scene wiring pending) — `ExternalTextureSender` gained `SendSource.ComposerOutput` (default stream `EoC/Composer`); a second `ExternalTextureReceiver` (`TD_Diffusion` stream) closes the loop with TouchDesigner's StreamDiffusion, Spout both directions on the show machine. Cap `SimulationManager.targetFPS`/`BiomeCellRig.cellRate` so Unity leaves GPU headroom for the diffusion step. Author clips via `Biomes → Biome Palette` (thumbnail grid over snapshot assets, capture-from-composite, drag/insert-at-playhead). Design: `docs/superpowers/specs/2026-07-19-temporal-composer-design.md`; manual setup/perf checklist: `docs/superpowers/2026-07-19-temporal-composer-manual-setup.md`.

See `docs/ARCHITECTURE.md` for the system reference and `docs/INDEX.md` for the doc map (ADRs, sessions, specs).

## Roadmap

- **Agent life/death + respawn** — `UmweltMapping` exposes the lifecycle params (oxygen/permeability death thresholds, corpse→waste amount/decay) but mortality is not yet executed; parked for this version. Planned as an aesthetic bloom/collapse mechanism rather than homeostasis.
- **Parameter literature grounding** — map the exposed sim/biome parameters onto real slime-mold / termite / flocking biology. Brief: `Assets/Workspace/11.0 Biomes/docs/RESEARCH_BRIEF.md`.
- **Layer & external-input integration** — Q10 decomposition fronts, homeostatic channel equilibria (O₂/Temperature relax to baseline, `docs/adr/0007`), and injector raw-sensor calibration are live in the show scene. Humidity (12th channel, temperature-driven evaporation) is now live. Still planned: diurnal forcing, topographic stigmergy, texture-valued injector sources. Next-step coupling ideas (outside-signal routing, generalizing field→agent beyond the 4 perception slots, lifecycle death→succession) are sketched in `Assets/Workspace/11.0 Biomes/docs/INTERACTION_DESIGN_II.md`. Design: `Assets/Workspace/11.0 Biomes/docs/INTEGRATION_DESIGN.md`.
