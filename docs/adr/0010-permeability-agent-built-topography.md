---
status: accepted
date: 2026-07-15
tags: [adr, biome, permeability, habitat, gpu]
related: [[../ARCHITECTURE]], [[../sessions/2026-07-15-permeability-mounds]], [[../superpowers/specs/2026-07-14-permeability-mounds-design]], [[0007-mass-conserving-diffusion-relax-channels]]
---
# ADR-0010: Permeability is agent-built topography with habitat-band confinement

## Context
Permeability (channel 7) was a **static fBM noise terrain** — a pure function of `uv`,
recomputed identically in `InitPermeabilityKernel` and in the `InteractFieldsKernel` relax
target. So termite "digs" (a one-shot umwelt `channel:7 amount:-0.75` write) always **healed
back to the fixed noise**. Meanwhile every `UmweltMapping` carried `preferredPermeabilityMin/Max`
(per-species, inspector-visible, MIDI-mapped) that **no kernel read** — dead code (ROADMAP).

Goal: persistent, **termite-built** walls that partition the field into habitats each species
is confined to (Boids open / Physarum edges / Termites walls), with succession — walls grow as
termites build, melt on `ResetTermites`. Reuse ch7; no new GPU field, no deterministic terrain.

Options: (a) keep the noise terrain + a separate deterministic habitat map; (b) drop the noise,
make permeability a **uniform-open baseline authored only by agents**, and wire the dead habitat
gate as the confinement mechanism.

## Decision
(b). Semantics: **permeability high = open/passable, low = solid/wall; termites build downward.**

- **Persistence spine.** Init and the relax target both drop the noise; permeability starts at
  `permeabilityOpenBaseline` (0.9) and relaxes toward it at a near-zero rate (walls persist, heal
  slowly). Temp→perm coupling retained but tuned to ~0.
- **Build.** A dedicated `BuildPermeabilityKernel` lowers permeability at termite positions,
  **probabilistically and pulsing with neuron firing** (reuses the type's deposit/firing deposit
  probabilities). Replaces the one-shot umwelt dig — permeability is now authored **only** here.
- **Confinement.** `preferredPermeabilityMin/Max` wired into `ReadFieldKernel`: out-of-band
  permeability adds **avoidance** (steer back) and a **speed penalty**, floored by
  `habitatSpeedFloor` so agents slow but never freeze.
- **Ownership.** `ResetTermites` clears permeability to open (melts mounds, frees confined
  species); `ResetPhysarum`/`ResetBoids`/`ResetSimsOnly` leave it intact.
- **Render.** A post-composite `MoundOverlayKernel` paints the walls (bilinear-clamp + smoothstep),
  independent of the additive sim blend.

## Consequences
- `preferredPermeabilityMin/Max` is **no longer dead** — it is now the primary permeability→steering
  mechanism. Legacy umwelt ch7 reads (effect-0 chemotaxis, effect-1 speed penalty) still exist and
  **overlap / for Physarum+Boid contradict** the bands (they pull toward low perm while the band
  pushes to the species' own band) — flagged for review, not yet removed.
- Permeability is now a clean **producer** (`BuildPermeability`) / **consumer** (habitat bands,
  `deathThresholdPermeability`) split — no sim writes ch7 through the umwelt path.
- **The speed floor is load-bearing.** Termite band 0–0.5 vs the 0.9 open baseline is fully
  out-of-band at init; without the floor `speedMult → 0` and every non-firing termite freezes —
  and frozen termites can't build the walls that would put them in-band (deadlock; observed as
  corner-clustering + only firing agents visible).
- Habitat is **emergent, not instant**: at the uniform-open start only Boids are home; Physarum/
  Termite territory appears as walls form (succession). Optional per-run seed bootstrap deferred.
- `noiseScale`/`noiseThreshold` no longer affect permeability (still set from C#, harmless).
- Curvature (Laplacian) and humidity-scaffold build cues remain deferred (spec non-goals).

## Related
[[../ARCHITECTURE]] §3.3 (channels), §3.4 (TermiteSim) ·
[[../sessions/2026-07-15-permeability-mounds]] ·
[[../superpowers/specs/2026-07-14-permeability-mounds-design]] (spec) ·
[[../superpowers/plans/2026-07-14-permeability-mounds]] (plan) ·
[[0007-mass-conserving-diffusion-relax-channels]] (relax-to-baseline channels)
