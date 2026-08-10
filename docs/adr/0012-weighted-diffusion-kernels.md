---
status: accepted
date: 2026-08-10
tags: [adr, biome, pde, diffusion, anisotropy]
related: [[0007-mass-conserving-diffusion-relax-channels]], [[0010-permeability-agent-built-topography]], [[../sessions/2026-08-10-anisotropic-diffusion-kernels]]
---
# ADR-0012: diffusion neighbourhood is a shaped weighted average (flow anisotropy + permeability gating), not a fixed box

## Context

`DiffuseFieldsKernel` blurred every channel with an isotropic 3×3 box: mass spreads
identically in all directions regardless of wind or medium. The biome models the advection
term of ∂c/∂t = ∇·(D∇c) − ∇·(vc) (flow + semi-Lagrangian transport) but D was a scalar.
Two physical effects were unrepresentable: turbulent eddy diffusivity (plumes stretch
*along* wind, symmetrically — distinct from being *carried* by it) and porous media
(ADR-0010's termite walls had zero effect on chemical transport — scent passed through
built topography).

Options: (a) alternate fixed stencils only — no flow/medium coupling; (b) normalized
weighted gather, ADR-0007 semantics untouched downstream; (c) flux-form tensor diffusion —
exactly mass-conserving but rewrites ADR-0007 and re-tunes every scene.

## Decision

(b). The box average becomes a normalized weighted average; per-channel `channelKernel`
rows (`BiomeFieldConfig`: `kernelShape`, `flowAnisotropy`, `permeabilityInfluence`) shape
the weights:

```
w(o) = base(o) · lerp(1, (ô·f̂)², flowAnisotropy·sat|flow|) · lerp(1, min(perm_c, perm_n), permInfluence)
avg  = Σ w·v / Σ w
```

`base` morphs box→gaussian (1,2,1⊗1,2,1). Downstream (relax-vs-stigmergic gating, decay,
relax) is byte-identical to ADR-0007. `ambientWind` (config Vector2) adds into the flow
accumulation each step — the one deliberate mean-nonzero flow source (equilibrium ≈ 10×
per-step add; ±0.02..0.1 useful).

## Consequences

- All-zero rows = legacy box blur (verified: 0.0 max diff vs legacy in numerical replica);
  existing assets deserialize to defaults → no scene changes.
- Normalized weights keep the uniform-field fixed point (dev ~1e-16), so ADR-0007's
  relax-channel equilibrium == baseline survives every knob.
- Exact **global** mass conservation is lost when weights vary spatially (gather ≠ scatter;
  ~+0.2% over 60 steps in tests). Accepted: stigmergic channels leak by design, relax
  channels are baseline-pinned. If a future channel needs exact conservation under
  gating, that's option (c) territory.
- Anisotropy is symmetric up/downwind by construction ((ô·f̂)²) — drift stays advection's
  job; the two transport terms remain separately tunable.
- `permeabilityInfluence` 1 makes mounds scent containers (interface `min` ⇒ perm-0 walls
  pass exactly zero); sealed cells keep their load until decay. ADR-0010's topography now
  shapes chemistry, not just locomotion.
- Sim-local trail blurs (Physarum/Termite `DiffuseTextureKernel`) still box — out of scope.

## Related

[[../superpowers/specs/2026-08-10-anisotropic-diffusion-design]] — kernel math, invariants, numerical verification.
