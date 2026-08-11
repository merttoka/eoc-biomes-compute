---
status: accepted
date: 2026-08-10
tags: [adr, sims, trails, diffusion, anisotropy]
related: [[0012-weighted-diffusion-kernels]], [[../sessions/2026-08-10-anisotropic-diffusion-kernels]]
---
# ADR-0013: sim trail anisotropy comes from the trail's own structure tensor, not a heading-memory field

## Context

The three agent sims (Physarum, Boid, Termite) blur their trail textures with the same
isotropic 3×3 box the biome used. A slow, small flock reads as a wide cone: isotropic
blur spreads ∝ √steps regardless of speed, so a slow agent's short path drowns in its
own blur radius. ADR-0012's biome fix elongates along the *flow field*; sims have no
flow at trail resolution, so the diffusion needs a different orientation source.

Two designs were built and killed by measurement (JS replica of the kernels):
1. **Deposited heading field** (agents write normalize(velocity) at their cell, decayed
   per step): no measurable effect. Under a normalized gather, sideways leak stops only
   when the RECEIVING cell knows the trail direction — heading confined to the 1-px
   agent path leaves the flanks isotropic.
2. **Blurred heading field riding the trail ping-pong** (extra dirX/dirY layers): still
   no effect — the memory (persistence^k · blur dilution) dies ~50 steps before the
   trail it should shape; matching two decay timescales against an exponential is
   fragile by construction.

## Decision

Coherence-enhancing diffusion (Weickert): each cell computes the structure tensor of
the TOTAL trail layer (3×3-averaged central-difference gradients over a 5×5 sample
window) and elongates every type's blur along the ridge axis (minor eigenvector),
gated by coherence = (λ₁−λ₂)/(λ₁+λ₂) (linear — the squared gate tested weaker
in-editor), with a tight angular lobe (alignment⁴, halving the diagonal side-leak of
alignment²). The tensor window's texel spacing is resolution-scaled
(`trailTensorStride` = 3 · rezY/2160, min 1) like every other spatial param: a fixed
5-texel window inside a wide trail's flat core reads "no orientation" and mutes the
effect at production rez. One per-sim knob: `trailAnisotropy` on `SimulationBase`
(default 0 = legacy box blur, bit-parity verified). No new textures, no deposit
changes, no extra layers.

## Consequences

- Orientation derives from the trail itself → it exists exactly where and as long as
  the trail does. Measured on the slow-agent case: trail width σ⊥ halves (3.55→1.81 px)
  and aspect rises 4.6→7.3 at full strength; stationary blobs stay exactly round at any
  stride; crossings de-anisotropize (coherence → 0). The 0.5 knob lands midway.
  In-editor (2026-08-10): physarum visibly stringier with the knob — confirmed live.
- Comet tails form by RETENTION (symmetric ridge axis), never drift — agent motion
  stays the only transport, mirroring ADR-0012's advection/diffusion split.
- Orientation is shared across types (total layer): where two species' trails overlap
  spatially, coherence drops toward isotropic — acceptable (crossings are hubs);
  per-type tensors would cost 25 loads × typeCount.
- Sharpened trails hold less total mass (weighted mean + evaporation leak): crisper,
  dimmer halos. `diffuseRate` still owns evaporation.
- Cost: +25 point-filtered loads of one layer + 2×2 eigen algebra per cell, per sim step.
- Trail sensing (SensorTurns / food-seek) is untouched; agents now follow crisper
  gradients, which mildly strengthens trail-following feedback — watch in-editor.

## Related

[[../superpowers/specs/2026-08-10-anisotropic-diffusion-design]] — measurements, killed designs, kernel math.
