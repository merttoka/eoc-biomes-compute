---
status: closed
date: 2026-07-11
tags: [session, biome, injector, temperature, diurnal, tuning]
related: [[../../Assets/Workspace/11.0 Biomes/docs/INTEGRATION_DESIGN]], [[../ARCHITECTURE]], [[2026-07-11-fps-independent-sim]]
---
# Diurnal sun — procedural injector source + indirect-only Temperature routing

Shipped INTEGRATION_DESIGN Tier-1 row 6 (the "master off-equilibrium pump"). Also closed
two backlog items found stale (humidity umwelt wiring, boid overrun) — see their own session
closes. This session = diurnal sun build + the routing diagnosis that made it read.

## Shipped
- **Diurnal sun** (`BiomeInjector`, commit `ed02da7`): new `Source.Drive.Procedural` mode — a
  self-animating source that ignores OSC and computes its own `fieldUV` + value from a phase
  clock. Diurnal preset (`AddDiurnalSunSource` button): warm `MaxToward` Gaussian sweeps
  Temperature L→R across daylight (uv.x = dayPhase), sine warmth envelope (0 sunrise → 1 noon
  → 0 sunset), dark cool night (no stamp — `MaxToward`'s `max(cur,·)` can't cool, field just
  relaxes to baseline). Reuses the whole Gaussian-stamp GPU path — **no HLSL**.
- **Phase from the neuron playhead**: `phaseSource FiringIndex` → `NeuronFiringSource.CurrentFrame
  / FrameCount`, so one firing-blob playthrough (`/index` 0→180000) is one day and the existing
  `index=0` resetSimsOnly lands on sunrise. Falls back to free-running `SimStepCount`/`periodSteps`
  (7200 = 2 min @60 Hz) when no OSC index / no firing source. `Inject(biome)` →
  `Inject(biome, SimStepCount)` (one call site, `SimulationManager:325`).
- **Indirect-only routing tune** (`25a4caa`): diagnosis found the sun invisible because (a)
  Temperature was agent-saturated (Boid metabolicHeat 0.08 vs relax 0.02 → pinned ~1.0, no
  headroom for `max()`), and (b) **no sim reads Temperature** (0 channel-5 reads) so it only
  acts through flow/humidity/Q10/permeability. Per artist call, kept it indirect (no temp
  chemotaxis). Retuned `BiomeFieldConfig_Homeostatic`: Temperature `diffuseRate 0.997→0.96`
  (sharper disk = stronger gradient for every indirect path), `relaxRate 0.02→0.06`,
  `initialValue →0.5`; `temperatureToEvaporation 0.05→0.13`, `decompositionTempSpan 2→3`,
  `temperatureToFlowStrength 0.6→0.8`; cut metabolic heat (Boid `0.08→0.02`, Physarum
  `0.01→0.0005`). DiurnalSun source added to `Scene_SIGGRAPH` (gain 0.9).
- **OSC per-family resets** (`4e40e12`): `/sim_resetPhysarum|Boids|Termites` → `ResetSimsOfType<T>`;
  `osc_index_tester.py --resets` sends N spaced resets through a `--stream`.
- **Docs**: INTEGRATION_DESIGN row 6 ✅ + row 4 (humidity build-cue) ✅ + Tier-1 Q10 caveat
  (front now travels via the sun); README concept bullet; this session; INDEX.

## Decided
- **Indirect by design.** No `UmweltMapping` reads Temperature — the sun is an *environmental*
  pump (flow/evaporation/Q10/permeability), not a chemotaxis leash. Tuning levers are headroom
  (metabolic heat ↔ Temperature relax) and gradient (Temperature `diffuseRate`), not agent reads.
- **Phase = playhead, not wall-clock.** Ties the biome's day to the organoid firing playback;
  `index=0` loop = sunrise on a fresh sim. `SimStepCount` fallback keeps it working headless.
- **General procedural drive**, not a one-off — `PhaseSource`/envelope hook also serves a future
  tempo-breath (INTERACTION_DESIGN_II) without a new code path.
- Live vs Reset knobs: cross-field scalars (evap/flow/perm/Q10) are per-`Step` live; per-channel
  `diffuseRate`/`relaxRate` need a Reset (`Biome.cs:131` `UploadChannelSettings`).

## Open / next session
1. Watch Flow saturation at `tempToFlowStrength 0.8` + sun gain 0.9 — design warns a too-strong/
   wide sun saturates Flow to ±1 → global drift. Co-tune with Flow `decayRate 0.02` if it drifts.
2. Optional: let Permeability track the moving sun (its `relaxRate 0.001` is too slow to follow) —
   raise to ~0.015, but it interacts with termite build/topography; tune last.
3. Sun currently only in `Scene_SIGGRAPH` — port to CURRENTS if wanted (add source + same temp tune).
