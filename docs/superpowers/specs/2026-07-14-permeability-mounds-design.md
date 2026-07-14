---
status: draft
date: 2026-07-14
tags: [spec, biome, permeability, termite, habitat, stigmergy]
related: [[../../../Assets/Workspace/11.0 Biomes/docs/INTEGRATION_DESIGN]], [[../../ROADMAP]], [[../../ARCHITECTURE]]
---
# Permeability Mounds — Termite-Built Habitat Partitioning (Design)

## Goal

Replace the static, repetitive, immediately-dissolved permeability **noise** with a
**persistent, termite-authored structure** that partitions the field into habitats the other
species are confined to. Termites become world-builders: what they construct carves the
territories Physarum and Boids live in. Reuses permeability channel 7 and the currently-dead
`preferredPermeability` habitat gate — **no new GPU field, no deterministic terrain**.

## Why (from the 2026-07-13 audit)

- **No persistence.** Only termites write permeability (`−0.75` dig), but `InteractFieldsKernel`
  relaxes every cell back toward the fixed noise terrain each step, and termite heat biases the
  relax *target* upward right where they dig — so the coupling **fights** the write and the relax
  **heals** it. Agent structure cannot survive.
- **Dead visual.** The terrain is a seedless `uv`-only fBM crushed through a narrow `smoothstep`
  into near-binary blobs, identical every Reset.
- **Dead behavior.** Permeability's only live effect is a weak `perception.g` speed multiplier;
  the `preferredPermeabilityMin/Max` habitat gate is authored + MIDI-mapped but read by no kernel.

## Semantic model (the core idea)

Permeability keeps its meaning: **high = open/passable, low = solid/wall.**

- The **unbuilt** field sits **uniform-open** (~0.9).
- Termites build **downward** — deposits *lower* permeability → solid walls.
- The gradient between open and wall is naturally **mid**.

One builder + one scalar field therefore yields **three habitats**, and the *already-authored*
`preferredPermeability` bands match exactly (overlaps → the soft borders we want):

| Species | Habitat | Band (authored, now wired) |
|---------|---------|-----------------------------|
| Boids | open space (roam the unbuilt) | high — `0.6–1` |
| Physarum | the interface (colonize wall edges) | mid — `0.3–0.7` |
| Termites | walls (live in/around what they build) | low — `0–0.5` |

## Components

### 1 · Persistence spine (fixes write-then-dissolve)
- **Init** (`Biome.compute` `InitPermeabilityKernel`): replace the noise `smoothstep` with a
  uniform `openBaseline` (~0.9). Optional **per-run-seeded** faint low-perm blobs (toggle — NOT
  the old repetitive fBM) to bootstrap termite/physarum habitat if the empty start feels slow.
- **Relax target** (`InteractFieldsKernel` perm block): replace `permTerrain = smoothstep(noise…)`
  with the same `openBaseline`. **Keep** the `+ (temp−0.5)·tempToPermeability` coupling but set
  `tempToPermeability ≈ 0` (retained hook, off by default).
- **Channel settings** (`BiomeFieldConfig_Homeostatic.asset`): permeability `relaxRate` +
  `decayRate` **near zero** → walls persist (effectively permanent on show timescales; recovery
  is via ResetTermites, §4, not decay). `diffuseRate 0`, `advectedByFlow 0` unchanged.

### 2 · Termite wall-build (probabilistic accretion)
- Termites lower permeability by `wallDepositAmount` (~`−0.02`) at their cell, **gated by the
  existing per-agent deposit probabilities**: `depositProbability` (baseline) and
  `firingDepositProbability` (a stronger build **burst when the neuron fires** — construction
  pulses with the organoid playback, same clock the diurnal sun rides).
- Accretes over many steps → gradual, persistent walls (vs today's one-shot `−0.75`).
- Reuses the termite `WriteTrails` kernel's probability + firing gating; the wall write targets
  the **biome permeability field** (binding the biome field into the termite build kernel, or a
  small dedicated build kernel — exact wiring is a plan detail). `wallDepositAmount` is a new
  `TermiteParams` field; the probabilities are reused, not duplicated.
- Build **location** = termite position; termites already congregate at the `|∇Humidity|` edge
  (existing cue driven by the diurnal sun), so walls form there for free.

### 3 · Habitat confinement (wire the dead gate)
- In the **per-species perception build** (`Biome.compute` `ReadFieldKernel`, dispatched per sim
  by `Biome.BuildPerceptionTex`), pass the species' `preferredPermeabilityMin/Max` as uniforms.
- Compute out-of-band distance and fold into the existing perception slots:
  `outOfBand = max(0, min − perm) + max(0, perm − max)` (0 inside band);
  `avoidance += outOfBand · habitatAvoidGain` (steer back) and
  `speedMod *= saturate(1 − outOfBand · habitatSlowGain)` (can't progress).
- Agents strongly avoid + slow outside their band ("mostly can't cross"); overlapping bands keep
  borders soft. `habitatAvoidGain` / `habitatSlowGain` are global tuning knobs.

### 4 · Termite-owned mounds (ResetTermites melts them)
- The permeability/mound field is **termite-owned state that lives in the shared biome.**
- `ResetTermites()` (`SimulationManager`) additionally clears permeability ch7 back to
  `openBaseline` — a **targeted per-channel clear** (new `Biome` method + small clear kernel, or
  reuse init for ch7 only). `ResetPhysarum`/`ResetBoids` leave it untouched; full `Reset` clears
  all channels as today.
- Melting the walls **frees the confined species** — Physarum/Boids spill out while the rest of
  the ecosystem keeps running. A legible performance gesture, and the escape valve that makes
  near-zero decay safe for a multi-hour install.

### 5 · Composite mound overlay (visualize the structure)
- A **post-composite overlay pass** modeled on the existing `NeuronRingKernel`
  (`SimulationManager.compute` + dispatch in `SimulationManager.Render()`): after
  `CompositeRenderKernel`, sample the permeability field and paint the built structure
  (earthy/dark walls) onto `compositeOut`.
- Own `moundOverlayStrength` + color uniforms; independent of the additive sim blend, so it
  never fights trail brightness. Expose the permeability texture to the manager (biome getter).

## Data flow (per sim step)
1. **Build** — termites stochastically lower permeability at their cells (§2).
2. **Biome.Step** — permeability persists (near-zero relax to `openBaseline`; temp coupling ≈0) (§1).
3. **Perception** — per species, out-of-band permeability → avoidance + speed penalty (§3).
4. **Sims move** — agents confined toward their band; termites keep building.
5. **Composite** — additive sim blend, then the **mound overlay** paints walls on top (§5).

## Files touched (from audit; plan will refine)
- `Assets/Workspace/11.0 Biomes/src/computes/Biome.compute` — `InitPermeabilityKernel`,
  `InteractFieldsKernel` perm block, `ReadFieldKernel` (habitat gate), a permeability-clear kernel.
- `.../src/components/core/Biome.cs` — pass band uniforms in `BuildPerceptionTex`; add
  `ClearPermeability()`; expose the permeability texture.
- `.../src/computes/TermiteSim.compute` + `TermiteParams.cs` — probabilistic wall write +
  `wallDepositAmount`.
- `.../src/components/core/SimulationManager.cs` + `SimulationManager.compute` — `ResetTermites`
  clears ch7; new mound-overlay kernel + dispatch + strength/color.
- `.../assets/BiomeFieldConfig_Homeostatic.asset` — perm relax/decay ≈0, `tempToPermeability ≈0`,
  `openBaseline`.
- (`preferredPermeabilityMin/Max` on `UmweltMapping.cs` already exist — now consumed.)

## Non-goals (deliberately deferred — future layers on this spine)
- **Curvature / Laplacian stigmergy** (self-organizing galleries) — approach ②; a swap-in *build
  rule* later, no rework of confinement.
- **Humidity-scaffolded build** as an explicit chemotaxis weight — approach ③ (the location bias
  is already implicit via the existing `|∇Humidity|` read).
- **Discrete land/water/air terrain types** (categorical field) — rejected for lock-in.
- **Mortality interaction** (corpses decaying inside walls) — waits on the mortality feature.

## Risks / open tuning
- **Confinement vs starvation.** Too-hard a gate could trap agents in shrinking pockets. Tune
  `habitatAvoidGain`/`habitatSlowGain`; `ResetTermites` is the escape valve.
- **Empty start.** Uniform-open means only Boids are home at t=0 (a succession arc); enable the
  seed toggle if it reads too slow.
- **Cross-system write.** The wall build binds the biome field into a termite kernel — a plan
  detail to keep clean (avoid a full extra dispatch if the existing write path can carry it).
- **Vestigial noise.** `noiseScale`/`noiseThreshold` become unused for permeability after this
  (confirm no other channel uses them before removing).
- **Perception downscale.** Confinement reads permeability through the low-res perception texture
  (`perceptionResScale`) — acceptable; habitat bands are coarse by nature.

## Success criteria (play-mode; no automated tests in this project)
- Termite walls **persist** (survive many minutes, don't dissolve).
- Visible **segregation**: Boids in open, Physarum on edges, Termites in/near walls.
- `ResetTermites` **melts** the walls; Boids/Physarum spill out; other sims keep running.
- Mounds are **visible in the composite** (not just the debug grid).
- No repetitive noise; per-run variation when seeded.
