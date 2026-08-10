---
status: closed
date: 2026-08-10
tags: [session, biome, sims, pde, diffusion]
related: [[../adr/0012-weighted-diffusion-kernels]], [[../adr/0013-coherence-enhancing-trail-diffusion]], [[../adr/0007-mass-conserving-diffusion-relax-channels]], [[../superpowers/specs/2026-08-10-anisotropic-diffusion-design]]
---
# Anisotropic / non-uniform diffusion kernels

Worktree `worktree-diffusion-kernels` off `ca-dev` (7b0b44b); parallel agent active on the
main `ca-dev` checkout (CA sims) — no file overlap.

## Shipped

- `Biome.compute` — `DiffuseFieldsKernel` 3×3 box → normalized weighted average; per-cell
  tap data (clamped Loads, flow alignment, interface permeability) hoisted out of the
  channel loop. New `channelKernel` buffer + `windX/windY` uniforms; `GenerateFlowKernel`
  adds ambient wind into the flow accumulation.
- `BiomeFieldConfig.cs` — per-channel `kernelShape` (Box/Gaussian), `flowAnisotropy`,
  `permeabilityInfluence`; config-level `ambientWind` (Vector2). All defaults inert.
- `Biome.cs` — `channelKernelBuffer` alloc/pack/bind/release beside the existing channel
  buffers; wind uniforms set per Step.
- Spec: [[../superpowers/specs/2026-08-10-anisotropic-diffusion-design]].
- Numerical verification (JS replica of the kernel): defaults == legacy (0.0 diff);
  uniform fixed point ~1e-16; aniso var-ratio 2.0 along flow with zero centroid drift;
  perm-0 wall passes exactly 0; gather≠scatter mass error +0.2%/60 steps; gaussian
  corner/edge 0.5 vs box 1.0.

## Shipped (part 2, same session)

- FIX: biome diffuse taps wrap toroidally (`% rez`), not clamp — GPUResourceManager
  Repeat-wraps every texture, so the legacy SampleLevel blur wrapped at edges; the
  first cut of the weighted kernel silently changed that.
- Per-config values applied to 11.3 DAC `BiomeFieldConfig_Homeostatic.asset`
  (pheromones aniso 0.7 / permInfluence 0.9 / gaussian; wind (0.03, 0); full table in
  the asset). 11.1/11.2 left stock deliberately.
- Coherence-enhancing anisotropic trail diffusion in all three agent sims
  (`PhysarumSim/BoidSim/TermiteSim.compute` `DiffuseTextureKernel` + one
  `trailAnisotropy` knob on `SimulationBase`, default 0 = legacy): structure tensor of
  the total trail orients the blur along the ridge → slow boids leave comet tails, not
  wide cones. Two heading-field designs built first, measured useless, killed →
  [[../adr/0013-coherence-enhancing-trail-diffusion|ADR-0013]].
- Measured (JS kernel replica): trail σ⊥ 3.55→1.95 px at aniso 1; stationary blob
  aspect exactly 1.0; aniso 0 bit-parity with legacy box blur incl. toroidal edges.

## Decided

- Weighted gather over flux-form tensor diffusion; ADR-0007 downstream untouched →
  [[../adr/0012-weighted-diffusion-kernels|ADR-0012]].
- Anisotropy symmetric up/downwind ((ô·f̂)²); drift stays advection's job.
- Interface permeability = `min(here, there)`; wind is the only mean-nonzero flow source.
- Sim trail anisotropy from the trail's own structure tensor, NOT a deposited heading
  field — two heading-field variants measurably did nothing (decay-timescale mismatch;
  receiving-cell problem). [[../adr/0013-coherence-enhancing-trail-diffusion|ADR-0013]].

## Open / next session

1. In-editor validation: shader + C# compile, then visual pass — 11.3 DAC config ships
   live values (pheromone aniso/permInfluence + wind); flip each sim's
   `trailAnisotropy` to ~0.6 and watch boid trails.
2. Apply the per-channel table to 11.1/11.2 configs if 11.3 looks right.
3. Watch trail-following feedback: sharper ridges strengthen SensorTurns gradients.
4. Not merged: README/ARCHITECTURE untouched per convention (update on merge to main).
5. EditMode tests not run in worktree (second Unity instance vs open editor); run before
   merge. Merge waits for the other agent's CA work to land on ca-dev.
