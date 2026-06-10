---
status: closed
date: 2026-06-10
tags: [session, biome, boid, review, exhibition]
related: [[../adr/0007-mass-conserving-diffusion-relax-channels]], [[2026-06-09-ecosystem-io-investigation]], [[../../Assets/Workspace/11.0 Biomes/docs/PERFORMANCE]]
---
# Branch validation + homeostatic equilibrium fix

Tested `claude/biomes-11-performance-3aog3h` (perf second pass, injector calibration, mush fix). Editor compile clean. 15-agent adversarial review of the diff: 9 confirmed findings (0 refuted), 9 low-severity. Fixed the worst live; merged to main.

## Shipped
- **Mass-conserving diffusion for relax channels** (`Biome.compute` DiffuseFieldsKernel) — equilibrium now == baseline (O₂ 0.8, Temp 0.5) instead of ~33% under. → [[../adr/0007-mass-conserving-diffusion-relax-channels]]
- **Homeostatic asset retuned** — Temp decay 0, decompositionTempSpan 2, flow 0.6 / perm 0.2, noiseThreshold 0.212.
- **Boid interaction ranges rescaled 0–64** (OSC ranges + per-type values ×64/500) across `BoidParams` + 2 snapshots — enforces the ≤64 px quadratic-cost rule.
- **`ExternalTextureSender.ChannelNames` +Pheromone_2** (was 9 names / 10 channels; handoff item #3 from previous session).
- **`Scene_CURRENTS` adopts homeostatic config** + 640 px PDE, stepEvery 2, perceptionResScale 0.25, renderPersistence serialized, injector calibration fields.

## Decided
- Diffusion operator gated per channel class (relax → conserving, stigmergic → leaky/evaporating) → [[../adr/0007-mass-conserving-diffusion-relax-channels]].
- Show scene deliberately runs the new Q10 curve at span 2 (defuses the silent-default finding for `Scene_CURRENTS`; TestScene's old assets still get span 4 silently).

## Review findings — confirmed, still open
1. Boid `agentsCount` live slider overruns GPU buffers (sized at Reset only; 150 k cap widens exposure) — snapshot `_allocatedAgents` at Reset.
2. Ring overlay freezes at last intensity if `NeuronFiringSource` disabled mid-fire (dropped null-guards; `_scaled` never cleared in ReleaseBuffers).
3. One NaN OSC packet permanently latches an injector source to NaN, even at smoothing 0 — needs `IsFinite` ingress guard.
4. Injector EMA + timeout decay are per-sim-step → smoothing varies with fps/stepsPerFrame/stepMod; make dt-based.
5. `metabolismEvery` "flux-conserving" false near [0,1] clamp in saturated cells — fix wording or use unclamped accumulator.
6. `decompositionTempSpan = 4f` default silently enables Q10 for pre-existing config assets (TestScene) — default 0 + gate legacy curve.
7. Nine low-severity footnotes (full list: review workflow output, session 2026-06-10).

## Open / next session
1. Apply fixes 1–6 above (each is small + surgical; 3 and 1 first — install-killers).
2. Validate homeostatic look long-run at 640 px PDE (equilibrium hold, Q10 fronts at span 2, dig healing).
3. Injector click-to-place + texture-valued source (carried over).
4. Neuron disruption routing aesthetic call (carried over).
