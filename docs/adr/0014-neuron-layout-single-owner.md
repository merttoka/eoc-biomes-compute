---
status: accepted
date: 2026-08-12
tags: [adr, neuron, layout, engine-core, single-owner, dataset]
related: [[../superpowers/specs/2026-08-12-neuron-positions-single-owner-design]], [[../superpowers/specs/2026-08-02-neuron-layout-single-owner-design]], [[0006-osc-neuron-firing]], [[../ARCHITECTURE]]
---
# ADR-0014: The neuron layout has a single owner — `NeuronFiringSource`

## Context

The neuron layout (positions CSV + spawn scale) was declared redundantly across components.
The 2026-08-02 spec collapsed `spawnScale` to `NeuronFiringSource` and promised this ADR,
but justified itself with a claim that was not yet true: sims still declared and parsed
their own `labelsPositionsCsv` per instance, and the source's own copy carried the tooltip
"Same labels_positions.csv as the sims" — the exact hand-maintained invariant that had
already desynced `spawnScale` in two scenes. Dead weight rode along: a
`csvCoordinatesAreNormalized` flag that never changed behavior (auto-detect covered it) and
a `PositionsBuffer` with zero consumers. Two organoid datasets with different durations and
neuron counts are expected soon, which would have multiplied every per-sim copy into a
migration hazard.

Options: (a) keep per-sim CSVs and document the invariant — rejected, it is the defect
pattern; (b) sims read the source directly — rejected, inverts the dependency direction;
(c) the manager pushes, sims consume — matches the `neuronSpawnScale` precedent.

## Decision

`NeuronFiringSource` owns everything about the neuron layout: the positions CSV (parsed
once per load, decoupled from the firing-blob guard so a blob-less instance works), the
spawn scale, and the firing blob. `SimulationManager.ConfigureSim` pushes `PositionsCPU`
to every sim before `Reset()` with a reference-identity check that invalidates the sim's
cached upload on swap. Sims hold only a `[NonSerialized]` normalized list and convert to
their own pixel space at upload. The parse site validates the contract: coordinates
normalized 0..1 (warn per load if violated) and `PositionsCount == NeuronCount` when a blob
is present (warn per load — row *k* = neuron *k* is ADR-0006's spatial-coherence property).
`csvCoordinatesAreNormalized`, `LooksNormalized01`, and `PositionsBuffer` are deleted.

## Consequences

- A dataset swap is two fields on one component per scene; every consumer follows at the
  next configure/reset. Per-recording `FrameActivity` normalization and blob-local duration
  already adapt, so multi-dataset use needs no further plumbing for switching.
- Every scene now needs a `NeuronFiringSource` for neuron-anchored seeding — a scene
  without one gets random scatter, dead CA ignition, and a one-time console warning
  (11.0 TestScene gained a blob-less instance for exactly this).
- Known asymmetry, documented not fixed: per-type reset buttons (`ResetSimsOnly` etc.) skip
  `neuronFiring.Initialize()`, so a mid-session CSV swap propagates on the next full
  `Reset()` — same as the firing blob.
- Outgrowth points, designed when the second dataset arrives: an explicit `NeuronDataset`
  pairing asset (blob + CSV); `TermiteSim`'s serialized 1:1 agent count (131) is not
  derived from the source; concurrent per-sim source routing is now a manager-level change.

## Related

[[../superpowers/specs/2026-08-12-neuron-positions-single-owner-design]] (design + audit),
[[../superpowers/specs/2026-08-02-neuron-layout-single-owner-design]] (spawnScale precedent),
[[0006-osc-neuron-firing]] (the coherence property this preserves).
