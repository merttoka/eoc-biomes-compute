---
status: living
date: 2026-06-07
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
  11.0 Biomes/          active biome runtime (this doc's focus)
  Includes/             shared compute helpers / shaders (copied verbatim — ADR-0005)
Packages/               UPM deps (klak.spout)
memory/
  daemon/               Python: folder-watch → SQLite + OSC
  td/                   TouchDesigner files (placeholder)
  docs/                 OSC contract, schema notes
docs/                   this file, migration.md (living plan), adr/, sessions/, specs/, plans/
```

### `11.0 Biomes/src/` structure

```
src/
  components/
    core/     SimulationManager, SimulationBase, Biome, BiomeFieldConfig,
              UmweltMapping, ExternalInputProvider, GPUResourceManager
    Sim/      BoidSim, PhysarumSim (concrete SimulationBase subclasses)
    network/  MidiFighterTwister, MIDIMapping, OSCMapping (control surfaces)
    utils/    ParameterRecorder, ParameterInterpolator, ScreenLayout
  params/     BoidParams, PhysarumParams, ParamRange, ColorPalette, IParamSet
  Editor/     custom inspectors (ParamsEditor, MFT/MIDI editors, ScreenLayoutPreview)
```

---

## 3. Unity biome runtime

### 3.1 Orchestration — `SimulationManager`

A single `SimulationManager` owns resolution, timing, the `Biome`, a list of
`SimulationBase` sims, the `ExternalInputProvider`, and the composite output. It is
the only driver: `Reset()` (re)initializes everything; `Update()` calls `Step()`
`stepsPerFrame` times every `stepMod` frames. `SimStepCount` is the canonical sim
clock (monotonic, increments per `Step()`), used by time-based tooling.

### 3.2 The per-step pipeline

`SimulationManager.Step()` runs a fixed sequence each simulation step:

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

The biome is a double-buffered `Texture2DArray` of **9 scalar channels**
(`BiomeChannel`): `Nutrient, Pheromone0, Pheromone1, Oxygen, Temperature, Waste,
Permeability, FlowX, FlowY`. Per-channel behavior (diffuse rate, decay, advected-by-
flow, initial value) comes from `BiomeFieldConfig` and is uploaded as a structured
buffer. `Biome.Step()` runs the field dynamics on the GPU: temperature gradients
generate flow, flow advects fields, cross-field interactions (waste→nutrient,
temp→permeability) react, then diffuse + decay. Resolution is independent of sim
resolution; sim↔field coordinates are mapped by ratio.

### 3.4 Simulations — `SimulationBase` → `BoidSim` / `PhysarumSim`

`SimulationBase` is the abstract GPU-agent template: trail texture arrays (per-type
+ total), a perception texture, an external-influence texture, and the common kernel
set (reset/move/write-trails/diffuse/render). Subclasses implement the agent model:

- **`PhysarumSim`** — slime-mold agents (sense-angle/distance, turn, deposit, eat).
  Can seed agents from a neuron-positions CSV (the "neuron firing" visuals).
- **`BoidSim`** — flocking agents with a GPU spatial hash (separate/align/attract
  ranges, food-seeking).

Both support **multiple agent types** (up to 8), each with its own parameters and
HSV color, uploaded as a per-type structured buffer every step.

### 3.5 Biome ↔ sim coupling — `UmweltMapping`

Each sim references an `UmweltMapping` (ScriptableObject) defining its *Umwelt* —
how it perceives and affects the field:

- **reads** — `(channel, weight, effect)` entries; the biome builds a per-sim
  perception texture as a weighted sample (chemotaxis / speed / avoidance).
- **writes** — `(channel, amount)` deposits/consumptions at agent positions.
- **metabolicHeat / oxygenConsumption** — implicit writes to Temperature / Oxygen.
- **death** params (oxygen/permeability thresholds, corpse waste) — planned agent
  mortality.

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
- **`OSCMapping`** — OSC control of the same parameter API (TD / external drivers).
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
  `SyphonServer`'s source-setter tears down its publish coroutine. Spec:
  [[superpowers/specs/2026-06-07-external-texture-share-design]].
- **`GPUResourceManager`** — owns ComputeBuffer/RenderTexture lifetimes; everything
  allocates through it and `ReleaseAll()` cleans up. Each `Biome` and `SimulationBase`
  holds its own instance, released on `Reset`/disable/destroy.

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
