# Primordial Particle System Sim — Design

**Date:** 2026-10-20
**Status:** Implemented (branch `feat/primordial-sim`)
**Branch:** `feat/primordial-sim`
**Files touched:** new `11.0 Biomes/src/components/Sim/PrimordialSim.cs`, new `params/PrimordialParams.cs`,
new `computes/PrimordialSim.compute` + `computes/includes/primordial_type_params.hlsl`, new
`core_math/PrimordialMotion.cs`, `components/core/SimulationBase.cs` (`TrailLayerCount`, `OscPrefix`),
`components/core/SimulationManager.cs`, `components/utils/SimTimeline.cs`, `core_math/PaletteAssign.cs`,
`components/network/{MidiFighterTwister,OSCMapping}.cs`, `Editor/{ParamsEditor,MidiFighterTwisterEditor}.cs`,
new `Editor/PrimordialSceneSetup.cs`, new `Assets/Tests/EditMode/Primordial{Motion,Kernel}Tests.cs`,
`AgentRenderKernelTests.cs`, new `Assets/Workspace/11.4 PPS Scene/**`, `MIDI_OSC.md`, docs.

## Goal

A fourth agent sim: Schmickl et al.'s **primordial particle system** (PPS, *Sci. Rep.* 6:37969,
2016). Each particle moves at constant speed and turns by

    Δφ = α + β · N · sign(R − L)

where `L`/`R` count the other particles in the left/right half-disc of radius `r`, and `N = L + R`.
At ⟨r = 5, α = 180°, β = 17°, v = 0.67⟩ and ~0.08 particles per space unit², a random soup
self-organizes into spores that eat free "nutrient" particles, grow into cells with a membrane
and a nucleus, divide, decay, and settle into a density-regulated population. These are creatures
in the a-life sense, which none of the existing sims give: Physarum builds networks, Boids flock,
Termites build walls.

The point of putting PPS **into the biome** is the coupling. PPS creatures feed the field that
Physarum hunts on, and Physarum veins push back on the creatures. Food webs, chases and
blooms then come out of two simple laws sharing one chemistry.

The lab sketch (`website/lab/src/experiments/online-petri-dish/pps/agents.wgsl`) is a crude
O(n²) WebGPU version. It moves along the pre-turn heading, races on its in-place buffer, and
works in raw pixels. This design follows the paper instead.

## Model (paper conventions)

- **Order per step** (paper Fig. 1): sense with the current heading → `φ += Δφ` → move
  `p += v · (cos φ, sin φ)` along the **new** heading. Torus world.
- **Handedness:** y-up math frame. A neighbour at offset `d` is on the right iff
  `cross(h, d) = h.x·d.y − h.y·d.x < 0`; positive `Δφ` rotates counter-clockwise (a left turn).
  Mirroring the convention maps ⟨α, β⟩ → ⟨−α, β⟩, harmless at α = 180° but not elsewhere in
  the paper's phase diagram, so it is pinned by a test.
- **Neighbourhood:** `0 < |d| < r` (self and exactly coincident particles excluded), minimum-image
  distance across the seam.
- **Update:** synchronous (every particle senses positions from the start of the step,
  double-buffered). The paper updates asynchronously. GPU ports with synchronous updates are
  known to keep the life cycle. Gate 1 (below) is where that claim gets checked.
- **Noise:** optional Gaussian `σ` on `Δφ` (the paper's robustness axis, Fig. 6D). This is also
  the lever that lets the biome shatter cells (see coupling).

## Units: space units, not pixels

Every PPS distance (`radius`, `speed`, `fieldDrift`) is in the paper's **space units (su)**.
The sim maps them to pixels with one per-sim knob, `spaceUnitPx`: pixels per su at the
reference height (2160). It is scaled by `rezY / referenceHeight` like every other spatial
param, so `px/su = spaceUnitPx · ResolutionScale`.

- The paper's parameters transfer verbatim. ⟨5, 180°, 17°, 0.67⟩ is the default type.
- The world size in su is resolution-independent: `(aspect · H, H)` with
  `H = referenceHeight / spaceUnitPx`. At the default `spaceUnitPx = 6` that is 640 × 360 su at
  16:9. Cells come out about 15–30 su ≈ 90–180 px at 4K.
- **Agent count is derived** from `density` (particles/su², default 0.08):
  `count = round(density · worldW_su · worldH_su)`, which is ≈ 18.4 k at the defaults. The count
  is computed and **cached in `Reset()`**. `GetAgentCount()` returns the cache, because the
  manager dispatches biome write-back with it every step, and a live density edit must not index
  past the buffers. A density or `spaceUnitPx` edit therefore takes effect on the next reset,
  exactly like `agentsCount` on the other sims.
- `PrimordialParams.ScaleSpatial` is a no-op (the unit carries the scale). `ScaleDensity` scales
  `depositAmount` (×k), like the other sims.

## GPU design (`PrimordialSim.compute`)

Agent record = the shared 20-byte `Agent` (`float2 position`, `float2 direction` = unit
heading, `uint typeId`). Biome write-back, the injector's live-agent dispersal mode and
`AsyncGPUReadback` consumers work unchanged. A parallel `float` buffer, `agentDensity`, carries
each particle's last `N` from Move to WriteTrails (render-only).

Kernels: `ResetTextureKernel`, `ResetAgentsKernel`, the Boid spatial-hash set
(`ClearCellCounts` → `HashAndCount` → single-group `PrefixSum` → `Scatter` → `ReorderAgents`),
`MoveAgentsKernel` (cell-sorted threads, result to the agent's own slot), `WriteTrailsKernel`,
`DiffuseTextureKernel`, `RenderKernel`.

**Hash sizing.** Cells are `max(maxRadiusPx, minCellPx)` wide (`minCellPx = rezY / 160`) and
tile the torus exactly (`gw = floor(rezX / cell)`, `cellSize = rez / gw`). The buffers are
allocated for the `minCellPx` grid, so a live radius edit can only coarsen the grid and never
overflows it. This differs from Boid's 32 px floor, which a 5 su radius at small `spaceUnitPx`
would undershoot.

**Per step:**

    Diffuse(trailRead → trailWrite)                 once
    repeat substeps:
        hash(agents) → Move(read → write) → WriteTrails(+deposit / substeps → trailWrite) → swap agents
    (base) swap trails → Render

`substeps` (1–8, default 2) is a per-sim knob. Paper cells divide in ~10³ steps, about 17 s at
60 Hz, so a few PPS iterations per sim step make the life cycle legible without speeding up
every other sim the way `stepsPerTick` does. PPS is cheap: ~18 k particles × a few dozen
neighbours. The extra substeps cost far less than one physarum step.

## Biome coupling

Same four perception slots as the other sims, read through the sim's `UmweltMapping`. What
each one does to a PPS particle:

| perception | effect on PPS | rationale |
|---|---|---|
| **G** speed multiplier | `v ·= G` | engine contract (habitat walls, keep-outs). Constant `v` is load-bearing for the life cycle, so the shipped PPS umwelt has **no SpeedPenalty reads** |
| **A** speed boost | dispersal speed response (shared include) **and** `σ += agitationNoise · A` | the characterized way to shatter cells (paper Fig. 6D): firing pulses, or a predator's scent mapped to SpeedBoost, dissolve cells back into nutrients |
| **R − B** chemotaxis − avoidance | **positional drift** `p += fieldDrift · ∇S` (central differences at ±r, clamped to 2v) | a near-uniform drift translates a whole cell without breaking it, because neighbour relations are Galilean-invariant. Steering the heading instead would fight α = 180°'s per-step flip in ways nobody has characterized |

Writes go through the generic umwelt write-back (one deposit per particle). Dense creatures
therefore dominate the field and the sparse nutrient soup leaves a faint uniform floor.

**Channel choice (v1): no new channel.** PPS writes **Nutrient** (ch0). Its free particles *are*
the paper's "nutrients", and every Physarum umwelt already reads Nutrient as food (+1.4
chemotaxis) and eats it. Physarum's scent is Pheromone0 (ch1), which PPS reads. A dedicated
16th channel (`BiomeChannel.Count/Names`, `CH_COUNT`, every `CopyChannelsExcept` mask, every
`BiomeFieldConfig` asset) is backlog for a scene that also needs Nutrient for something else.

**The ecosystem loop (11.4 scene):**

    PPS cells ──Nutrient──► Physarum chemotaxis: veins grow between creatures, and eat the Nutrient
    Physarum ──Pheromone0──► PPS: SpeedBoost (agitation: cells dissolve where veins are thick)
                            + Chemotaxis (drift: away = prey flees, toward = symbionts cluster)
    Neuron firing ──Dispersal pulses──► everyone flees; PPS cells shatter and re-nucleate

The result is a predator/prey cycle with no lifecycle code. Cells far from veins grow and divide
and attract veins. Veins agitate them back into free nutrients, which the veins consume or which
re-nucleate as spores elsewhere.

**Neuron coupling** follows the shared contract: agent `i` → neuron `i % 131`; firing →
`firingSpeedMul`, `firingDepositAmount`.

## Spawn

`SpawnMode.NeuronPositions` (default): each particle independently, with probability
`neuronSeedFraction` (default 0.3, hashed by id so every type gets its share), is born within a
Gaussian `seedRadius` (su) of neuron `i % 131`. At the defaults that puts ≈ 40 particles at each
neuron, about one *triangle cell*, so every neuron is the birthplace of a creature. The rest
scatter uniformly as the nutrient soup. A firing neuron excites exactly the creature it seeded.
`Scatter` = the paper's initial condition. `BottomEdge`/`TopEdge` = the shared band spawns.
Keep-out eviction matches the other sims.

## Render: membrane vs nucleus

The paper colours particles by their neighbourhood class (green nutrients, brown spores, blue
membranes, yellow nuclei). Discrete classes do not fit the HSB palette system, so the sim renders
a **continuous density ramp** instead:

- The trail array grows a second bank of per-type layers: `[0, T)` trail, `T` total,
  `[T+1, 2T]` **density-weighted trail** (`deposit · saturate(N / densityFull)`). Both banks
  diffuse identically, so `dens = weighted / trail` at a pixel is the deposit-weighted mean
  neighbourhood density of the light there. It survives blur and fade unchanged.
- Colour per type: `hsb2rgb(hue + densityHueShift·dens, saturation·(1 − 0.6·densityGlow·dens),
  brightness·trail·(1 + densityGlow·dens))`. With shift = glow = 0 this is exactly the other
  sims' formula, pinned by the shared render test. A shifted hue for dense matter reads as
  membrane vs nucleus, and the palette keeps owning the base colour.
- `SimulationBase` gains `protected virtual int TrailLayerCount => TypeCount + 1`, so the base
  `Allocate()` (and its `MarkAllocated()`) stays the only allocation path. No `Allocate`
  override, no second buffer pair to swap in `FadeStep`.

## Params (`PrimordialParams`, per type)

| param | default | range | unit |
|---|---|---|---|
| alpha | 180 | −180…180 | deg |
| beta | 17 | −90…90 | deg |
| radius | 5 | 1…20 | su |
| speed | 0.67 | 0.05…3 | su/step |
| noise | 0 | 0…45 | deg σ |
| otherTypeWeight | 1 | −1…1 | weight of other types in `L`/`R` (0 = species pass through each other, <0 = inverted turning) |
| fieldDrift | 0 | −20…20 | su/step per unit field difference |
| agitationNoise | 0 | 0…90 | deg σ per unit perception A |
| depositAmount, diffuseRate | 0.25, 0.9 | 0…1, 0.8…1 | |
| densityFull, densityHueShift, densityGlow | 35, 0.25, 0.6 | 5…80, −0.5…0.5, 0…2 | |
| hue, saturation, brightness | | 0…1 | |
| firingSpeedMul, firingDepositAmount | 1.5, 1 | 1…5, 0…1 | |

Per-sim (component, structural, take effect on reset): `spaceUnitPx`, `density`, `substeps`,
`neuronSeedFraction`, `seedRadius`.

## Integration

- `AgentFamily.Primordial` and `TimelineAction.StartPrimordial/StopPrimordial` are **appended**
  (both are serialized as ints).
- `SimulationManager`: Start/Stop/Reset Primordial buttons. OSC `/sim_resetPrimordial`.
- OSC param prefix: `SimulationBase.OscPrefix` (virtual, default first letter). Primordial
  overrides it with `pps` because `p` is Physarum's: `/pps_alpha_0`.
- MFT: family LED colour (green) and type count through `PresetParamSet`/`LiveParamSet`.
  Params inspector: `PrimordialParamsEditor`.
- Palettes: swatches can be tagged `Primordial`, and untagged swatches apply as usual.

## Scene: `Assets/Workspace/11.4 PPS Scene`

`Scene_PPS.unity` is built by `Editor/PrimordialSceneSetup.cs` (menu `Biomes/Setup 11.4 PPS
Scene`, the `TemporalComposerSceneSetup` idiom), not hand-written YAML. It is a copy of the 11.3
rig with Boids and Termites removed, plus Physarum and Primordial, and its own `assets/`
(params, umwelts, field config) and `materials/`.

## Testing

- `PrimordialMotion` (`Biomes.Core`, pure C#): the turn law, side test and a CPU reference step.
  EditMode tests pin the paper's conventions (handedness, turn-then-move, α = 180° isolated
  particles holding position within two steps).
- `PrimordialKernelTests`: dispatch hash + Move on a hand-made configuration and compare
  against the CPU reference (positions/headings within float tolerance), plus the
  upload-struct mirror.
- `AgentRenderKernelTests` gains Primordial (shift = glow = 0 ⇒ the shared colour formula).
- **Gate 1 (visual, before any coupling work):** paper defaults, `Scatter`, 0.08 p/su², no
  biome reads. Spores and then cells must appear within a few thousand steps (checked in Play
  through `ExportPNG` frames).

## Non-goals / backlog

Asynchronous update order, the paper's magenta close-range class (`N_r=1.3 > 15`), a dedicated
PPS biome channel, per-type population fractions, and agent death (the umwelt lifecycle params
stay deferred engine-wide).

## Unresolved questions

- Folder name: built as `11.4 PPS Scene` (matches `11.x <Name> Scene`); the brief said `11.4 PPS`.
- Channel: PPS → Nutrient for v1; promote to a dedicated channel only if a scene needs both.
- `spaceUnitPx = 6` (~18 k particles, cells ~90–180 px at 4K) — pick per show.
