---
status: accepted
date: 2026-09-08
tags: [adr, neurons, rendering, cleanup]
related: [[../ARCHITECTURE]], [[0006-osc-neuron-firing]], [[../sessions/2026-09-08-video-to-channels-firing-playback]]
supersedes: ADR-0006 (ring-overlay clause only)
---
# ADR-0015: Remove the firing-ring composite overlay

## Context
[[0006-osc-neuron-firing|ADR-0006]] added `NeuronRingKernel`: one count-independent ring per firing
neuron drawn over the composite, because the additive composite saturated under physarum's firing and
hid termite/boid firing. Since June the firing signal reaches the picture through paths that live
*inside* the ecology rather than on top of it: per-sim `firingSpeedMul` / `firingDepositAmount`, the
injector's firing-driven Dispersal pulses at neuron or agent positions, firing-gated termite mound
building. Every show scene (CURRENTS, SIGGRAPH, SIGGRAPH DAC, the ten `DAC_params` copies) had the
overlay off; `PERFORMANCE.md` §10 already noted the rings "don't sit well on the evolved composite".
Off, it cost one bool check per frame — but it left eight fields in the `SimulationManager`
inspector and ~120 lines across two files.

Options: keep (zero cost off) · repurpose as a separate infographic texture for TD (the §10
candidate) · delete.

## Decision
Delete. Inspector fields, shader property IDs, compact buffers + CPU caches, the `Render()` block,
and the kernel + uniforms are gone; `SimulationManager.compute` no longer includes
`neuron_layout.hlsl`. `NeuronFiringSource.ScaledValues` / `PositionsCPU` stay — `BiomeInjector`
reads them for firing dispersal.

## Consequences
- Inspector loses the "Neuron Firing Ring Overlay" header. Scenes drop the stale `m_Ring*` keys on
  their next save; no scene needs re-authoring (the toggle was already 0 everywhere but TestScene).
- Firing legibility is the ecology's job (dispersal pulses, deposits, mounds). If a count-independent
  readout is wanted again, build it as a separate small texture sent to TD — not on the composite.
- ROADMAP "trails → separate overlaid composite channel" now models on `MoundOverlayKernel`.
- Recoverable from git: last commit carrying the kernel is `7588c5d`.

## Related
[[0006-osc-neuron-firing]] · [[../ARCHITECTURE]] · `Assets/Workspace/11.0 Biomes/docs/PERFORMANCE.md` §10
