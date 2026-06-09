---
status: closed
date: 2026-06-09
tags: [session, neurons, firing, osc, rendering]
related: [[../ARCHITECTURE]], [[../adr/0006-osc-neuron-firing]], [[../superpowers/specs/2026-06-08-osc-neuron-firing-design]]
---
# OSC-driven shared neuron firing

Branch `feat/osc-neuron-firing`. Spec → plan → 9 tasks + ring overlay, each verified live via OSC.

## Shipped
- **`NeuronFiringSource`** (`components/network/`) — owns the firing blob + OSC frame index + decay
  envelope + neuron positions; emits a shared 131-float buffer (firing) and a positions buffer.
- **`SimulationBase`** — shared firing consumption (`neuronFiring`/`firingThreshold`/`BindNeuronFiring`)
  + hoisted neuron-position seeding (`BuildNeuronPositions`, `ParseCsvFloat2`) once for all sims.
- **`computes/includes/neuron_firing.hlsl`** — `NeuronFireValue`/`IsFiring(agentId, neuronCount)`.
- **Termite** drops its private blob/playback; reads the shared buffer in-shader (behavior preserved).
- **Boid** gains neuron-position seeding + `firingSpeedMul`/`firingDepositAmount` (speed burst + brighter deposit).
- **Physarum** gains `firingSpeedMul`/`firingDepositAmount`.
- **OSC** `/index <int>` → `NeuronFiringSource.SetFrame` (`OSCMapping.cs`).
- **Manager** owns + drives + broadcasts the source each step (`SimulationManager.cs`).
- **Ring overlay** — `NeuronRingKernel` in `SimulationManager.compute`: count-independent ring per
  firing neuron on top of the composite; tunable (color/radius/thickness/strength/threshold/scale).
- Preset firing values tuned; `firingDepositAmount` default → 1.0 (physarum/boid). Scene wired.

## Decided
- See [[../adr/0006-osc-neuron-firing]] — frame-index scrub, hold+decay-to-quiet, direct excitation,
  seeding/firing hoisted to base, count-independent ring overlay for legibility.

## Open / next session
1. **Firing visual balance / "strategies"** — physarum density still dominates agent-trail firing;
   the ring overlay is the legibility layer for now. Consider boid trail-brightening on fire (boid
   firing only affects speed/deposit ≤1, not trail intensity), per-sim composite weights, or firing
   probability on physarum to thin its footprint.
2. **OSC `/index` contract** — finalize from the driving patch (int frame). Map its index → 0..179999.
3. **Benign warning** — `SimulationManager.compute` Metal warning on `SampleSim` (pre-existing,
   uninitialized-path); silence with a default init if desired.
4. **Uncommitted `Biome.cs`** — modified outside this feature; left unstaged. Decide separately.
