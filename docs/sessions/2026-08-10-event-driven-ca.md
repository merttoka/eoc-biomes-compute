---
status: closed
date: 2026-08-10
tags: [session, sim, cellular-automata, biome, event-driven]
related: [[../ARCHITECTURE]], [[../adr/0011-field-native-sims-derive-simulationbase]], [[../superpowers/specs/2026-08-10-event-driven-ca-design]]
---
# Event-driven CA: absolute cell rez, burst lifecycle, and the first coupled species

## Shipped

**Core extraction** (the `NeuronLayout` precedent — the test asmdef can only reference
`Biomes.Core`)
- `CellGrid` + `BurstEnvelope`/`RisingEdge` extracted as pure logic into `Biomes.Core`
  (`src/core_math/`). 20 new EditMode tests, 51 total.

**`FieldSimulationBase`**
- Absolute `cellRezHeight` (width derived from the master's aspect) replaces
  `cellResolutionScale` — grid scale no longer moves when output resolution does.
- Event-driven bursts: `burstEnabled`, `TriggerBurst()` (`[Button]`), rising-edge auto-trigger
  on `neuronIntensity` as broadcast by `SimulationManager`.
- Idle dispatches nothing at all — no rule, no render, no publish — and publishing stops on
  idle so the deposit stays put in the biome channel rather than getting overwritten each frame.
- `outputEnvelope` HLSL uniform multiplies the render only (state itself is unscaled).

**Channel seeding**
- `seedFromChannel`/`seedChannel`/`seedThreshold` + `SeededByChannel()` in
  `cellular_common.hlsl`. `LookupCASim` replaces its `initMode` figure with this; `CyclicCASim`
  now starts empty except for seeds.

**PDE rates for Excitability/Substrate (13/14)**
- `diffuseRate` 0.96, `decayRate` 0.004, advected — code defaults and all three Homeostatic
  assets. The deposit erodes once the burst goes idle, instead of sitting there inert forever.

**Task 7 — the coupling, wired**
- `UmweltBoid_Alt.asset` gets one new `reads` entry: channel 14 (`Substrate`), weight `1.5`,
  effect `Avoidance` (`UmweltEffect.Avoidance = 2`, confirmed against
  `components/core/UmweltMapping.cs:26` before writing it — not typed blind). This is the first
  species anywhere on this branch wired to perceive a CA channel; ADR-0011's central claim
  (a species can respond to a CA by editing one mapping asset, no shader change) had not
  actually been exercised until this edit. **Not yet Play-verified** — see Open below.

## Decided

- **`TriggerBurst()` guarded on `!burstEnabled`** (user ruling) — the button is inert in legacy
  (non-event-driven) mode rather than force-triggering a burst state that mode doesn't use.
- **`seedField` fallback is a static 1×1×1 `Texture2DArray`, not `Texture2D.blackTexture`** —
  Metal validates bound resource *types* even past branches that never sample them, so a 2D
  fallback for an array-typed uniform is invalid there.
- **Decay-rate comment corrected**: 0.004 is "about double Pheromone (0.002)"; the plan's
  original claim ("between 0.002 and 0.001") was arithmetically false and is fixed in the code
  comment.
- **Task 7 Step 1 deviation (controller-authorized)**: edited `UmweltBoid_Alt.asset` textually
  instead of through the Inspector — the Editor holds the project lock this session. Read the
  `UmweltEffect` enum from source first (`Avoidance = 2`) and copied the existing `reads:` YAML
  entry shape rather than guessing either.
- **Avoidance weight must be positive.** `Biome.compute`'s Avoidance branch computes
  `avoidance += max(0, val * entry.weight)` over channels already clamped to `[0,1]`, so a
  negative weight always contributes 0 — the entry would be a functional no-op. The brief's
  `weight: -1.5` was a wrong inference from Chemotaxis's "negative = repel" convention; for
  Avoidance the sign is meaningless except that negatives zero the branch out. Fixed to `1.5`,
  matching the asset's existing Avoidance row (channel 6, weight `1`, effect `2`). Recorded here
  for the next person adding an Avoidance mapping.

## Measured

Pre-change baseline, 2026-08-10, 3840×2160, GPU-synced: `LookupCASim` costs **0.190 ms** of
`SimulationManager.Step()`'s **9.257 ms** total (rule 0.011 / publish 0.007 / render 0.182).
This work was done for the look and for the coupling, not for frames — the CA was never the
bottleneck and event-driven bursting doesn't change that; it changes what the automaton looks
like and what it means to the ecosystem around it.

## Open / next session

1. **Boid-avoidance Play verification is pending the user's final checkpoint.** Editor lock
   this session; Task 7 Step 2 (enter Play, trigger a burst, watch `UmweltBoid_Alt` boids steer
   off `Substrate` and keep avoiding the eroding trace for a few seconds after it fades) has not
   been run yet — the coupling is wired, not yet proved.
2. **Full EditMode suite (51 expected) is deferred to the user** — Test Runner needs the Editor.
3. User wants a dedicated test for the `TriggerBurst()` `!burstEnabled` guard specifically.
4. Look/aesthetics of the CA layers are still being tuned — this session shipped the mechanism,
   not a final look.
5. `caParams` Edit-mode serialization issue remains known-deferred (a public serialized field
   holding a runtime clone; marking it `[System.NonSerialized]` is the fix, intentionally not
   bundled into this branch of work).
