---
status: closed
date: 2026-10-20
tags: [session, primordial, pps, physarum, ecology, scene]
related: [[../ARCHITECTURE]], [[../adr/0017-primordial-sim-paper-units-noise-coupling]], [[../superpowers/specs/2026-10-20-primordial-sim-design]]
---
# Primordial particle system — the 4th agent sim and the 11.4 PPS scene

Branch `feat/primordial-sim`. Brief: add a primordial particle system (Schmickl et al. 2016) as
the 4th sim, in its own scene folder (11.4), coupled to Physarum, so the a-life ecology gets
creatures. The starting point was the crude WebGPU sketch in the lab site (`online-petri-dish/pps`).
That sketch is O(n²), moves along the pre-turn heading, and races in place. The work was done in
the live Editor through the Unity CLI pipeline package, with Play-mode `ExportPNG` frames as eyes.

## Shipped
- `PrimordialSim` + `PrimordialSim.compute` + `primordial_type_params.hlsl` + `PrimordialParams`:
  - **Model:** the motion law on the GPU, using Boid's spatial-hash set and a synchronous
    double-buffered update. `substeps` PPS iterations per sim step.
  - **Units:** paper space units mapped by `spaceUnitPx`; the agent count is derived from
    `density` and cached on Reset.
  - **Spawn:** a neuron-seeded spawn (≈10 particles per neuron) with the rest scattered as soup.
  - **Render:** a density-weighted second trail bank for the membrane/nucleus render
    (`densityFloor`/`Full`, hue shift, glow, `sparseDim`), plus a per-type particle `size` splat.
  - **Coupling:** perception A → turn noise (`agitationNoise`); R − B → positional drift,
    clamped to 0.5·v.
- `PrimordialMotion` (Biomes.Core): the CPU reference. 13 new tests: 10 motion conventions, GPU
  hash + Move vs. the reference on a 400-particle soup, and render + struct-mirror rows. Suite
  180/180.
- `SimulationBase`: `TrailLayerCount` and `OscPrefix` virtuals (no change for the existing sims).
  `AgentFamily.Primordial` and `TimelineAction.Start/StopPrimordial` are appended.
- Control surfaces (via an implementation subagent):
  - Manager Start/Stop/Reset Primordial; OSC `/sim_resetPrimordial`, `/pps_<param>_<i>`.
  - MFT: magenta LED (97, because green 43 is Biome's), `Pr` knob tag, type count via the live
    or preset param set.
  - `MIDI_OSC.md` section.
- `Editor/PrimordialSceneSetup.cs` (`Biomes → Setup 11.4 PPS Scene`) builds
  `11.4 PPS Scene/Scene_PPS.unity` from the 11.3 rig: Physarum + Primordial, scene-local assets.
  It also adds a two-type hunting Physarum, a quiet Pheromone0, a 16:9 neuron layout, and a local
  firing playback loop. The script was verified by deleting the folder and regenerating it.

- Fixes from an independent review of the branch: type params are bound at the allocated type
  count (a live type-list edit indexed trail layers past the array); the unit is cached on Reset
  with the count; in neuron spawn mode only the seeded share fires (a neuron's index class is ~80
  particles, mostly soup); the setup script asks to save a dirty scene first; the splat is clamped
  to 16 px and the hash uses |radius|; `ScaleDensity` is a no-op (the splat already keeps the
  per-pixel deposit resolution-independent). Live edits verified in Play. Suite 180/180.

- Follow-up (user review): 11.4 keeps 11.3's Boids (20 k) and Termites (131) and its Physarum count
  (1 M), with Primordial appended in sim order and a fourth preview quad. The injector goes back to
  11.3's termite-position pulses, and the Physarum scent write drops to 0.001 for 4× the agents.
  Boids and Termites also graze Nutrient, so the creatures feed the whole web. Resolution check at
  1080p / 4K / 4800×1080: same world (480×270 su; 1200×270 on the wide canvas), the count scales
  only with width, and the population mix holds (spec "Resolution check"). `com.unity.pipeline`
  is committed (kept on request).

## Decided
- [[../adr/0017-primordial-sim-paper-units-noise-coupling|ADR-0017]]: paper units, derived
  population, paper turn convention, coupling through turn noise rather than speed, Nutrient as
  the v1 channel.
- Folder named `11.4 PPS Scene` (the `11.x <Name> Scene` convention); the brief said `11.4 PPS`.

## Found
- **Turn direction.** Gate 1 first ran ⟨α, −β⟩: R counted clockwise, positive Δφ turned
  counter-clockwise, and the soup congealed into tissue blobs. The paper says "a positive change
  of φ is a turn to the right". After the fix, cells, divisions and colonies emerged as in its
  Fig. 3 (scatter, ~14 k iterations). Pinned by `PositiveTurn_IsARightTurn_TowardTheRightSide`.
- **Speed as a lever kills cells.** 11.3's Physarum write rate saturated Pheromone0 (mean 0.38,
  22 % > 0.5). The dispersal multiplier on that scent dissolved every cell, because v/r is what
  holds them together. Fixed with a quiet scent (0.004 → mean ≈ 0.08), agitation through noise, and
  ×0.5 speed response.
- **Drift collapses the soup.** Drift up Pheromone0 (fieldDrift 6, clamp 2v) pulled every particle
  onto the scent maxima within a minute. The drift is now clamped to 0.5·v and the shipped umwelt
  drifts only on transient dispersal pulses.
- Unity's add-component `Reset()` reached `PrimordialSim.Reset` before `cs` was assigned. It is
  guarded the way the CA sims are, so no orphan params clone serializes into the scene.
- Measured, 1080p, M4 Max, op run 200× from one state: Primordial 0.66 ms/step (3 substeps,
  10.4 k particles), 0.40 ms at 1 substep; 11.4 Physarum (250 k) 0.55 ms. In steady state the
  particle classes hold ~37 % free / ~2 % spore / ~60 % cell body / ~1 % nucleus for 2 min: a
  density-regulated equilibrium of turnover.

## Open / next session
1. Show resolution: 11.4 runs at 1920×1080 for iteration. At 4K, check creature size
   (`spaceUnitPx`) and Physarum count on the show machine.
2. MFT magenta (97) is untested on the device. Several PPS params (agitationNoise, density
   render knobs) are OSC-only on the MFT layout.
3. A symbiosis variant (creatures riding the veins) needs a lever that doesn't converge, e.g. a
   drift along the vein tangent or a capped attraction. Backlog in [[../ROADMAP]].
4. Not merged; on `feat/primordial-sim` for user testing.
