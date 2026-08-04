---
status: closed
date: 2026-08-02
tags: [session, sim, cellular-automata, biome]
related: [[../ARCHITECTURE]], [[../adr/0011-field-native-sims-derive-simulationbase]], [[../superpowers/specs/2026-07-23-cellular-automata-sims-design]], [[../superpowers/specs/2026-08-02-neuron-layout-single-owner-design]]
---
# Cellular automata arrive as field-native sims

> **Provenance.** This session also produced a body of delivery machinery for a since-abandoned
> show. That half — `ShowArc`, `CueExporter`, `ShanghaiTransect` and its baker, the show scene,
> and the render/encode tooling — was not carried onto this branch. It is preserved in full on
> `archive/dac-shanghai`, along with the original combined log. See
> [[../superpowers/specs/2026-08-04-engine-extraction-design]] for what was split and why.

## Shipped

**Neuron single-owner (verification only — the change itself landed as `caca144`).**
- 31/31 EditMode tests pass. Ran headlessly by compiling the live sources + real fixtures
  against Unity's own nunit and driving `[Test]`/`[ValueSource]` by reflection; the Editor held
  the project lock and `Library/ScriptAssemblies` was stale.
- Spec criteria 1–3 hold: exactly one `spawnScale` declaration (`NeuronFiringSource.cs:32`),
  mapping written once in HLSL and once in C#, tests green.

**CA sims** ([[../superpowers/specs/2026-07-23-cellular-automata-sims-design]], full scope)
- `src/components/core/FieldSimulationBase.cs` — see
  [[../adr/0011-field-native-sims-derive-simulationbase|ADR-0011]].
- `SimulationBase.cs` — extracted `MarkAllocated()`, `NeedsAllocation()` now `virtual`.
- `CyclicCASim` / `LookupCASim` + `CyclicCA.compute` / `LookupCA.compute` +
  `CyclicCAParams` / `LookupCAParams` (`IParamSet`).
- `includes/cellular_common.hlsl` — state pair, toroidal reads, coupling gate, neuron ignition,
  centre keep-out. Guarded; includes only guarded headers.
- CA↔CA coupling, neuron ignition, channel publish via new `BiomeChannel.Excitability` (13) /
  `Substrate` (14), `Count` 13 → 15.
- `SimulationManager.ResetCellular()`, typed on the shared base.

## Decided

- **`BlendMode.MinToward` added** (value 3, wired into `InjectStampKernel` too). A raster→channel
  seed whose rule is a monotonic closure floor — `perm = min(perm, 1 − source)` — cannot be
  expressed by any of Additive/MaxToward/SetToward. SetToward would stomp termite mounds and
  fight [[../adr/0010-permeability-agent-built-topography|ADR-0010]]. Extending the shared enum
  keeps one blend vocabulary and makes it expressive enough to state the rule.
- **Keep-out biases, never cuts.** Smoothstepped and floored at `1 − depth`; applied to the CA
  *render*, not its rule — a gap in the rule leaves a seam the waves never cross. The same master
  may play on a display with no cutout, where a hard band reads as a defect.

## Verified

- **`.meta` files authored by hand.** Unity accepts pre-existing `.meta`, so the GUIDs were
  minted rather than waiting on an editor import. 17 GUIDs, each verified declared exactly once.
- **Shaders compile.** 12/12 compute kernels compile through Unity's own `libdxcompiler` at
  `cs_6_0`, driven via a small dlopen harness (no `dxc` CLI ships with Unity). The harness was
  negative-controlled against a missing entry point, a syntax error, a type error and an
  undeclared identifier, so the passes are real.
- **It runs.** 0 errors, 0 exceptions across all scenes, in a batchmode Unity against an APFS
  copy-on-write clone (the author's Editor kept the real project's lock and the clone cost no
  disk). Metal / Apple M4 Max.
- **Performance is a non-issue.** GPU-synced: **9.26 ms/sim step, 10.10 ms/step+composite** at
  9472×900 with 1 M physarum plus two CA layers — 99 fps realtime-equivalent, comfortably inside
  the 16.7 ms budget. (An earlier unsynced measurement read 0.1 ms/step; that was enqueue cost,
  not GPU cost, and was discarded.)
- **`BiomeChannel.Count` 13 → 15 touches every scene**, and both existing scenes survive it —
  CURRENTS (3840×1080) and SIGGRAPH (3840×2160) reset and step clean.

## Open / next session

1. **Retune is expected when resolution moves.** `ResolutionScale` scales every pixel-unit param;
   a change in field height shifts them proportionally. A consequence of resolution, not a
   regression.
2. `biomeRezX/Y` does **not** need its `[Range(32, 1024)]` raised — 1024×97 fits 10.524:1. The
   CA spec listed that as a risk; it is not one.
3. **No scene on this branch instantiates a CA sim yet.** The sims were only ever exercised in
   the archived show scene. A sandbox scene that wires `CyclicCASim` / `LookupCASim` is the next
   step — see [[../superpowers/specs/2026-08-04-engine-extraction-design]] C5.
4. `ROADMAP` still does not list the CA pillar; `README` and `ARCHITECTURE` do.
