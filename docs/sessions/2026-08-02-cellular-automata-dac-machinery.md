---
status: closed
date: 2026-08-02
tags: [session, sim, cellular-automata, shanghai, dac, show, biome]
related: [[../ARCHITECTURE]], [[../adr/0011-field-native-sims-derive-simulationbase]], [[../superpowers/specs/2026-07-23-cellular-automata-sims-design]], [[../superpowers/specs/2026-08-02-shanghai-dac-11-3-design]], [[../superpowers/specs/2026-08-02-neuron-layout-single-owner-design]]
---
# Cellular automata sims + Scene_DAC show machinery

Branch `feat/ca-sims-and-dac-show` (commit `893460e`). Not merged, not play-mode verified.

## Shipped

**Neuron single-owner (verification only — merge already landed as `caca144`).**
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

**DAC show machinery** ([[../superpowers/specs/2026-08-02-shanghai-dac-11-3-design]])
- `Biome.SeedChannelFromTexture()` + `SeedChannelKernel` — the only texture→channel path.
- `ShanghaiTransectBaker` (Editor) — own 16-bit PNG decoder → StreamingAssets blob.
- `ShanghaiTransect` + `ShanghaiTransect.compute` — 12-epoch crossfade → openness → closure floor.
- `ShowArc`, `CueExporter`.
- `centreKeepOut` on `ShowArc` / `BiomeInjector` / `FieldSimulationBase`.

## Decided

- **`BlendMode.MinToward` added** (value 3, wired into `InjectStampKernel` too). The DAC spec
  asks `SeedChannelFromTexture` to reuse the existing three modes "so there is one blend
  vocabulary, not two" — but its own closure rule is `perm = min(perm, 1 − shanghai(t))`, and
  none of Additive/MaxToward/SetToward can express a `min`. SetToward would stomp termite mounds
  and fight [[../adr/0010-permeability-agent-built-topography|ADR-0010]]. Extending the shared
  enum keeps one vocabulary and makes it expressive enough to state the rule.
- **Built-up response curve γ 0.45 / gain 2.0, not raw.** Measured over the actual transect:
  GHSL normalises m²/cell against a global max of 7368, so the 2030 band mean is **0.144**. A raw
  `1 − v` seed moves permeability 0.90 → 0.86 across the entire piece — invisible. The curve
  gives 0.764 → 0.429 mean and 14 % → 54 % closed area. Concave because what matters is whether
  an agent can cross a cell, which saturates far sooner than area does.
- **Hue spread computed closed-form, not via `ParameterInterpolator` waypoints.** The spread is a
  one-parameter family; expressing it through the interpolator means authoring and syncing two
  8-type `PhysarumParams` assets differing in one scalar. Pushed through `SetParameter` — the
  same shipped path MIDI drives. The interpolator can still be driven alongside.
- **Arc clocked on `SimStepCount`, not wall time**, so it is identical under Recorder.
- **Keep-out biases, never cuts.** Smoothstepped and floored at `1 − depth`; applied to the CA
  *render*, not its rule (a gap in the rule leaves a seam the waves never cross). 2.6 has no
  cutout and would show a hard band as a defect.

### Verified in the data
Decoding all 12 epochs confirmed the site reading the show rests on: the venue centre pixel
(1016, 1077) reads **0.3384 → 0.3393 across 1975–2030** — flat. And growth is strictly monotonic
across the transect, so the `min()` closure floor is sound.

## Also shipped (second pass, commit `6566928`)

- **`.meta` files authored by hand.** Unity accepts pre-existing `.meta`, so the GUIDs were
  minted rather than waiting on an editor import — that was the only thing blocking scene wiring.
  17 GUIDs, each verified declared exactly once.
- **`Scene_DAC` wired.** Master 6750×675 → **9472×900**; biome field 675×68 → **1024×97**; both
  CA sims added to `simulations` (3 → 5) with `CyclicCA.couplingSource → LookupCA`; a `Show`
  object carrying `ShanghaiTransect` / `ShowArc` / `CueExporter`. Scene folder brought under
  version control — it had been entirely untracked.
- **Execution-order defect caught and fixed.** `ShowArc` was `-90`, i.e. *after*
  `ShanghaiTransect` (`-100`), so the transect would have seeded with the previous step's arc
  values every frame. Now `-110 → -105 → -100 → 0`.
- **Shaders verified.** 12/12 compute kernels compile through Unity's own `libdxcompiler` at
  `cs_6_0`, driven via a small dlopen harness (no `dxc` CLI ships with Unity). The harness was
  negative-controlled against a missing entry point, a syntax error, a type error and an
  undeclared identifier, so the passes are real.
- **Scene graph validated**: 84 documents, no duplicate fileIDs, every local fileID and every
  guid resolves, GameObject/component and Transform parent/child back-references agree.

## Open / next session

1. **Retune is expected at the new resolution.** 675 → 900 px tall moves `ResolutionScale`
   0.3125 → 0.4167; every pixel-unit param shifts ~33 %. This is a consequence of hitting the
   spec's aspect, not a regression.
2. **`Scene_DAC` `NeuronFiringSource.spawnScale` is `(0.15, 0.75)`** — matches neither the value
   the neuron spec recorded for DAC (`0.5, 0.6`) nor the stale ring value. No drift any more
   (everything reads the one owner), but confirm the layout is intended. Left as authored.
3. `biomeRezX/Y` does **not** need its `[Range(32, 1024)]` raised — 1024×97 fits 10.524:1. The
   spec listed that as a risk; it is not one.
4. **Baked transect is on disk but uncommitted** —
   `Assets/StreamingAssets/biomes11/shanghai_transect.bytes` (9.1 MB, round-trip verified).
   Regenerable via `Biomes > Bake Shanghai Transect`; left out of git to keep a 9 MB binary out
   of history. Decide whether to track it.
5. **Play-mode is the whole remaining risk surface**: neuron spec criteria 4–5, CA spec all, DAC
   spec 1–9. Nothing has ever executed. Performance at 9472×900 with 1 M physarum agents plus two
   CA layers is entirely unmeasured.
6. **Not merged to main.** Everything mechanically checkable is green, but `main` is required to
   be production-ready and this has not run once. `BiomeChannel.Count` 13 → 15 touches *every*
   scene (handled by the existing zero-fill + warning, but unverified) — that is DAC spec
   criterion 7. Merge after a play-mode pass on 11.1 CURRENTS and 11.2 SIGGRAPH.
7. `Scene_DAC.unity.bak-preCA` is the pre-edit backup; delete once the scene opens cleanly.
8. `README` / `ARCHITECTURE` / `ROADMAP` deliberately **not** updated — the CA spec says those
   track shipped pillars and this has not run yet.
