---
status: accepted
date: 2026-10-20
tags: [adr, primordial, pps, sims, coupling, units]
related: [[../ARCHITECTURE]], [[../superpowers/specs/2026-10-20-primordial-sim-design]], [[../sessions/2026-10-20-primordial-sim]], [[0011-field-native-sims-derive-simulationbase]]
---
# ADR-0017: The primordial particle sim works in the paper's space units and is coupled through turn noise, not speed

## Context
`PrimordialSim` runs Schmickl et al.'s motion law (Δφ = α + β·N·sign(R − L), *Sci. Rep.* 6:37969).
Its life cycle (spores eat free particles, grow into cells, divide, decay) exists only in a narrow
region of parameter space: ⟨r = 5, α = 180°, β = 17°, v = 0.67⟩ at ~0.08 particles per unit².
Three engine conventions would each push the sim out of that region:
- **Pixel units with per-param rez scaling** (the other sims). r and v would be authored in
  pixels, and the particle count would be a free number. Density, the variable that decides
  whether cells form, would then change with resolution and with every count edit.
- **Speed as the coupling lever.** Perception G (speed penalty) and A (dispersal boost) multiply
  every sim's speed. For PPS, v/r sets cell size and stability. Measured in the 11.4 rig: a
  dispersal multiplier on Physarum's saturated scent dissolved cells everywhere, not where the
  veins were.
- **Turn direction.** The paper's "positive Δφ is a turn to the right" has to agree with the side
  test. The first build counted R clockwise but turned counter-clockwise for positive Δφ. That is
  ⟨α, −β⟩, and the soup congealed into tissue blobs instead of growing cells.

## Decision
- **Paper space units.** Every PPS distance (radius, speed, drawn size, drift) is in su.
  `spaceUnitPx` (px per su at the 2160 reference, scaled by `rezY/referenceHeight`) maps the whole
  world to pixels. `PrimordialParams.ScaleSpatial` is a no-op because the unit already carries the
  scale.
- **Derived population.** `count = density · world area in su²`, computed and cached in `Reset()`.
  The world is `referenceHeight / spaceUnitPx` su tall at any resolution, so the population is
  resolution-independent. The manager's per-step write-back reads the cache, never a live
  recomputation.
- **Convention pinned in tests.** y-up; right = `cross(h, d) < 0`; positive Δφ turns clockwise;
  sense → turn → move along the new heading; synchronous update. `PrimordialMotion` (Biomes.Core)
  is the CPU reference, and `PrimordialKernelTests` holds the GPU kernel to it on a random soup.
- **Coupling through the paper's robustness axis.** Perception A adds Gaussian turn noise
  (`agitationNoise` deg σ per unit A) on top of the shared dispersal speed response, which the
  11.4 rig runs at a mild ×(1 + 0.5A). Noise is what the paper characterizes as dissolving cells
  (Fig. 6D). The shipped PPS umwelt has no SpeedPenalty read.
- **Drift only for transient fields.** R − B drives a positional drift, clamped to 0.5·v. A
  near-uniform drift moves a cell intact. A standing scent with maxima pulls the whole soup onto
  them: at 2v the 11.4 soup collapsed into a few points.
- **No new biome channel.** PPS writes Nutrient (its free particles are the paper's "nutrients",
  and Physarum already hunts Nutrient). It feels Physarum through Pheromone0.

## Consequences
- The paper's numbers are the presets. Particle count is not an inspector field; `density` and
  `spaceUnitPx` are, and both take effect on reset, like every other sim's agent count.
- `SimulationBase` gained two virtuals with no behaviour change for existing sims:
  `TrailLayerCount` (PPS keeps a density-weighted second trail bank) and `OscPrefix` (`pps`,
  because `p` is Physarum's).
- Predation emerges without lifecycle code. In 11.4, cells attract Physarum (Nutrient), and
  Physarum's scent agitates them back into nutrients. The measured population sits in a
  density-regulated equilibrium (~37 % free, ~60 % in cell bodies over 2 min) while individual
  cells turn over.
- A scene that needs Nutrient for something else (decomposition food webs) needs a dedicated PPS
  channel: `BiomeChannel.Count/Names`, `CH_COUNT`, every `CopyChannelsExcept` mask, every
  `BiomeFieldConfig` asset. That is backlog.

## Related
[[../superpowers/specs/2026-10-20-primordial-sim-design|spec]] ·
`Assets/Workspace/11.0 Biomes/src/computes/PrimordialSim.compute` ·
`core_math/PrimordialMotion.cs` · [[../sessions/2026-10-20-primordial-sim|session]]
