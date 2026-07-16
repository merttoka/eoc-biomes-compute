---
status: closed
date: 2026-07-15
tags: [session, biome, permeability, habitat, termite, render]
related: [[../adr/0010-permeability-agent-built-topography]], [[../ROADMAP]], [[../superpowers/plans/2026-07-14-permeability-mounds]]
---
# Permeability mounds — termite-built habitat partitioning

Implemented the permeability-mounds plan on `feat/permeability-mounds`, play-tested, fixed two
bugs, merged to `main` (fast-forward). Tag `pre-perm-updates` @ `badb224` = pre-merge rollback.

## Shipped
- **Persistence spine** — `Biome.compute` init + relax drop the fBM noise; permeability starts at
  `permeabilityOpenBaseline` (0.9, `BiomeFieldConfig`) and relaxes toward it near-zero. Asset:
  perm `relaxRate 0.0005`, `temperatureToPermeability 0.02`. (`Biome.compute`, `Biome.cs`,
  `BiomeFieldConfig.cs`, `BiomeFieldConfig_Homeostatic.asset`)
- **Firing-gated build** — new `BuildPermeabilityKernel` + `Biome.BuildPermeability(...)`, called
  per termite sim in the `SimulationManager` write-back loop; probabilistic, pulses with neuron
  firing (reuses type deposit/firing-deposit probs). Removed the one-shot `channel:7 -0.75` umwelt
  dig. 1-elem dummy firing buffer when no source. (`TermiteSim.cs`, `SimulationManager.cs`,
  `UmweltTermite_Alt.asset`)
- **Habitat confinement** — wired the dead `preferredPermeabilityMin/Max` into `ReadFieldKernel`:
  out-of-band → `avoidance` + `speedMod` penalty. `Biome.habitatAvoidGain/SlowGain`.
- **ResetTermites melts mounds** — `Biome.ClearPermeability()` re-inits ch7 to open in both
  ping-pong buffers; `ResetTermites` calls it, other resets don't.
- **Composite overlay** — `MoundOverlayKernel` (SimulationManager.compute) paints walls over the
  composite, `moundOverlayStrength`/`moundColor`; `Biome.OpenBaseline` getter.
- **Bug fix — freeze deadlock** — added `habitatSpeedFloor` (0.25): out-of-band speed penalty is
  floored so termites (band 0–0.5, fully out-of-band at the 0.9 start) stay mobile enough to build.
  Without it `speedMult → 0`, termites froze (corner-clustering, only firing agents visible).
- **Bug fix — aliased overlay** — overlay samples the 256² perm field with built-in
  `sampler_linear_clamp` (bilinear) + `smoothstep` instead of point-sampling into the 1024 composite.
- **OSC tester default** — `tools/osc_index_tester.py` no-arg default = full-range loop,
  `/sim_resetSimsOnly` @ pass start (clears trails, keeps walls), then 5× `/sim_resetTermites`
  (melt) + 10× `/sim_resetPhysarum` evenly spaced. Multi-schedule reset support added.

## Decided
- Permeability = agent-built topography, not static terrain; habitat bands are the confinement
  mechanism → **[[../adr/0010-permeability-agent-built-topography]]**.
- Speed floor over per-species build exemption — simpler, general (helps Physarum too), keeps the
  "confined but not frozen" tuning knob.
- `/sim_resetSimsOnly` at pass start (not full `/sim_reset`): clears trails + respawns but **keeps
  the built walls** across passes; the 5× termite resets are what melt them mid-pass.

## Open / next session
1. **Legacy umwelt ch7 reads** — each umwelt still has effect-0 (chemotaxis, neg weight → pull to
   low perm) + effect-1 (speed penalty) reads on channel 7, authored for the old noise terrain.
   For Physarum/Boid the chemotaxis pull **contradicts** their bands. Decide: strip the ch7
   chemotaxis reads (all three, or just Physarum/Boid); optionally drop the effect-1 penalties and
   let the band own perm→speed.
2. **Per-run seed bootstrap** (plan Task 6) — deferred; add if the uniform-open start reads too slow
   (no Physarum/Termite habitat until walls form). Would re-introduce noise into init behind a toggle.
3. **Tuning** — `habitatAvoidGain`/`SlowGain`/`SpeedFloor`, `wallBuildAmount`, `moundColor`, and
   `biomeRezX` (256→512 for higher-detail walls) all live-tunable; not yet locked for the show.
4. **Push** — `main` is 10 commits ahead of `origin/main`, unpushed.
