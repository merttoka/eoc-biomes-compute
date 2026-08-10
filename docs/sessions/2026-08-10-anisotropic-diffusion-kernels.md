---
status: closed
date: 2026-08-10
tags: [session, biome, pde, diffusion]
related: [[../adr/0012-weighted-diffusion-kernels]], [[../adr/0007-mass-conserving-diffusion-relax-channels]], [[../superpowers/specs/2026-08-10-anisotropic-diffusion-design]]
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

## Decided

- Weighted gather over flux-form tensor diffusion; ADR-0007 downstream untouched →
  [[../adr/0012-weighted-diffusion-kernels|ADR-0012]].
- Anisotropy symmetric up/downwind ((ô·f̂)²); drift stays advection's job.
- Interface permeability = `min(here, there)`; wind is the only mean-nonzero flow source.
- Sim trail blurs (Physarum/Termite) out of scope until biome-level results earn it.

## Open / next session

1. In-editor validation: shader + C# compile, then visual pass — suggest Pheromone_0
   `flowAnisotropy` 1 + `ambientWind` (0.05, 0) first, then `permeabilityInfluence` 1 on
   pheromones in the termite scene (mounds as scent containers).
2. Tune which channels get which knobs per scene asset; defaults ship inert.
3. Not merged: README/ARCHITECTURE untouched per convention (update on merge to main).
4. EditMode tests not run in worktree (second Unity instance vs open editor); run before
   merge.
