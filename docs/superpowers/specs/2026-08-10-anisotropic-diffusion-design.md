# Anisotropic / Non-Uniform Diffusion Kernels — Design

**Date:** 2026-08-10
**Status:** implemented on `worktree-diffusion-kernels` (off `ca-dev`); pending in-editor validation
**Files touched:** `Assets/Workspace/11.0 Biomes/src/computes/Biome.compute`, `.../core/Biome.cs`, `.../core/BiomeFieldConfig.cs`

## Problem

`DiffuseFieldsKernel` (Biome.compute) spreads every chemical channel with an
isotropic 3×3 box blur. Mass moves equally in all directions no matter what the
medium or the wind is doing. The biome already models the *other* transport
term — `GenerateFlowKernel` + `AdvectFieldsKernel` implement bulk transport
∇·(vc) — but the diffusion tensor D in ∂c/∂t = ∇·(D∇c) − ∇·(vc) is a scalar
constant. This work makes D non-uniform.

## Physical readings of non-uniform diffusion

1. **Anisotropic (flow-aligned) D — turbulent eddy diffusivity.** In a wind,
   scent doesn't just get carried downwind (advection); shear turbulence also
   *spreads* it faster along the flow axis than across it. Plumes are cigars,
   not discs. Model: elongate the kernel along the local flow vector,
   proportional to flow speed. Note the tensor is symmetric — it spreads both
   up- and downwind. The asymmetric "carried downwind" part is advection's job
   and already exists; conflating the two is the classic mistake.
2. **Spatially-varying isotropic D — porous media.** Scent moves freely in open
   air, slowly through soil/clay. The biome literally has this medium map:
   `CH_PERMEABILITY` (termite-built walls, ADR-0010). Gating neighbor exchange
   by interface permeability turns mounds into scent containers: pheromone
   pools inside chambers, corridors duct it, walls cast scent shadows. Strong
   stigmergic consequence for free.
3. **Kernel shape.** The 3×3 box is itself anisotropic in a bad way (diagonal
   bias — the box's corners are √2 away but weighted like the edges). A
   Gaussian (1,2,1)⊗(1,2,1)/16 stencil is closer to rotationally symmetric.
   Offering the shape as a knob makes the "uniform" baseline more honest.

Plus one source term: **ambient wind** — a constant vector added into the flow
accumulation each step, giving the field a prevailing wind that both advects
(existing kernel) and now anisotropically diffuses chemicals.

## Decision

Generalize the box average in `DiffuseFieldsKernel` to a **normalized weighted
average**; keep everything downstream (ADR-0007 two-class semantics, decay,
relax) byte-identical. Per 3×3 offset `o`, per channel `c`:

```
w(o) = base(o) · lerp(1, align(o), e_c) · lerp(1, perm(o), q_c)

base(o)  = 1                      (Box)  or  {4,2,1} / center,edge,corner (Gaussian)
align(o) = (normalize(o) · f̂)²    center tap: 1 (never penalized)
e_c      = flowAnisotropy_c · saturate(|flow|)
perm(o)  = min(perm_center, perm_neighbor)   (interface permeability)
q_c      = permeabilityInfluence_c

avg = Σ w(o)·v(o) / Σ w(o)
```

Then, unchanged from ADR-0007:
- relax channels: `diffused = lerp(center, avg, diffuseRate)`
- stigmergic channels: `diffused = avg * diffuseRate` (leak = evaporation)

`GenerateFlowKernel` gains `+ float2(windX, windY)` in the flow accumulation.

## Invariants

- **Defaults are identity.** shape=Box, flowAnisotropy=0, permeabilityInfluence=0,
  ambientWind=0 → all w(o)=1 → identical to today's box blur. Existing scenes
  and assets (which lack the new serialized fields and get C# defaults) are
  untouched.
- **Uniform field maps to itself** (weights normalized) — the exact invariant
  ADR-0007 needs so relax-channel equilibrium == baseline survives any knob
  setting.
- **Exact global mass conservation is lost** when weights vary spatially
  (normalized gather ≠ scatter). Accepted: stigmergic channels leak by design
  (evaporation), relax channels are pinned to baseline. ADR amendment records
  this.
- CA-owned channels (Excitability, Substrate) keep diffuseRate 0 → early-skip
  path unchanged, the automaton stays sole owner.

## Plumbing

- `FieldChannelSettings` += `kernelShape` (enum Box=0/Gaussian=1),
  `flowAnisotropy` [0..1], `permeabilityInfluence` [0..1].
- `BiomeFieldConfig` += `ambientWind` (Vector2, per-axis ~[−0.2..0.2] useful
  range; flow equilibrium ≈ 10× the per-step add given flow's 0.92
  diffuse-leak · 0.98 decay, clamped to ±1).
- `Biome.cs`: pack the three new fields into a `channelKernel`
  `StructuredBuffer<float4>` (x=shape, y=aniso, z=permInfluence, w=reserved),
  allocated once beside `channelSettings`/`channelRelax`, re-uploaded in
  `UploadChannelSettings`, bound for the diffuse pass; `windX/windY` floats set
  before the flow pass.
- Kernel perf: flow vector, 9 permeability taps, alignment and interface terms
  are hoisted out of the per-channel loop (computed once per cell). Neighbor
  taps switch from bilinear `SampleLevel` at texel centers to TOROIDALLY WRAPPED
  integer `Load`s — same values: the field textures are Repeat-wrapped
  (GPUResourceManager), so the legacy blur wrapped at edges and modulo taps
  reproduce it exactly.

## Out of scope

- Sim-local trail blurs (`PhysarumSim`/`TermiteSim` `DiffuseTextureKernel`):
  same box-blur pattern, but their type-param structs have strict C#/HLSL
  stride coupling and no flow field at sim resolution; extend later if the
  biome-level results earn it.
- Flux-form tensor diffusion (exactly mass-conserving): rejected — rewrites
  ADR-0007 semantics and risks every tuned scene for rigor the art sim doesn't
  need.
- Scene/asset edits: none. Defaults are inert; knobs are flipped in the
  inspector per experiment.

## Addendum (same day): edges, config values, sim trails

- **Toroidal fix.** GPUResourceManager Repeat-wraps every texture — the legacy blur
  wrapped at edges, so the weighted kernel's taps wrap with integer modulo, not clamp.
- **Config values** applied to the 11.3 DAC `BiomeFieldConfig_Homeostatic` asset
  (pheromones 0.7/0.9/gaussian, wind (0.03, 0), full table in the asset); 11.1/11.2
  left stock until 11.3 validates.
- **Sim trails** (Physarum/Boid/Termite) got anisotropy from a different orientation
  source: the trail's own structure tensor (coherence-enhancing diffusion, ADR-0013).
  Two deposited-heading-field designs measured as no-ops first — orientation must
  exist at the RECEIVING cells and live as long as the trail; only the trail itself
  satisfies both. Single `trailAnisotropy` knob per sim, default 0 = legacy.
  Measured: σ⊥ halves at 1, stationary blobs stay round, crossings stay isotropic.

## Verification

Unity compile/tests deferred to the main checkout (second editor instance
would fight the open one). In-worktree: line-by-line back-compat review of the
kernel math (defaults → w≡1), C# pack order vs HLSL field order, buffer
lifetime through Allocate/Release.
