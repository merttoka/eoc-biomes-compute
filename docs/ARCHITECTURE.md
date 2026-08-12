---
status: living
date: 2026-07-15
tags: [architecture, unity, gpu, memory, biomes]
related: [[migration]], [[INDEX]]
---
# Architecture

System reference for `eoc-biomes-compute`. Two halves: a **Unity GPU-compute
biome runtime** (real-time agent sims coupled to a shared chemical field) and a
**cross-installation memory system** (Python daemon + TouchDesigner orchestration).
This doc owns the Unity runtime; [[migration]] owns the memory plan in depth —
update both when the corresponding half changes.

---

## 1. System topology

```
   Sensors ─────► TouchDesigner ◄──Spout/Syphon──► Unity (biome compute)
                       │                                │
                       │ OSC                            │ params snapshot (.asset)
                       ▼                                ▼
                  Memory Daemon (Python) ── watches snapshot folder
                  SQLite (events/tags) · LanceDB (embeddings) · blobs
                       │ scheduled sync
                       ▼
                  Canonical archive (TBD)
```

- **TouchDesigner** is the real-time orchestration hub: sensors in, Unity visuals
  (Syphon/NDI/Spout) + params (OSC) in, composites everything for output. See
  [[adr/0004-td-as-orchestration-hub]].
- **Unity** runs the biome simulation and emits visuals + parameter snapshots. Video
  share with TD is implemented (§3.8): Syphon/NDI/Spout send of composite/sim/biome
  textures, plus one received texture as influence.
- **Memory daemon** indexes snapshots into a local-first store, giving consecutive
  installations a persistent, queryable history. See [[migration]] for v0/v1/v2
  scope and the OSC contract.

The conceptual throughline: each exhibition run writes memory; future runs read
from accumulated memory — a slow dialogue across time and venues.

---

## 2. Repo layout

```
Assets/Workspace/
  10.0 Metaesthetica/   earlier Unity scenes + sims
  11.0 Biomes/          shared biome engine — src/, docs/, TestScene.unity (this doc's focus)
  11.1 CURRENTS Scene/  CURRENTS show — Scene_CURRENTS.unity + curated assets/materials/snapshots
  11.2 SIGGRAPH Scene/  SIGGRAPH show — Scene_SIGGRAPH.unity + leaner curated assets/materials
  Includes/             shared compute helpers / shaders (copied verbatim — ADR-0005)
Assets/Settings/        Unity 6 Build Profiles (macOS standalone)
Assets/StreamingAssets/ runtime-loaded blobs (e.g. biomes11/organoid_firing.f16, LFS)
Packages/               UPM deps (klak.spout / klak.ndi / klak.syphon)
tools/                  offline preprocessors (firing_csv_to_f16.py)
memory/
  daemon/               Python: folder-watch → SQLite + OSC
  td/                   TouchDesigner files (placeholder)
  docs/                 OSC contract, schema notes
docs/                   this file, migration.md (living plan), adr/, sessions/, specs/, plans/
```

**Per-show scene workspaces** ([[adr/0009-per-show-scene-workspaces]]): `11.0 Biomes/` owns
the shared engine (`src/`, `docs/`) only; each exhibition gets its own folder (`11.1 CURRENTS`,
`11.2 SIGGRAPH`) holding just that show's scene + a curated `assets/`+`materials/` set, so
per-show tuning is isolated while the engine stays single-source. `11.0 Biomes/TestScene.unity`
is the engine smoke-test scene. The active build scene (EditorBuildSettings) is
`11.1 CURRENTS Scene/Scene_CURRENTS.unity`.

### `11.0 Biomes/src/` structure

```
src/
  components/
    core/       SimulationManager, SimulationBase, Biome, BiomeFieldConfig,
                UmweltMapping, ExternalInputProvider, GPUResourceManager
    Sim/        BoidSim, PhysarumSim, TermiteSim (concrete SimulationBase subclasses)
    network/    MidiFighterTwister, MIDIMapping, OSCMapping, BiomeInjector,
                NeuronFiringSource, ExternalTexture* (control surfaces + external inputs)
    utils/      ParameterRecorder, ParameterInterpolator, ScreenLayout
    sequencer/  CompositeSequencer, BiomeCellRig, tracks/ (Timeline tracks — §3.9)
  sequencer_core/ Biomes.Sequencer.Core — engine-free patch-scheduling logic (§3.9)
  params/     BoidParams, PhysarumParams, TermiteParams, ParamRange, ColorPalette, IParamSet
  Editor/     custom inspectors (ParamsEditor, MFT/MIDI editors, ScreenLayoutPreview,
              sequencer/BiomePaletteWindow — §3.9)
```

---

## 3. Unity biome runtime

### 3.1 Orchestration — `SimulationManager`

A single `SimulationManager` owns resolution, timing, the `Biome`, a list of
`SimulationBase` sims, the `ExternalInputProvider`, and the composite output. It is
the only driver: `Reset()` (re)initializes everything; `FixedUpdate()` calls `Step()`
`stepsPerTick` times on a fixed clock — `Time.fixedDeltaTime = 1/simRate` (default
60 Hz) — so sim speed is independent of render FPS; `LateUpdate()` runs the composite
`Render()` once per rendered frame. Unity's `Time.maximumDeltaTime` (exposed as
`maxAllowedTimestep`) caps catch-up on slow hardware. `SimStepCount` is the canonical
sim clock (monotonic, increments per `Step()`), used by time-based tooling.

`Reset()` is **clear-in-place** ([[adr/0008-clear-in-place-reset]]): each owner
(`SimulationManager`, `Biome`, every `SimulationBase`, `NeuronFiringSource`,
`ExternalTextureReceiver`) splits into a guarded `Allocate()` + the GPU clear/respawn
dispatches. `Allocate()` runs only when an **allocation signature** changes — output
resolution, perception scale, agent count, or type count; otherwise resources persist
and only the clear/respawn runs. This keeps `compositeOutTex`/sim `outTex` instances
stable across a reset, so an active Syphon stream is not torn down (§3.8). A genuine
resolution/structural change still reallocates (one Syphon re-init — a Play-stopped
operation). `ResetSimsOnly()` resets sims (clear-in-place) while preserving the biome
and composite.

### 3.2 The per-step pipeline

`SimulationManager.Step()` runs a fixed sequence each simulation step (the composite
render is **not** part of it — it runs separately in `LateUpdate`):

```
0. ExternalInput.UpdateInput()      → influence texture assigned to every sim
1. Biome.BuildPerceptionTex(sim)    → biome fields sampled through each sim's Umwelt
2. sim.Step()  (for each sim)       → agents sense perception + move + write trails
3. Biome.WriteField(sim agents)     → sims deposit/consume biome channels (per Umwelt)
4. Biome.Step()                     → flow gen → advect → cross-field react → diffuse/decay
5. Render()                         → composite all sim outputs (+ optional overlay)
```

This is a **read-modify-write loop over a shared field**: sims read the biome
(perception), act, then write back into the biome, and the biome evolves. The
coupling direction each way is defined per-sim by its `UmweltMapping`.

### 3.3 The biome field — `Biome` + `BiomeFieldConfig`

The biome is a double-buffered `Texture2DArray` of **15 scalar channels**
(`BiomeChannel`, defined in `core/BiomeFieldConfig.cs` as a `static class` of `const int` —
not an enum, despite older notes): `Nutrient, Pheromone0, Pheromone1, Pheromone2, Oxygen,
Temperature, Waste, Permeability, FlowX, FlowY, Dispersal, Humidity, HumidityGrad,
Excitability, Substrate` (Pheromone0/1/2 are per-species scents; **Dispersal** is a
transient, fast-decay agitation field that scatters all sims — see §3.5; **Humidity** is a
high-diffusion, flow-advected moisture field that relaxes to an ambient baseline and is
evaporated by Temperature; **Permeability** starts at a uniform-open baseline and relaxes
toward it near-zero — its structure is authored entirely by termites, not static terrain,
see §3.4 + [[adr/0010-permeability-agent-built-topography|ADR-0010]];
**Excitability** and **Substrate** are CA-owned publish targets, written at full gain while
a `FieldSimulationBase` is bursting; once the burst goes idle it stops publishing and the PDE
takes the deposit over — these channels deliberately do bleed and advect (`diffuseRate` 0.96,
`decayRate` 0.004), so the trace erodes rather than sitting inert). Per-channel
behavior (diffuse rate, decay, advected-by-flow, initial value, homeostatic relax) comes
from `BiomeFieldConfig` and is uploaded as a structured buffer. The channel count is
hardcoded in two sync'd places — `BiomeChannel.Count/Names` (the C# source of truth; both
`ExternalTextureSender` and the debug grid reference `BiomeChannel.Names` directly) and
`Biome.compute` `CH_COUNT`; **adding a channel means updating both** plus each
`BiomeFieldConfig` asset's channel list.
`Biome.Step()` runs the field dynamics on the GPU as a **ping-pong chain** —
temperature gradients generate flow → flow advects the advectable channels →
cross-field interactions (waste→nutrient, temp→permeability, temp→humidity evaporation)
react → diffuse + decay
— each pass reading the previous pass's buffer and swapping. The partial passes
(flow/advect/interact) call `CopyAllChannels` first so the channels they don't write
survive the swap; **any new partial pass must do the same.** Field samples use
texel-center UVs (`(id+0.5)/rez`) to avoid half-texel diffusion drift. Flow transports
the chemical fields only — agents are never pushed by it. Resolution is independent of
sim resolution; sim↔field coordinates are mapped by ratio.

### 3.4 Simulations — agent sims and field sims

Two families share one base. **Agent sims** (`PhysarumSim` / `BoidSim` / `TermiteSim`) carry
populations of GPU agents. **Field sims** (`FieldSimulationBase` → `CyclicCASim` /
`LookupCASim`) have no agents at all — their entire state is one integer per cell.

Field sims still *derive* from `SimulationBase` and seal the agent contract to
`null`/`0`/`1`; `SimulationManager`'s existing null-guards then route around them, so a
grid process needs no orchestrator special-casing. They own a double-buffered
`stateRead`/`stateWrite` pair (mandated at the base so an in-place update is inexpressible),
render into `outTex` like any other layer, and may publish their normalized state into a
biome channel so agent species perceive them through `UmweltMapping` with **no shader
change** — only a mapping entry. See
[[adr/0011-field-native-sims-derive-simulationbase|ADR-0011]].

Field sims are **event-driven** by default (`burstEnabled`). A burst is triggered by a rising
edge of the neuron firing level, or — when `burstOnFrameAdvance` is on, replacing the edge
trigger — by a playback-frame advance whose aggregate activity clears `burstFiringThreshold`,
so a dense `/index` stream produces sporadic bursts instead of one that never lets go. That
activity is normalized to the loaded recording's own peak per-frame synchrony (`NeuronFiringSource.FrameActivity`,
computed once at blob load), so the threshold reads as "fraction of this recording's strongest
frame" rather than an absolute mean tied to how sparse the source recording happens to be. It may
also be triggered directly by `TriggerBurst()`. A burst seeds its grid — optionally from a biome
channel by threshold, so the automaton grows out of the ecosystem rather than an abstract figure
— holds for `burstSustainSteps`, then fades. While idle a field sim dispatches nothing at all:
no rule, no render, no publish. Because it stops publishing on going idle, the lattice it
deposited stays in its biome channel and is eroded, spread and advected by the PDE from then on.
Grid size is authored as an absolute cell height (`cellRezHeight`), with width derived from the
master's aspect, so the automaton's on-screen scale does not move when output resolution does.

> A subclass that overrides `Allocate()` **must** call `MarkAllocated()`. The clear-in-place
> signature is private and that is the only way to stamp it; skipping it makes
> `NeedsAllocation()` permanently true, so every reset reallocates and downstream Syphon
> servers tear down each time — the failure [[adr/0008-clear-in-place-reset|ADR-0008]] exists
> to prevent.

`SimulationBase` is the abstract GPU-agent template: trail texture arrays (per-type
+ total), a perception texture, an external-influence texture, **neuron-position seeding**
(`BuildNeuronPositions`, consuming the normalized layout `SimulationManager` pushes from
`NeuronFiringSource` — the single CSV owner, [[adr/0014-neuron-layout-single-owner|ADR-0014]])
and **shared neuron firing**
(`BindNeuronFiring`, `firingThreshold`), and the common kernel set
(reset/move/write-trails/diffuse/render). Subclasses implement the agent model:

- **`PhysarumSim`** — slime-mold agents (sense-angle/distance, turn, deposit, eat).
- **`BoidSim`** — flocking agents with a GPU spatial hash (separate/align/attract
  ranges, food-seeking).
- **`CyclicCASim`** — cyclic (Griffeath) cellular automaton, an excitable medium of smooth
  spiral waves. A cell advances to the next state when `threshold` neighbours already hold
  it. Publishes to `Excitability`.
- **`LookupCASim`** — multi-state lookup-table CA, a crisp standing lattice. The 5-cell
  von-Neumann neighbourhood indexes a seed-generated `nstates^5` table; Langton's `lambda`
  is the edge-of-chaos dial. Publishes to `Substrate`. Because the rule is a *buffer*, only
  `seed`/`lambda`/`nstates` are interpolatable and changing any regenerates the table —
  arc evolution is discrete regenerations, not a continuous morph.
  Pointing a `CyclicCASim.couplingSource` at a `LookupCASim` gates the waves on the lattice,
  so structure and flow become one coupled system rather than two stacked layers. The gate
  reads the partner's live `stateRead`, not its published (and eroding) biome channel, so once
  a burst goes idle the frozen lattice keeps gating while the deposit erodes on its own —
  accepted, not synchronized.
- **`TermiteSim`** — neuron-coupled pheromone-stigmergy swarm (ported from
  `PDE_Nefeli_Termites`). Sense-and-turn like Physarum, minus "eat". Builds persistent
  **permeability mounds** via a dedicated firing-gated `Biome.BuildPermeability` /
  `BuildPermeabilityKernel` (agent-authored ch7 topography, not the umwelt write path); each
  species is then confined to its `preferredPermeabilityMin/Max` band by the perception habitat
  gate (out-of-band → avoidance + floored speed penalty). `ResetTermites` melts the mounds.
  See [[adr/0010-permeability-agent-built-topography|ADR-0010]]. Runs at **131 agents
  (1:1 with neurons)** by default —
  each termite is its own neuron group, seeded with a coherent heading and a **per-group
  fixed turn-angle magnitude** (`turnAngleSpread`, via the `NeuronGroup()` helper) so each
  stream curves with its own character instead of a single global turn angle. Specs:
  [[superpowers/specs/2026-06-07-termite-sim-design]],
  [[superpowers/specs/2026-06-11-termite-biome-features-design]].

All three sims also carry a **dispersal speed response** (`SimulationBase`:
`dispersalSpeedMode` constant/multiplier, shared via
`computes/includes/dispersal_speed_response.hlsl`) that accelerates agents out of a
Dispersal pulse — constant mode snaps to a fixed flee speed so even slow agents react fast.

**Neuron firing is shared, not termite-private** (see [[adr/0006-osc-neuron-firing]]). All
three sims seed `agent i → neuron i % 131` at the same CSV positions, and read one shared
firing vector each step: `agent → neuron → firing[neuron] ≥ firingThreshold` →
`firingSpeedMul` (faster) + `firingDepositAmount` (brighter trail), via
`computes/includes/neuron_firing.hlsl`. The firing *values* come from a `float16` blob
(`StreamingAssets/biomes11/organoid_firing.f16` — the organoid spike series, 131 neurons ×
180000 frames, preprocessed by `tools/firing_csv_to_f16.py`); the playhead is **external** —
see §3.7. Because CSV row
*k* = blob neuron *k* = each sim's seed position, a firing neuron excites the agents
physically on it in every biome.

All support **multiple agent types** (up to 8), each with its own parameters and
HSV color, uploaded as a per-type structured buffer every step.

### 3.5 Biome ↔ sim coupling — `UmweltMapping`

Each sim references an `UmweltMapping` (ScriptableObject) defining its *Umwelt* —
how it perceives and affects the field:

- **reads** — `(channel, weight, effect)` entries; the biome builds a per-sim
  perception texture (`ReadFieldKernel`) as a weighted sample. Effects map to the
  perception channels: `Chemotaxis→R`, `SpeedPenalty→G`, `Avoidance→B`, and
  `SpeedBoost→A` (the last added for Dispersal — all three sims read a negative-weight
  Chemotaxis entry on `Dispersal` to flee a pulse, plus a positive `SpeedBoost` entry to
  accelerate out of it).
- **writes** — `(channel, amount)` deposits/consumptions at agent positions.
- **metabolicHeat / oxygenConsumption** — implicit writes to Temperature / Oxygen.
- **death** params (oxygen/permeability thresholds, corpse waste) — agent mortality;
  defined but not yet executed (deferred — see README Roadmap).

This is the seam that makes the biome a genuine shared substrate rather than a
backdrop: two sims sharing one field interact indirectly through the chemicals they
read and write.

### 3.6 Parameters — preset / clone / ranges

Agent parameters live in ScriptableObjects (`BoidParams`, `PhysarumParams`),
each a list of per-type structs plus a list of `ParamRange` (min/max for 0–1
control mapping).

- **`paramsSO`** — the saved preset asset, assigned in the inspector, never mutated.
- **`agentParams`** — a runtime `Instantiate` clone created on `Reset()`; this is
  what all live control mutates. `GPUStep()` re-uploads it every step, so mutating
  the clone is immediately reflected on the GPU with no extra plumbing.
- **`IParamSet`** — interface giving by-name *raw* access (`GetValue`/`SetValue`/
  `GetRange`/`TypeCount`) to any params object, live clone or on-disk asset.
  Exposed on the sim via `SimulationBase.LiveParamSet`. Note the Boid name↔field
  quirk: control name `separateRange` maps to field `separationRange`,
  `foodSeek` → `foodSeekingStrength`.

Two parameter surfaces coexist deliberately: the sims' `Get/SetParameter` take
**normalized 0–1** values mapped through ranges (for MIDI/OSC); `IParamSet` works in
**raw** values (for interpolation). Not redundant — different semantics.

### 3.7 Control surfaces (`network/`, `utils/`)

- **`MidiFighterTwister` / `MIDIMapping`** — MFT hardware → normalized
  `SetParameter`/`SetParameterDelta`. `SaveParams` action writes timestamped `.asset`
  snapshots (the memory daemon's input).
- **`OSCMapping`** — OSC control of the same parameter API (TD / external drivers), plus
  `/index <int>` → `NeuronFiringSource.SetFrame` (the firing playhead). Reset commands
  (`/sim_reset`, `/sim_resetSimsOnly`) are queued and drained on the main thread in `Update()`
  — OscJack invokes callbacks on its socket thread, and reset touches GPU/GameObject APIs that
  must run on the main thread ([[adr/0008-clear-in-place-reset]]). Param/injector/`/index`
  callbacks stay inline (CPU-only).
- **`NeuronFiringSource`** — the **firing playhead** ([[adr/0006-osc-neuron-firing]]).
  Owns the firing blob + neuron positions; an external patch sends `/index <int>` to scrub
  which frame is shown (file = values, OSC = playhead — no auto-advance). Holds the last
  frame and **decays firing to quiet** (`firingDecaySeconds`) when silent. Each step it
  emits a shared 131-float buffer (row × decay) that `SimulationManager` broadcasts to every
  sim. Thread-safe intake (field + dirty flag, the `BiomeInjector` pattern).
- **Firing-ring overlay** — `NeuronRingKernel` (`SimulationManager.compute`) draws one
  count-independent ring per firing neuron on top of the composite. Needed because the
  composite is a pure additive sum → physarum's dense firing saturates the canvas and hides
  termite/boid firing; the rings key off firing intensity directly, not agent counts. The
  ring is an **expanding shockwave** — radius grows as the firing intensity decays
  (`ringExpandGain`) with a bright onset core flash (`ringCoreStrength`).
- **`BiomeInjector`** — paints **Gaussian stamps** into biome channels (thread-safe intake;
  per-source raw→0..1 calibration + EMA smoothing; Additive/MaxToward/SetToward modes). Two
  stamp producers: external **sources** (sensors/OSC → any channel), and **firing-driven
  Dispersal** pulses (intensity-scaled, radius-expanding) at either fixed **neuron CSV
  positions** or **live agent positions** of a chosen sim (`FiringDispersalSource`, e.g. the
  termites — `i % neuronCount` selects the firing neuron). Runs after sim write-back, before
  `Biome.Step()`, so stamps ride the full field evolution. Spec:
  [[superpowers/specs/2026-06-11-termite-biome-features-design]].
- **`ParameterRecorder`** — records per-step parameter *changes* as a JSON event
  track; replays them deterministically against `SimStepCount`.
- **`ParameterInterpolator`** — eases live params from current state through an
  ordered queue of preset `.asset` waypoints, sim-step driven, per-param-name enable
  toggles, shortest-arc hue, global duration/hold/easing, stop-and-hold at end. For
  long-running installations. Spec: [[superpowers/specs/2026-06-07-parameter-interpolator-design]].

### 3.8 External texture I/O & GPU resources

Inter-app video over **Syphon / NDI / Spout** (Unity ↔ TouchDesigner). All three Klak
packages compile on every platform; availability is gated at runtime
(`ExternalTextureShare.IsAvailable`: NDI everywhere, Syphon macOS, Spout Windows).

- **`ExternalTextureShare`** — backend isolating *all* Klak (NDI/Spout/Syphon) API
  behind `ITextureSenderBackend`/`ITextureReceiverBackend`, plus `IsAvailable` and
  `EnumerateSources` (discovery). The only file touching `Klak.*`.
- **`ExternalTextureReceiver`** — receives one external texture (Syphon/NDI/Spout, or a
  debug video clip) into an `OutputTexture` fed to sims as external influence. Replaces
  `ExternalInputProvider`. `selfDrive` + a custom inspector preview/source-picker let
  you verify reception standalone. Note: receive needs the source's *exact* canonical
  name (NDI `"<MACHINE> (Name)"`, Syphon `"App/Name"`) — hence the discovery dropdown.
- **`ExternalTextureSender`** — sends selected textures (composite, per-sim outputs,
  biome channel layers) out; per-stream protocol + resolution scale, default
  `EoC/<name>` stream names. Biome layers extracted via `Biome.RenderChannelTo` only
  while enabled. `SetSource` is idempotent (set-on-change) — required because
  `SyphonServer`'s source-setter tears down its publish coroutine. Clear-in-place reset
  keeps the source texture instance stable, so this set-on-change never re-fires on a
  normal reset ([[adr/0008-clear-in-place-reset]]). Spec:
  [[superpowers/specs/2026-06-07-external-texture-share-design]].
- **`GPUResourceManager`** — owns ComputeBuffer/RenderTexture lifetimes; everything
  allocates through it and `ReleaseAll()` cleans up. Each `SimulationManager`, `Biome`,
  and `SimulationBase` holds its own instance. Instances **persist across resets**
  (clear-in-place — [[adr/0008-clear-in-place-reset]]); `ReleaseAll()` runs only on a
  resolution/structural realloc, disable, or destroy.

### 3.9 Temporal Composer — show sequencer (`11.2 SIGGRAPH Scene`)

A Unity Timeline-driven show sequencer choreographs the SIGGRAPH show — sim
visibility, param snapshots, resets, network routing, 2–4 live "biome cells," and
scattered diffusion-return patches — all composited into a dedicated `composerOutTex`,
separate from the sim `compositeOutTex`:

```
SimulationManager ──compositeOutTex──┐
BiomeCellRig ×N ──cell outTex────────┤
ExternalTextureReceiver #2 ──────────┤   (StreamDiffusion return, "TD_Diffusion")
                                      ▼
PlayableDirector(ShowSequence) → CompositeSequencer → composerOutTex → ExternalTextureSender / ScreenLayout
        ▲                                                   │
  track mixers push per-frame state              sent out → TD StreamDiffusion → back in (loop)
```

- **`CompositeSequencer`** (`components/sequencer/`) owns `composerOutTex` — `ARGBHalf`,
  resolution independent of sim rez (default = sim composite rez × `composerResScale`),
  allocated once and cleared in place, reallocated only on a resolution change — the
  same stable-RT rule as [[adr/0008-clear-in-place-reset|ADR-0008]], so
  `ExternalTextureSender`'s `SendSource.ComposerOutput` (default stream `EoC/Composer`)
  and `ScreenLayout` keep a stable native handle for the whole show. `LateUpdate` (after
  `SimulationManager.Render()`) dispatches `SequencerComposite.compute`: a base pass
  always runs, copying the sim composite weighted by `_baseWeight` (`SetBaseWeight`,
  clamped to [0,1], reset to 1 each frame) — a `Replace`-mode cell with `duckBase` sets
  it to `1 - w` for that frame, so full cell coverage (`w = 1`) dims the base pass
  toward 0 rather than skipping the dispatch — then one `RectBlendKernel` dispatch per
  active cell/patch rect (≤4 cells, ≤128 active patch draws/frame), then an optional
  debug-outline pass (`debugOutlines`, off for the show).
- **`BiomeCellRig`** (prefab, ≤4 in scene) — a trimmed, self-paced `SimulationManager` +
  `Biome` + sims at reduced rez (default 1024²) with `ownsGlobalTiming = false` /
  `stepsPerTick = 0` so a `BiomeCellTrack` clip alone drives its `Running` flag via
  `BiomeCellMixer`; own preset assets, own tick rate (`cellRate`, 1–60 Hz) independent of
  the main sim. Its `CompositeOutputTexture` is a cell source alongside `MainComposite`
  and `DiffusionReturn` (`CompositeSequencer.ResolveSource`).
- **Tracks** (`components/sequencer/tracks/`; each a Timeline `TrackAsset` +
  `PlayableBehaviour` + `PlayableMixer` bound to `CompositeSequencer` or
  `SimulationManager`): `BiomeCellTrack` (source + `dstRect` + `Overlay`/`Replace` mode,
  weight from clip ease curves), `PatchScatterTrack` (Anadol-style scattered patches,
  `sourceA`/`sourceB` crossfade), `ParamSnapshotTrack` (eased live-param morph to a
  snapshot asset via `IParamSet`/`ParameterInterpolator.LerpHue01`), `RoutingTrack`
  (sets `SimulationManager.influenceOverride` for the clip's duration). Plus reset
  `SignalEmitter`s → `SignalReceiver` → `ResetSimsOnly`/`ResetPhysarum`/`ResetBoids`/
  `ResetTermites`. `BiomeCellMixer` and `RoutingMixer` both clear their external
  stateful resource (`rig.Running`, `influenceOverride`) from `OnPlayableDestroy`/
  `OnBehaviourPause`, not just per-frame `w<=0`, so a director Stop mid-clip can't leave
  a rig running or a routing override stuck on.
- **Patch grammar + determinism** (`Biomes.Sequencer.Core`, `sequencer_core/`) —
  `PatchEventScheduler` is a pure-logic, engine-free assembly (no Unity playmode
  dependency), unit-tested by `Biomes.Sequencer.Tests` (`Assets/Tests/EditMode/`):
  rejection-sampled non-overlapping `PatchEvent[]` generated deterministically from
  `(clip params, seed)`, size→hold inversion (large patches flash, small linger),
  asymmetric lead/trail stagger + jitter, sweep-line `PatchSweep.Collect` for O(active)
  per-frame activation. Same seed always reproduces the same schedule, and `PatchSweep`
  is rewind-safe, so scrubbing the Timeline reproduces identical patch layouts. Test
  coverage: determinism, non-overlap invariant, size→hold mapping, sigmoid crossfade
  distribution.
- **StreamDiffusion loop** — `composerOutTex` sends out via Spout/Syphon
  (`ExternalTextureSender`, `SendSource.ComposerOutput`) → TouchDesigner runs
  StreamDiffusion (same show machine, RTX 5080) → returns via Spout into a second
  `ExternalTextureReceiver` (`streamName = "TD_Diffusion"`) bound to
  `CompositeSequencer.diffusionReturn`, feeding `PatchScatterTrack`'s `DiffusionReturn`
  source and, via `RoutingTrack`, sim external influence. Patch hold times (0.2–1.5s)
  make diffusion fps nearly irrelevant — a 5–10fps return reads identically to 30fps.
  Spout both directions locally (same-GPU zero-copy); Syphon covers mac dev without the
  diffusion leg; NDI only for cross-machine sources.
- **Deviations from the design spec** (agreed rationale, see the plan's Global
  Constraints): one `RectBlendKernel` dispatched per rect replaces the spec's separate
  `CellKernel`/`PatchKernel` pair with a 512-wide `StructuredBuffer` loop — same
  underlying math (sample a source sub-rect, blend into a dest rect) and strictly
  cheaper, since each dispatch covers only its own rect's pixels and no buffer is
  needed. Rate caps live where they already existed
  (`SimulationManager.targetFPS`/`simRate`, `BiomeCellRig.cellRate`) rather than being
  duplicated onto `CompositeSequencer`, which exposes only `composerResScale` — one
  source of truth for frame-rate/tick-rate caps.
- **Biome Palette** (`Editor/sequencer/BiomePaletteWindow.cs`, menu
  `Biomes → Biome Palette`) — grid of `IParamSet` snapshot/preset assets with cached PNG
  thumbnails (`SnapshotThumbnailCache`, captured from the live composer output on
  demand, stored next to the asset); drag onto the Timeline or Insert-at-playhead to
  create a `ParamSnapshotClip`. Follows the `ScreenLayoutPreview` editor idiom.

Design: [[superpowers/specs/2026-07-19-temporal-composer-design]]; manual scene-wiring
and perf-gate checklist: [[superpowers/2026-07-19-temporal-composer-manual-setup]].

---

## 4. Memory system

Summarized here; authoritative detail in [[migration]] §2.

- **Folder-as-event-log** ([[adr/0002-folder-as-event-log]]): Unity's `SaveParams`
  writes `.asset` snapshots; the daemon watches the folder and indexes them. Folder
  is source of truth, DB is a derived index — nuke the DB, replay the folder.
- **Local-first storage** ([[adr/0003-local-first-storage]]): SQLite (events/tags) +
  LanceDB (embeddings) + blob disk per node; scheduled sync to a canonical archive.
- **v0 shipped**: folder-watch, SHA256 → SQLite (idempotent), OSC `:9100`
  (`/memory/ping`, `/memory/count`). **v1 pending**: parse `.asset` YAML into a
  param blob, similarity query, symbolic tags. **v2**: organoid spikes, plant
  biopotential, viewer-derived signals, feedback policies.

---

## 5. Key design decisions (ADRs)

- [[adr/0001-rsync-over-filter-repo]] — repo split via plain rsync (no history).
- [[adr/0002-folder-as-event-log]] — snapshot folder is source of truth.
- [[adr/0003-local-first-storage]] — SQLite + LanceDB per node.
- [[adr/0004-td-as-orchestration-hub]] — TouchDesigner orchestrates; daemon is a
  separate OSC-speaking process.
- [[adr/0005-includes-copied-verbatim]] — `Includes/` copied, not vendored.
- [[adr/0008-clear-in-place-reset]] — reset clears GPU resources in place (stable
  `outTex` → no Syphon teardown).
- [[adr/0009-per-show-scene-workspaces]] — one workspace folder per show; shared engine
  stays in `11.0 Biomes/`.

---

## 6. Conventions

- **Params:** edit presets via the `Biomes/*Params` Create-Asset menu; assign to a
  sim's `paramsSO`. Live tweaks land on the runtime clone, not the asset.
- **`.meta` files:** Unity tracks assets by GUID in `.cs.meta` / `.asset.meta`, not by
  path. Always move/commit a file *with* its `.meta`, or scene/prefab references break.
  Generated ` 2.meta` Finder duplicates are gitignored (see [[migration]] §1).
- **Docs:** ADRs in `docs/adr/`, session logs in `docs/sessions/`, specs/plans under
  `docs/superpowers/`. All carry Obsidian frontmatter (`status`, `date`, `tags`,
  `related`) and link via `[[wikilinks]]`. `docs/INDEX.md` is the entry point.

---

## 7. Maintaining this file

Update when: the per-step pipeline changes, biome channels are added/removed, a new
sim type or control surface lands, the params/`IParamSet` model changes, or a new ADR
is accepted. Keep it an overview — push deep rationale into ADRs and `migration.md`,
and link rather than duplicate.
