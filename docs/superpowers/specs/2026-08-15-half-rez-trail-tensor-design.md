# Half-Rez Trail Structure Tensor — Design

**Date:** 2026-08-15
**Status:** designed, unimplemented — backlog (implement when a scene with `trailAnisotropy > 0` measurably needs the headroom)
**Files touched (when built):** new `src/computes/includes/trail_tensor.hlsl`; `src/computes/PhysarumSim.compute` / `BoidSim.compute` / `TermiteSim.compute`; `src/components/core/SimulationBase.cs`; possibly `GPUResourceManager.cs`

## Problem

With `trailAnisotropy > 0`, `DiffuseTextureKernel` pays 25 texture loads + an
eigen-decomposition **per full-res pixel per sim step** to derive two numbers — local ridge
axis and coherence. That cost caused the 2026-08-15 fps collapse in scenes running the knob
at 0 (fixed by the uniform early-out, `98926dd`); scenes running it ON (11.3 DAC:
physarum 0.2, termite 0.3, boid 1.0) still pay full price by design.

The tensor field is low-frequency **by construction**: the 5×5 window samples at a
~3–5-texel stride (support ≈ 13–21 texels), so neighboring pixels compute nearly identical
tensors. Full-res evaluation oversamples a smooth field.

## Design

Split the kernel in two:

1. **`TrailTensorKernel`** (new, per compute asset; shared math in
   `includes/trail_tensor.hlsl` — the block is byte-identical across the three sims today,
   so this deduplicates it): dispatched at `rezX/2 × rezY/2`, runs the existing 25-tap
   window + gradients + eigen, writes the result to a half-rez RGHalf texture. Quarter the
   pixels → the expensive part costs ~¼ of today.
2. **`DiffuseTextureKernel`** (existing, full res): replaces its tensor section with ONE
   bilinear sample of that texture; blur weights as before.

**The load-bearing subtlety — double-angle encoding.** The ridge is a sign-free AXIS;
naively interpolating axis vectors lets opposite signs cancel at boundaries. Store
`(cos 2θ, sin 2θ) · coherence` instead — double-angle vectors interpolate correctly for
axes, and the weight needs only `cos²(θtap − θridge) = (1 + R₂·D₂)/2` where the 8 tap
directions' double-angle vectors are compile-time constants: no per-pixel trig or eigen at
full res at all. Graceful degradation is built in: where orientations disagree (crossings),
interpolation shrinks the vector's magnitude → elongation fades → isotropic — exactly what
coherence gating wants there anyway.

`SimulationBase` gains: the half-rez RT (allocate/release), kernel handle + property IDs,
binds, and one extra dispatch per step **gated on `trailAnisotropy > 0`** — knob-off scenes
stay exactly as free as they are after `98926dd`.

## Expected effect

Anisotropy overhead shrinks ~3–4× (tensor at ¼ pixels; full-res pass drops from
25 loads + eigen to 1 sample + a few dots). Visual delta ≈ none: the orientation field is
already low-passed by the stride window; half-rez sampling softens orientation boundaries
slightly, where coherence is lowest anyway.

Cheaper cousin considered and parked: temporal amortization (tensor every other frame) —
trades lag on fast-moving trails for savings; half-rez is the better default.

## Preconditions before building

1. Measure first (project charter): Profiler GPU ms in 11.3 with the three knobs on vs 0.
   Build only if the delta matters to the show.
2. ADR-0013's contract (one `trailAnisotropy` knob per sim, orientation from the trail's
   own tensor) is unchanged — only the evaluation resolution moves. Note on the ADR when
   shipped, no supersede needed.

## Success criteria (when built)

1. Bit-comparable look at knob 0 (early-out path untouched) and no visible regression at
   knob 1 on boid trails (the sharpest case).
2. Profiler: DiffuseTextureKernel + TrailTensorKernel combined ≤ ~⅓ of today's
   knob-on kernel time at 11.3's resolutions.
3. The tensor block exists once (`trail_tensor.hlsl`), not three times.
