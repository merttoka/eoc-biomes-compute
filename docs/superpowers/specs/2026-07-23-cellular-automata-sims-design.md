---
status: draft
date: 2026-07-23
tags: [spec, sim, cellular-automata, cca, biome, edge-of-chaos]
related: [[../../ARCHITECTURE]], [[../../ROADMAP]], [[../../../Assets/Workspace/11.0 Biomes/docs/INTEGRATION_DESIGN]]
---
# Cellular Automata Sims — CCA + 2D Lookup CA as Field-Native Layers (Design)

## Goal

Bring the cellular-automata lineage from the predecessor repo
(`edge-of-chaos-unity-compute`) into the biomes engine as **first-class field-native sims** —
sims with no agents, whose state lives on a grid. Two rules ship on one shared base:

1. **CCA** — cyclic (Griffeath) cellular automaton. `range / threshold / nstates / moore`;
   a cell advances to its next state when `threshold` neighbors already hold that next state.
   Smooth spiral / demon waves. (Ported from `1.1 CCA/CCACompute.compute`.)
2. **CA2D** — multi-state totalistic **lookup-table** CA. A von-Neumann 5-cell neighborhood
   indexes a precomputed transition table (`buffer[nstates^5]`, seed-generated). `nstates = 2`
   → binary Life-like; higher → generative multi-state structure. (Ported from
   `5.5o 3D Edge of Chaos/CA2DCompute.compute`.)

Each CA (a) renders as its own additively-composited layer alongside physarum/boid/termite, and
(b) can **publish its state into a shared biome channel** so the existing agent sims perceive and
are shaped by it through `UmweltMapping` — reviving, and generalizing, the hand-wired CCA→agent
coupling that already existed in `6.2 Mixed Sim -- PBCCA-Perlin`.

## Why

- The CA family is where "edge of chaos" comes from; it's the one major simulation modality in the
  legacy repo that never made the jump to the biome architecture. Physarum, boids, and termites
  are all agent sims — the engine has never hosted a pure grid sim.
- The biome is already a grid-native PDE field with per-species Umwelt perception. A CA is *also* a
  grid process. So the deep integration ("CA as a substrate agents live in") is nearly free once a
  field-sim base exists — it reuses the entire perception pipeline with **zero per-sim shader
  changes**: an agent species opts in by adding one read entry to its `UmweltMapping`.
- Aesthetic contrast is a feature: CCA is smooth/wave-like, CA2D is crisp/structural. That maps
  cleanly onto two different roles in the ecosystem (a medium to follow vs. a lattice to avoid).

## Architectural fit (what makes this low-friction)

`SimulationManager` already **null-guards** the agent path, so a zero-agent sim drops into the
`simulations` list without special-casing the orchestrator:
- writeback loop: `if (posBuffer == null) continue;` (`SimulationManager.cs:328`)
- perception build: guarded by `sim.umwelt != null && sim.perceptionTex != null` (`:299`)

The blocker is that `SimulationBase` is **agent-centric**: it owns per-type trail
`Texture2DArray`s, agent buffers, and `MoveAgents/WriteTrails/Diffuse` kernels, and its abstract
contract demands `GetAgentPositionBuffer()` / `GetAgentCount()` / `TypeCount`. A CA uses none of
that. Rather than subclass `SimulationBase` and leave half its machinery dead, factor a thinner
base (below) — the second CA-shaped sim (reaction-diffusion is the obvious next one) would
otherwise force the same refactor later.

## Semantic model (the core idea)

Both CAs are the **same shape** to the engine: one integer state per cell, ping-ponged each step,
published as a normalized `0..1` texture. They differ only in the *rule kernel* and the *param
set*:

| | CCA | CA2D (lookup) |
|---|---|---|
| Params | `range / threshold / nstates / moore` | `nstates` + transition table + init mode |
| Rule source | inline count/threshold logic | uploaded `int[nstates^5]` table (`lambda`-sparsified) |
| Neighborhood | von-Neumann or Moore, radius `range` | von-Neumann 5-cell (radius 1) |
| Look | smooth spiral waves, cyclic | crisp lattice / gliders / blooms |

Because they share a base, running **both at once** is the intended configuration, not an
either/or.

## Components

### 1 · `FieldSimulationBase` (new thin base)
- Sibling to `SimulationBase`, or a shared abstract parent both derive from. Owns: `outTex`, the
  clear-in-place alloc signature (`_allocRezX/Y`, reallocate only on rez change — same discipline
  as `SimulationBase.Allocate`, so the composite/Syphon texture instance stays stable per
  [[../../adr/0008-clear-in-place-reset|ADR-0008]]), `Reset/Step/Render` lifecycle, `compositeWeight`,
  `IControllableSim` (string param get/set → MIDI/OSC for free), and the neuron-firing binding.
- Implements the agent contract as no-ops so the manager can still hold it in
  `List<SimulationBase>` **without** a type split: `GetAgentPositionBuffer() => null`,
  `GetAgentCount() => 0`, `TypeCount => 1`. (Decision point for the plan: unify under
  `SimulationBase` with virtual no-op agent members, vs. a parallel base + a shared `ISimLayer`
  interface the manager iterates. Lean toward the former — smallest manager change.)
- Owns **double-buffered** state textures `stateRead` / `stateWrite` (`RFloat`, point-filter,
  repeat-wrap) + a `SwapState()`. **This matters:** the ported CCA already ping-pongs
  (`CCA2D.cs` `readTex`/`writeTex`/`SwapTex`), but the ported **CA2D does not** — `CA2D.cs` binds a
  single `simTex` as both read and write in `StepKernel`, an in-place update where threads read
  neighbors other threads have already overwritten (a data race, tolerable at their scale but not a
  correct synchronous CA). The base **fixes this for both** by mandating the read/write pair.

### 2 · `CellularSim` rule variants
Two concrete sims on the base, each = a compute shader + a params SO + a thin `.cs`:
- **`CyclicCASim`** (`CyclicCA.compute`, `CyclicCAParams`): port `StepKernel` from
  `CCACompute.compute` (count-next-neighbors ≥ threshold → advance). Params `range` (1–10),
  `threshold`, `nstates`, `moore`. Optional `SecondaryNoiseKernel` (state re-seed) as a
  MIDI/OSC-triggerable perturbation.
- **`LookupCASim`** (`LookupCA.compute`, `LookupCAParams`): port `StepKernel` from
  `CA2DCompute.compute` (5-cell index → `transitionTable[idx]`). Params `nstates` (2–6), `seed`,
  `lambda` (Langton's λ — table sparsity, the edge-of-chaos knob), `initMode` (line/rect/circle).
  Rule table generated CPU-side in `Reset` and uploaded (`ComputeBuffer`), exactly as `CA2D.cs`
  does; expose "randomize seed" and "regenerate table" as `[Button]`s.
- Both follow the `IParamSet` scale hooks — but note CA params are **mostly unitless** (counts,
  states), so `ScaleSpatial`/`ScaleDensity` are near no-ops; `range` is the only cell-unit param and
  is deliberately *not* rez-scaled (it's a rule parameter, not a physical distance).

### 3 · Composite layer output (the "on screen" path)
- Each CA writes its render into `outTex`; the manager already composites up to 8 sim `outTex`
  additively with per-sim `compositeWeight` (`SimulationManager.Render`). Nothing new here — CA
  layers get weighting, MIDI mixing (piano/MFT), and per-type reset for free.
- Port a render kernel (HSB state → color); reuse `Includes/color.hlsl` conventions already in-repo.

### 4 · Biome substrate channel (the "real payoff" path)
Make a CA an **environmental process** the agent sims perceive:
- Add a channel — e.g. `Excitability` (CCA) and/or `Substrate` (CA2D) — to `BiomeChannel`
  (`BiomeFieldConfig.cs`) + a config row. `BiomeChannel.Count` and the `Names[]` array grow
  together (single source of truth already enforced there).
- The CA publishes its normalized state into that channel each step. Two wirings (plan picks one):
  - **B1 — CA writes the channel directly.** A tiny publish kernel copies `stateRead / nstates`
    into the biome field layer (`Biome.FieldReadArray` is public). Cheapest; CA owns the channel,
    biome PDE leaves it alone (`diffuseRate 0`, `relaxRate 0`) — or *doesn't*, if we want the CA
    pattern to bleed/advect through the existing flow field.
  - **B2 — CA update runs inside `Biome.Step`.** Fold the rule into `Biome.compute` as another
    channel-update kernel, gated by biome `stepEvery`. Tighter conceptually (CA becomes a biome
    process) but couples the CA rule into the biome shader — heavier blast radius. Defer.
- Agent species opt in via `UmweltMapping.reads` — **no sim shader edits**:
  - CCA `Excitability` as `Chemotaxis` (+weight) → physarum follows the spiral arms.
  - CA2D `Substrate` as `SpeedPenalty` / `Avoidance` → boids stall / steer around the crisp
    lattice. (This reproduces the legacy PBCCA "CCA scales sense-distance / max-speed" coupling,
    generically, through perception slots the agents already read.)

### 5 · CA↔CA coupling (why "both together" > two layers)
The compelling configuration, and the most "biome"-native:
- **One CA masks the other (structure ⟶ flow).** Gate the CCA step by CA2D liveness: CCA only
  advances where the CA2D cell is alive, so excitation waves propagate *through* the standing
  lattice. Structure and flow become one coupled system — mirrors the termite→permeability→
  confinement pattern already shipped ([[../../adr/0010-permeability-agent-built-topography|ADR-0010]]).
  Implementation: bind the other CA's `stateRead` into the step kernel as a mask input.
- **Cross-ignition.** CCA wavefronts crossing a phase boundary flip CA2D cells on (excitation
  plants life); or **neuron firing** perturbs both (reuse `BindNeuronFiring` + `neuronPositions`)
  — a firing organoid neuron ignites a wavefront at its canvas position, tying the CA into the same
  playback clock as the diurnal sun and dispersal pulses.
- Theoretical coherence: CCA is an excitable-medium model and Life-like totalistic rules sit on the
  same excitable / edge-of-chaos spectrum, so "CCA + CA2D" reads as two dialects of one idea.

## Data flow (per sim step)
1. **Perception** — (if wired to a channel) each agent sim's perception is built from the biome,
   now including the CA-published channel(s) (§4).
2. **CA step** — each `CellularSim` runs its rule kernel `stateRead → stateWrite`, swaps, renders
   `outTex` (§2–3). CA↔CA mask inputs read the *previous* step's state (§5).
3. **Publish** — CA normalizes state into its biome channel (§4, B1).
4. **Agent sims move** — physarum/boid/termite perceive the CA channel via Umwelt (unchanged code).
5. **Biome.Step** — PDE runs; CA channel either held static or advected, per config.
6. **Composite** — additive blend of all sim `outTex` (agent + CA layers) with weights.

## Files touched (plan will refine)
- **New** `src/components/core/FieldSimulationBase.cs` (or virtual no-op agent members added to
  `SimulationBase.cs`).
- **New** `src/components/Sim/CyclicCASim.cs`, `LookupCASim.cs`.
- **New** `src/computes/CyclicCA.compute`, `LookupCA.compute` (ported + double-buffered + render).
- **New** `src/params/CyclicCAParams.cs`, `LookupCAParams.cs` (`IParamSet`).
- `src/components/core/SimulationManager.cs` — hold CA sims in `simulations` (verify the null-guards
  cover them; add a `ResetCellular` per-type reset button mirroring `ResetPhysarum` etc.).
- `src/components/core/BiomeFieldConfig.cs` + `BiomeChannel` — add channel(s) for §4.
- `src/components/core/Biome.cs` — publish path (B1: expose a channel-write-from-texture helper), or
  `Biome.compute` kernel (B2, deferred).
- Params editor (`src/Editor/ParamsEditor.cs`) — randomize/table-regen buttons if not free via
  `[Button]`.

## Non-goals (deferred)
- **1D CA** (`2.1 Edge of Chaos`) — doesn't map onto the 2D composite cleanly; skip.
- **3D / voxel CA + history stacking** (`5.2o`, `5.3o`, `5.5o` instancer) — out of scope for the 2D
  biome composite.
- **Isotropic / Moore lookup table** for CA2D — the source flags it as a TODO
  (`CA2D.cs:33`, `:145`); ship the anisotropic von-Neumann table first.
- **B2 (CA rule inside `Biome.compute`)** — start with B1 (CA-owned channel), promote later if it
  earns it.
- **Agent-authored CA seeding** (termites writing CA births) — a later layer once the substrate
  coupling is proven.

## Risks / open tuning
- **State quantization.** Integer CA state ↔ `0..1` float channel: publish `state / nstates`, and
  keep the *authoritative* state in the CA's own `RFloat` texture (round on read) so accumulated
  float error never corrupts the rule. Don't run the rule off the shared biome layer.
- **In-place race (source bug).** CA2D as ported updates one texture in place — the base's mandated
  double-buffer fixes it; make sure the port doesn't inherit the single-`simTex` binding.
- **CCA neighborhood cost.** `range=r` Moore is `O((2r+1)^2)` samples per cell per step; at 2×FHD
  sim res and `r=10` that's heavy. Run CAs at a **reduced sim res** (they're coarse by nature —
  reuse `simResolutionScale`, or a per-sim res override) and/or a `stepEvery` decimation like the
  biome.
- **Rule-table authoring UX.** CA2D "params" are a `nstates^5` seed-generated buffer, not scalars —
  interpolation/snapshots (`ParameterInterpolator`) can't morph a table. Treat seed + `lambda` +
  `nstates` as the interpolatable surface; the table regenerates on change.
- **Aliasing at low res / point sampling.** CA state is point-filtered (correct for the rule) but
  looks blocky upscaled in the composite — the render kernel should smooth for display only, never
  feed a smoothed value back into the rule.
- **Determinism.** CA reset seeding must use the sim-step RNG convention, not `Time.frameCount`
  (per the FPS-independent-sim work), so multi-step catch-up frames stay deterministic.

## Success criteria (play-mode; no automated tests in this project)
- CCA produces stable spiral/demon waves; CA2D produces recognizable Life-like/multi-state
  patterns — both as their own composited layers with working `compositeWeight` + MIDI mixing.
- Both reset in place (no Syphon teardown/flash on `Reset`), resolution-independent placement.
- An agent species wired to a CA channel visibly responds (physarum tracks CCA arms, or boids
  avoid CA2D lattice) with **no change to that sim's shader** — only its `UmweltMapping`.
- CA↔CA coupling (§5) legible: masking confines the CCA wave to the CA2D structure, and/or a neuron
  firing ignites a visible wavefront.

## Follow-ups
- Promote the field/agent base split to an **ADR** once the base lands (it's an architecturally
  significant change to the sim contract).
- Update `ARCHITECTURE.md` §3 (sim taxonomy) and README "Concepts" when this ships (not before —
  those track shipped pillars).
