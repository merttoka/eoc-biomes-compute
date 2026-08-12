---
status: closed
date: 2026-08-12
tags: [session, refactor, neuron, layout, dataset]
related: [[../adr/0014-neuron-layout-single-owner]], [[../superpowers/specs/2026-08-12-neuron-positions-single-owner-design]], [[2026-08-10-event-driven-ca]]
---
# Neuron positions get a single owner

## Shipped

- Audit answered "do we still need per-sim Label Position CSVs?": needed *then* (sole source
  of agent spawn seeding + CA ignition positions; sims never received positions from
  `NeuronFiringSource`), redundant *by design* — the 2026-08-02 single-owner spec's claim
  that the source "already owns the CSV" was never true for sims.
- Spec: `superpowers/specs/2026-08-12-neuron-positions-single-owner-design.md`.
- Refactor (`2b940f1` + polish `72d4dd9`): `NeuronFiringSource` is the only CSV holder
  (`LoadPositions` decoupled from the blob guard, absorbed `ParseCsvFloat2`, per-load
  contract + dataset-pairing warnings); `SimulationManager.ConfigureSim` pushes
  `PositionsCPU` before `Reset()` (identity check + `InvalidateNeuronPositions`);
  `SimulationBase` loses `labelsPositionsCsv` / `csvCoordinatesAreNormalized` /
  `LooksNormalized01`; dead `PositionsBuffer` / `_posBuffer` deleted.
- 11.0 TestScene gained a blob-less `NeuronFiringSource` (it had no source at all).
- User-verified: compile clean, 56/56 EditMode, DAC scene spawning/ignition unchanged.
- [[../adr/0014-neuron-layout-single-owner|ADR-0014]] — the ADR the 2026-08-02 spec promised.

## Decided

- Manager pushes, sims consume (mirrors `neuronSpawnScale`) — dependency direction unchanged.
- Contract hardened at the single parse site: normalized 0..1, and `PositionsCount ==
  NeuronCount` when a blob is loaded; warnings reset per load so a second bad dataset still
  announces itself.
- Scene without a source = random scatter + dead ignition + one warning (acceptable, explicit).

## Measured

- Perf: steady state byte-identical (same buffers/binds, built-guard keeps per-step calls
  bind-only); init parses once per source load instead of once per sim; −1 dead GPU buffer.

## Open / next session

1. 11.1 / 11.2 scenes: user Play-checks pending ("later"); open + save each to shed stale
   CSV keys from the YAML.
2. Second organoid dataset: when it lands, consider the `NeuronDataset` pairing asset and
   the `TermiteSim` 1:1 agent-count question (spec §Datasets).
3. Carried from [[2026-08-10-event-driven-ca]]: dedicated `TriggerBurst` guard test (needs
   Play-mode or logic extraction); CA look/aesthetics tuning ongoing.
