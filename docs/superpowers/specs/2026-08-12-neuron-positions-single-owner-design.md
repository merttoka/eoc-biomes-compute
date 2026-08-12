# Neuron Positions Get a Single Owner — Design

**Date:** 2026-08-12
**Status:** shipped on `ca-dev` 2026-08-12 (`2b940f1`, polish `72d4dd9`; ADR-0014); 11.1/11.2 Play-checks pending
**Files touched:** `src/components/network/NeuronFiringSource.cs`, `src/components/core/SimulationBase.cs`, `src/components/core/SimulationManager.cs`, all four scenes (11.0 TestScene gains a component; the others shed stale keys on save)

Finishes the job [[2026-08-02-neuron-layout-single-owner-design]] started. That spec collapsed
`spawnScale` to one owner and justified it by claiming `NeuronFiringSource` "already owns …
the CSV the positions are parsed from." For the sims, that was never true: every sim still
declares its own `labelsPositionsCsv` and parses its own copy. This spec makes the claim true
and deletes the dead weight found in the 2026-08-12 audit.

## Problem

- `SimulationBase.labelsPositionsCsv` is declared per sim and parsed per sim
  (`BuildNeuronPositions`, `SimulationBase.cs:466`). It feeds two load-bearing things: agent
  spawn seeding in all three agent sims (`idx = id.x % neuronCount` in their reset kernels —
  the spatial-coherence property of [[../../adr/0006-osc-neuron-firing|ADR-0006]]) and CA
  firing ignition (`cellular_common.hlsl:150` early-outs at `neuronCount == 0`).
- `NeuronFiringSource` carries its **own** `labelsPositionsCsv` whose tooltip says *"Same
  labels_positions.csv as the sims"* — the exact "match the X" defect the 2026-08-02 spec's
  follow-up told us to audit for. Every scene points every copy at the same asset
  (`data/labels_positions.csv`), so the duplication expresses nothing.
- Dead weight riding along: `csvCoordinatesAreNormalized` is set to 1 everywhere and
  `LooksNormalized01` auto-detects the same condition, so the flag never changes behavior;
  `NeuronFiringSource.PositionsBuffer` / `_posBuffer` have zero consumers repo-wide.

## Performance

**No negative impact; init strictly improves, steady state is byte-identical.**

- CSV parsing goes from once **per sim** per allocation (5 sims in 11.3) to once per source
  load. ~131 lines either way — microseconds; still, fewer of them.
- Steady state: the per-sim `neuronPositionsBuffer`, its binds, and the GPU-side structures
  are unchanged. `FieldSimulationBase` keeps its bind-only path per `GPUStep` (the
  `_neuronPositionsBuilt` guard already prevents re-parsing; that guard stays). The broadcast
  happens in `ConfigureSim` (configure/reset time), never per step.
- Memory: −1 dead GPU buffer (`_posBuffer`, 131×8 B) and −1 parsed CPU list per sim. Trivial,
  but the sign is negative, not positive.

## Design

Mirror the `neuronSpawnScale` precedent exactly: the source owns the layout, the manager
pushes it, sims consume without declaring.

### 1 · `NeuronFiringSource` — the only CSV holder

- `LoadPositions()` moves **out of the blob-reload guard** (`Initialize()`, line ~112).
  Positions load whenever the CSV reference changes and no longer require a firing blob —
  which is also what lets a blob-less TestScene instance work.
- Contract hardens: coordinates MUST be normalized 0..1. The loader warns once if any point
  falls outside `[-0.01, 1.01]` (replacing the sims' `LooksNormalized01` auto-detect with a
  validated contract at the single parse site).
- Dataset-pairing check: when both a blob and positions are loaded, warn once if
  `PositionsCount != NeuronCount` — row *k* = neuron *k* is the ADR-0006 coherence property,
  and a mismatched (blob, CSV) pair should announce itself the moment it is authored.
- `ParseCsvFloat2` moves here from `SimulationBase` (private — no other caller remains).
- `PositionsBuffer` and `_posBuffer` are deleted (zero consumers). `PositionsCPU` and
  `PositionsCount` stay — ring overlay and `BiomeInjector` already read them.

### 2 · `SimulationManager` — pushes positions

In `ConfigureSim`, alongside the existing `sim.neuronSpawnScale = NeuronLayoutScale` push at
`SimulationManager.cs:604` (whose comment already states the ordering constraint: *"Must
precede Reset(): BuildNeuronPositions() uploads this to the reset kernel"*):

```csharp
var positions = neuronFiring != null ? neuronFiring.PositionsCPU : null;
if (!ReferenceEquals(sim.neuronPositionsNorm, positions))
{
    sim.neuronPositionsNorm = positions;
    sim.InvalidateNeuronPositions();   // forces re-upload on next BuildNeuronPositions
}
```

The identity check makes a runtime CSV swap propagate on the next configure/reset while
keeping every other frame free of work — parity with today's rebuild-on-allocation semantics.

### 3 · `SimulationBase` — consumes without declaring

- Removed: `labelsPositionsCsv`, `csvCoordinatesAreNormalized`, `LooksNormalized01`, the
  CSV branch of `BuildNeuronPositions`.
- Added: `[NonSerialized] public IReadOnlyList<Vector2> neuronPositionsNorm;` and
  `InvalidateNeuronPositions()` (clears `_neuronPositionsBuilt`).
- `BuildNeuronPositions` keeps everything else: the per-sim **pixel-space multiply stays
  per-sim** (sims run at different resolutions), the upload, the dummy-buffer path, and the
  kernel binds. A null/empty list behaves exactly like a null CSV today — `neuronCount = 0`,
  random scatter, no ignition — plus a one-time warning naming the missing source.

## Migration

Field removal loses no data worth keeping (every scene stores the same asset reference), and
Unity silently drops stale YAML keys on next scene save — so unlike 2026-08-02 there is no
value-recording step. Order:

1. **11.0 TestScene gains a `NeuronFiringSource`** with only the CSV assigned (no blob, no
   OSC) — it currently has no source at all, and its two sims would otherwise lose seeding.
2. Land the code change.
3. Open each scene, verify, save (stale `labelsPositionsCsv` / `csvCoordinatesAreNormalized`
   keys disappear on save; no manual edits needed).

## Non-goals

- Per-sim position sets (no scene expresses one; a future species-offset is a different
  feature, designed then — same stance as 2026-08-02).
- Touching the firing blob, decay envelope, OSC drive, or the burst-trigger path.
- Renaming anything serialized.

## Risks

- **TestScene regression** if step 1 is skipped — its sims would silently random-scatter.
  Mitigated by ordering; success criterion 5 checks it.
- **Hidden CSV divergence**: if some scene ever intentionally gave a sim a *different* CSV,
  this erases that. The 2026-08-12 audit found none (all four scenes, one guid everywhere).
- **Rebuild staleness**: a CSV hot-swap mid-Play propagates at the next configure/reset, not
  instantly — identical to today, where the parse is cached per allocation.
- **Per-type reset buttons skip the reload trigger**: `ResetSimsOnly`/`ResetPhysarum`/etc. call
  `ConfigureAndReset` directly without re-running `neuronFiring.Initialize()`, so a mid-session
  CSV swap only propagates on the next full `Reset()` — the same asymmetry the firing blob
  already has (pre-existing, documented here, not fixed by this spec).

## Success criteria

1. `grep -r labelsPositionsCsv src` → exactly one declaration (`NeuronFiringSource`).
2. `grep -r "csvCoordinatesAreNormalized\|LooksNormalized01\|PositionsBuffer" src` → zero hits.
3. All four scenes: agents spawn on the neuron layout (not scattered); 11.3 CA ignition still
   fires at neuron positions; rings and dispersal stamps unchanged.
4. EditMode suite stays green (56/56 — this change adds no pure logic).
5. TestScene sims seed via its new blob-less `NeuronFiringSource`.

## Datasets (near-term outlook, 2026-08-12)

Two organoid datasets with different durations and neuron counts are expected soon. This
refactor is the prerequisite: a dataset swap becomes two fields on one component per scene,
and every consumer follows at the next configure/reset. Already dataset-adaptive today:
`FrameActivity` renormalizes to each recording's peak at load; playback duration is
blob-local (diurnal phasing and frame-advance triggers adapt); `id.x % neuronCount` remaps
agents to any count. Not covered here, designed when the second dataset arrives:

- An explicit **`NeuronDataset` pairing** (blob + positions CSV in one ScriptableObject) so
  the two can't be mixed across datasets — until then the count-match warning above is the
  guard.
- **`TermiteSim`'s 1:1 agent-per-neuron count** is serialized per scene (131), not derived —
  a different neuron count wraps via `%` but the 1:1 aesthetic needs a manual retune or a
  derive-from-source option.
- **Concurrent sources** (two datasets driving different species at once) — per-sim source
  routing at the manager level; tractable only because sims now read pushed data instead of
  owning CSVs.

## Follow-ups

- **ADR-0014: neuron layout has a single owner** — the ADR the 2026-08-02 spec promised,
  written once this ships (only then is the claim fully true).
- Continue the "match the X" tooltip audit; this closes the known instance.
