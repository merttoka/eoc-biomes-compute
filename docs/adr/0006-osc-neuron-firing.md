---
status: accepted
date: 2026-06-09
tags: [adr, neurons, firing, osc, rendering]
related: [[../ARCHITECTURE]], [[../sessions/2026-06-09-osc-neuron-firing]], [[../superpowers/specs/2026-06-08-osc-neuron-firing-design]]
---
# ADR-0006: Neuron firing is an external OSC-driven signal shared by all sims

## Context
Firing was termite-only: an internal playhead auto-advanced through a 47 MB float16 blob
(`termite_firing.f16`, 131 neurons × 180000 frames), and only termites reacted (2× speed +
bright trails). The system is meant to be *externally driven* (`docs/RESEARCH_BRIEF.md` §4.4),
and we wanted (a) the playhead controlled by another patch, and (b) firing to reach physarum +
boid too.

Options weighed:
- **Index semantics:** frame-index scrub (file = values, OSC = playhead) · live neuron-spike
  events · full 131-value array per message.
- **Coupling:** direct in-shader excitation · indirect via the biome field (chemotaxis) · both.
- **Ownership:** keep per-sim copies · hoist to `SimulationBase`.
- **Idle behavior:** hold last frame · auto-advance fallback · hold + decay to quiet.

## Decision
- **`/index <int>` scrubs the precomputed blob** — file stays the firing-value source, OSC is
  only the playhead. No auto-advance. **Hold + decay to quiet** when silent (`firingDecaySeconds`,
  default 0.5 s).
- **`NeuronFiringSource`** (component on `SimulationManager`, like `BiomeInjector`) owns the blob,
  the OSC frame index (thread-safe `SetFrame`), and the decay envelope; each step it produces a
  shared **131-float buffer** (row × intensity). The manager broadcasts it to every sim.
- **Firing consumption + neuron-position seeding hoisted to `SimulationBase`.** All three sims read
  `firing[agent % neuronCount]` in-shader (`computes/includes/neuron_firing.hlsl` → `IsFiring`) and
  apply `firingSpeedMul` + `firingDepositAmount`. **Direct, threshold-based excitation.** Boid gains
  neuron-position seeding; termite drops its private blob path.
- **Count-independent ring overlay** in the composite (`NeuronRingKernel`): one soft ring per firing
  neuron drawn on top of the final composite, brightness/radius ∝ firing intensity.

## Consequences
- Firing is now a first-class external input; one `/index` from another patch drives a single neural
  "body" expressed three ways. Termite no longer special-cases the blob.
- **Spatial coherence for free:** `labels_positions.csv` row *k* = blob neuron *k* = each sim's
  `agent i%131` seed position, so a firing neuron excites the agents physically on it in every biome.
- Seeding deduped out of termite + physarum into the base (boid inherits it). Physarum's `neuronScale`
  field → base `spawnScale` (re-set per instance).
- **Why the ring overlay exists:** the composite is a pure additive sum then `saturate()`; physarum's
  ~2300 firing agents/neuron clip large areas to white, so termite (~100) and boid (~76) firing was
  mathematically invisible. The ring overlay keys off the shared firing intensity directly (not agent
  counts), making firing legible regardless of per-sim density. Per-sim agent-trail firing still runs
  underneath; the rings are the readout layer.
- Indirect (biome-field) coupling and graded (non-threshold) response were explicitly **not** taken.

## Related
[[../superpowers/specs/2026-06-08-osc-neuron-firing-design]] · [[../superpowers/plans/2026-06-08-osc-neuron-firing]] ·
[[../sessions/2026-06-09-osc-neuron-firing]] · code: `components/network/NeuronFiringSource.cs`,
`components/core/SimulationBase.cs`, `computes/includes/neuron_firing.hlsl`, `computes/SimulationManager.compute`.
