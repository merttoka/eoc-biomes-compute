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
  `cross(h, d) = h.x·d.y − h.y·d.x < 0`, and positive `Δφ` is a **turn to the right** (clockwise),
  as the paper states. The two must agree. Flipping only one of them runs ⟨α, −β⟩, a different
  PPS: the first gate-1 run did exactly that, and the soup congealed into blobs instead of
  growing cells. Flipping both is a mirror image, ⟨−α, β⟩. Both are pinned by tests.
- **Neighbourhood:** `0 < |d| < r` (self and exactly coincident particles excluded), minimum-image
  distance across the seam.
- **Update:** synchronous (every particle senses positions from the start of the step,
  double-buffered). The paper updates asynchronously. GPU ports with synchronous updates are
  known to keep the life cycle. Gate 1 (below) is where that claim gets checked.
- **Noise:** optional Gaussian `σ` on `Δφ` (the paper's robustness axis, Fig. 6D). This is also
  the lever that lets the biome shatter cells (see coupling).

## Units: space units, not pixels

Every PPS distance (`radius`, `speed`, `size`, `fieldDrift`) is in the paper's **space units (su)**.
The sim maps them to pixels with one per-sim knob, `spaceUnitPx`: pixels per su at the
reference height (2160). It is scaled by `rezY / referenceHeight` like every other spatial
param, so `px/su = spaceUnitPx · ResolutionScale`.

- The paper's parameters transfer verbatim. ⟨5, 180°, 17°, 0.67⟩ is the default type.
- The world size in su is resolution-independent: `(aspect · H, H)` with
  `H = referenceHeight / spaceUnitPx`. At the default `spaceUnitPx = 8` that is 480 × 270 su at
  16:9. Cells come out about 10–30 su ≈ 80–240 px at 4K. (6 was the first draft: 18 k particles,
  but the creatures read as grain on the 1080p preview.)
- **Agent count is derived** from `density` (particles/su², default 0.08):
  `count = round(density · worldW_su · worldH_su)`, which is ≈ 10.4 k at the defaults. The count
  is computed and **cached in `Reset()`**. `GetAgentCount()` returns the cache, because the
  manager dispatches biome write-back with it every step, and a live density edit must not index
  past the buffers. A density or `spaceUnitPx` edit therefore takes effect on the next reset,
  exactly like `agentsCount` on the other sims.
- The unit is cached with the count: a live `spaceUnitPx` edit would otherwise rescale every
  distance against an unchanged population and move the density off the life-cycle band.
- `PrimordialParams.ScaleSpatial` is a no-op (the unit carries the scale). So is `ScaleDensity`:
  each particle deposits a disc `size` su wide, which already keeps the per-pixel deposit
  resolution-independent. The other sims deposit a single pixel and scale ×k.

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

`substeps` (1–8, default 3) is a per-sim knob. Paper cells divide in ~10³ steps, about 17 s at
60 Hz, so a few PPS iterations per sim step make the life cycle legible without speeding up
every other sim the way `stepsPerTick` does. Measured at 1080p on an M4 Max (op run 200× from one
state, one dependent readback): 0.40 ms/step at 1 substep, 0.66 ms at 3, against 0.55 ms for the
11.4 Physarum (250 k agents). Each iteration costs ~0.13 ms; diffuse + render are the fixed ~0.27 ms.

## Biome coupling

Same four perception slots as the other sims, read through the sim's `UmweltMapping`. What
each one does to a PPS particle:

| perception | effect on PPS | rationale |
|---|---|---|
| **G** speed multiplier | `v ·= G` | engine contract (habitat walls, keep-outs). Constant `v` is load-bearing for the life cycle, so the shipped PPS umwelt has **no SpeedPenalty reads** |
| **A** speed boost | dispersal speed response (shared include) **and** `σ += agitationNoise · A` | the characterized way to shatter cells (paper Fig. 6D): firing pulses, or a predator's scent mapped to SpeedBoost, dissolve cells back into nutrients |
| **R − B** chemotaxis − avoidance | **positional drift** `p += fieldDrift · ∇S` (central differences at ±r, clamped to 0.5·v) | a near-uniform drift translates a whole cell without breaking it, because neighbour relations are Galilean-invariant. Steering the heading instead would fight α = 180°'s per-step flip in ways nobody has characterized. **Transient fields only:** drift up a standing scent converges everything onto its maxima. At a 2v clamp the 11.4 soup collapsed into a few white points within a minute. |

Writes go through the generic umwelt write-back (one deposit per particle). Dense creatures
therefore dominate the field and the sparse nutrient soup leaves a faint uniform floor.

**Channel choice (v1): no new channel.** PPS writes **Nutrient** (ch0). Its free particles *are*
the paper's "nutrients", and every Physarum umwelt already reads Nutrient as food (+1.4
chemotaxis) and eats it. Physarum's scent is Pheromone0 (ch1), which PPS reads. A dedicated
16th channel (`BiomeChannel.Count/Names`, `CH_COUNT`, every `CopyChannelsExcept` mask, every
`BiomeFieldConfig` asset) is backlog for a scene that also needs Nutrient for something else.

**The ecosystem loop (11.4 scene):**

    PPS cells ──Nutrient──► Physarum chemotaxis: veins grow between creatures, and eat the Nutrient
    Physarum ──Pheromone0──► PPS: SpeedBoost ×2 → turn noise (cells dissolve where the scent is high)
    Neuron firing ──Dispersal pulses──► everyone flees; PPS cells are agitated + pushed outward
                                        (Dispersal chemotaxis −2, fieldDrift 1.5), then re-nucleate

Tuned in the Editor. 11.3's Physarum write rate (0.012/agent/step) saturated Pheromone0 (mean
0.38) and agitated every cell everywhere, so the 11.4 umwelt writes 0.004 (mean ≈ 0.08, max ≈ 0.26)
and the PPS read weighs it ×2. The firing comes from a local `NeuronFiringPlayback` loop (the
Shanghai cut), so the scene needs no TouchDesigner.

The result is a predator/prey ecology with no lifecycle code. Cells far from veins grow and divide
and attract veins. Veins agitate them back into free nutrients, which the veins consume or which
re-nucleate as spores elsewhere. Measured over 2 minutes of steady state, the particle classes
stay flat (~37 % free, ~2 % spore, ~60 % cell body, ~1 % nucleus by the paper's N thresholds): a
density-regulated equilibrium of turnover, as in the paper's Fig. 3H.

**Neuron coupling** follows the shared contract: agent `i` → neuron `i % 131`; firing →
`firingSpeedMul`, `firingDepositAmount`.

## Spawn

`SpawnMode.NeuronPositions` (default): each particle independently, with probability
`neuronSeedFraction` (default 0.12, hashed by id so every type gets its share), is born within a
Gaussian `seedRadius` (2.5 su) of neuron `i % 131`. At the defaults that puts ≈ 10 particles at each
neuron, about a spore's worth, so every neuron seeds a creature. (0.3 put ~40 per neuron into 11.3's
narrow neuron strip, which congealed into tissue.) The rest
scatter uniformly as the nutrient soup. In this mode only the seeded share fires (the same hash
decides both). Index `i` shares neuron `i % 131` with ~80 particles, most of them soup, so this is
what makes a firing neuron excite the creature born on it rather than scattered soup. Other
spawn modes keep the shared index-mapped firing.
`Scatter` = the paper's initial condition. `BottomEdge`/`TopEdge` = the shared band spawns.
Keep-out eviction matches the other sims.

## Render: membrane vs nucleus

The paper colours particles by their neighbourhood class (green nutrients, brown spores, blue
membranes, yellow nuclei). Discrete classes do not fit the HSB palette system, so the sim renders
a **continuous density ramp** instead:

- The trail array grows a second bank of per-type layers: `[0, T)` trail, `T` total,
  `[T+1, 2T]` **density-weighted trail** (`deposit · ramp(N)`, with ramp going 0 → 1 from
  `densityFloor` to `densityFull`). Both banks diffuse identically, so `dens = weighted / trail` at
  a pixel is the deposit-weighted mean neighbourhood density of the light there. It survives blur
  and fade unchanged. The pair saturates together, which keeps the ratio honest.
- Colour per type: `hsb2rgb(hue + densityHueShift·dens, saturation·(1 − 0.6·densityGlow·dens),
  brightness·trail·lerp(1 − sparseDim, 1 + densityGlow, dens))`. With shift = glow = sparseDim =
  0 this is exactly the other sims' formula, pinned by the shared render test. The soup (N ≈ 6,
  below the floor) dims, and dense matter glows. Any hue shift between cyan and gold passes
  through green, so the shipped look keeps the shift small and lets the glow carry the nucleus.
- Each particle deposits a soft disc of `size` su (centred on its sub-pixel position) instead of
  a single pixel, so a creature's membrane reads as a body at any resolution.
- `SimulationBase` gains `protected virtual int TrailLayerCount => TypeCount + 1`, so the base
  `Allocate()` (and its `MarkAllocated()`) stays the only allocation path. No `Allocate`
  override, no second buffer pair to swap in `FadeStep`. The type params are bound at the
  *allocated* type count (`AllocatedTypeCount`), so a live edit of the type list can't move the
  layer layout or index past it before the reallocating Reset.
- `SimulationBase` gains `protected virtual int TrailLayerCount => TypeCount + 1`, so the base
  `Allocate()` (and its `MarkAllocated()`) stays the only allocation path. No `Allocate`
  override, no second buffer pair to swap in `FadeStep`. The type params are bound at the
  *allocated* type count (`AllocatedTypeCount`), so a live edit of the type list can't move the
  layer layout or index past it before the reallocating Reset.

## Params (`PrimordialParams`, per type)

| param | default | range | unit |
|---|---|---|---|
| alpha | 180 | −180…180 | deg |
| beta | 17 | −90…90 | deg |
| radius | 5 | 1…20 | su |
| speed | 0.67 | 0.05…3 | su/step |
| size | 0.55 | 0…2 | su (drawn particle disc; render only) |
| noise | 0 | 0…45 | deg σ |
| otherTypeWeight | 1 | −1…1 | weight of other types in `L`/`R` (0 = species pass through each other, <0 = inverted turning) |
| fieldDrift | 0 | −20…20 | su/step per unit field difference (clamped to 0.5·v) |
| agitationNoise | 30 | 0…90 | deg σ per unit perception A |
| depositAmount, diffuseRate | 0.35, 0.8 | 0…1, 0.8…1 | |
| densityFloor, densityFull | 10, 35 | 0…40, 5…80 | N |
| densityHueShift, densityGlow, sparseDim | 0.12, 1, 0.85 | −0.5…0.5, 0…2, 0…1 | |
| hue, saturation, brightness | 0.5, 0.9, 0.9 | 0…1 | |
| firingSpeedMul, firingDepositAmount | 1.5, 1 | 1…5, 0…1 | |

Per-sim (component, take effect on reset): `spaceUnitPx` (8), `density` (0.08), `substeps` (3),
`neuronSeedFraction` (0.12), `seedRadius` (2.5 su).

## Integration

- `AgentFamily.Primordial` and `TimelineAction.StartPrimordial/StopPrimordial` are **appended**
  (both are serialized as ints).
- `SimulationManager`: Start/Stop/Reset Primordial buttons. OSC `/sim_resetPrimordial`.
- OSC param prefix: `SimulationBase.OscPrefix` (virtual, default first letter). Primordial
  overrides it with `pps` because `p` is Physarum's: `/pps_alpha_0`.
- MFT: family LED colour magenta (`RGB_MAGENTA = 97`; green 43 is the Biome knobs' on the same
  bank-2 grid), knob tag `Pr` (`P` is Physarum's), type count generalized through
  `LiveParamSet` → `PresetParamSet`. Params inspector: `PrimordialParamsEditor`.
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
- `AgentRenderKernelTests` gains Primordial (shift = glow = sparseDim = 0 ⇒ the shared colour formula).
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
