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

## It has now run (third pass, commit `23ec319` + this)

Executed in a **batchmode Unity against an APFS copy-on-write clone**, so the author's Editor
kept the real project's lock and the clone cost no disk. Metal / Apple M4 Max. The sim is driven
from edit mode with ShowArc / ShanghaiTransect ticked explicitly (FixedUpdate does not fire
there), which makes step counts exact.

**Final run: 0 errors, 0 exceptions across all three scenes.**

| Criterion | Result |
|---|---|
| 1 · master + crops | **PASS** — 9472×900; 9472×800 and 9000×900 both crop with no rescale |
| 5 · CA glitch 55–75 s | **PASS** — visually confirmed; magenta tear across frame at 75 s, resolved by 89 s |
| 7 · existing scenes | **PASS** — CURRENTS (3840×1080) and SIGGRAPH (3840×2160) reset + step clean |
| 6 · cues.json aligns with frames | **PASS (structurally)** — `fps 60 == simRate 60`, so step index *is* frame index; arc cues land on exact integer frames (start 0 / many 1200 / converge 3300 / oneBody 4500 / loop 5400) and `ResetTermites` fired at the loop point |
| 4 · centre deader than edges | **FAILS** — see below |
| 2, 3, 8, 9 | not yet assessed (need the full loop and a human eye) |

One gap in the cue export: **0 firing onsets recorded.** The arc cues — what Max/MSP needs
most — are correct, but no neuron crossed the 0.35 onset threshold during the run. Either the
organoid blob is not advancing its playhead in this edit-mode harness, or the threshold is too
high for this material. Worth a look before sound is composed against it.

**Performance is a non-issue.** GPU-synced: **9.26 ms/sim step, 10.10 ms/step+composite** at
9472×900 with 1 M physarum plus two CA layers — 99 fps realtime-equivalent, comfortably inside
the 16.7 ms budget. The 90 s loop is ~0.9 min of GPU work. (An earlier unsynced measurement read
0.1 ms/step; that was enqueue cost, not GPU cost, and was discarded.)

### Criterion 4: cause found, fixed — `spawnScale.x` 0.15 → 0.9

Centre/edge luminance started at **C/edge ≈ 5.15**, the inverse of what the piece wants. Four
measurements, two of which refuted an earlier hypothesis recorded here:

1. **Not under-stepping.** Soaked 4000 steps at the t=55 s pose: C/edge 5.11 → 5.15, plateaued by
   step 500.
2. **Not the habitat bands.** Hypothesised that termite (band 0.0–0.5, the heaviest composite
   weight at 1.5) was being recruited into the closing centre. **Refuted:** dropping termite
   weight 1.5 → 0.0 moved C/edge 5.10 → 5.09. It contributes essentially nothing.
3. **The metric was measuring the wrong thing.** At `moundOverlayStrength 0` the whole composite
   collapses to L=0.0000 C=0.0337 R=0.0000 — the overlay painting the permeability raster is
   ~95 % of all luminance, and it was drowning the agent layer. An earlier note here claiming
   "`spawnScale` is exonerated" was an artefact of that masking and is **wrong**.
4. **With the overlay off, `spawnScale.x` is decisive:**

   | `spawnScale.x` | whole-frame | L | C | R |
   |---|---|---|---|---|
   | 0.15 *(was authored)* | 0.0080 | 0.0000 | **0.0393** | 0.0000 |
   | 0.50 | 0.0118 | 0.0001 | 0.0051 | 0.0002 |
   | 0.90 | 0.0156 | 0.0220 | 0.0011 | 0.0328 |
   | 1.00 | 0.0160 | 0.0249 | **0.0009** | 0.0368 |

   At 0.15 every agent is crammed into a single blob at **dead centre** — the one region the
   thesis says must be empty — with both edges at exactly zero. At 0.9–1.0 the agents fill the
   frame and the closure carves a hole in the middle: C/edge ≈ **0.03** on the agent layer, which
   satisfies criterion 4 *and* criterion 8 (nothing load-bearing inside the 1.2 cutout).

**Changed `Scene_DAC` `NeuronFiringSource.spawnScale` x from 0.15 to 0.9** (y left at 0.75 — only
x was swept). One scalar, trivially revertible. Result on the full composite:

| t | before (x 0.15) | after (x 0.9) |
|---|---|---|
| 5 s | L 0.0004 C 0.0826 R 0.0000 — C/edge **401** | L 0.0310 C 0.0187 R 0.0430 — C/edge **0.51** |
| 55 s | L 0.1648 C 0.4256 R **0.0000** | L 0.1833 C 0.4041 R **0.0338** |
| 89 s | L 0.1850 C 0.3956 R 0.0086 | L 0.1978 C 0.3779 R 0.0320 |

The right edge is now alive at every checkpoint where it was previously black throughout, so the
frame finally uses its full 11.84:1 width. Mid-arc C/edge stays >1 because the mound overlay
renders the city itself — that is the city being visible, and `moundOverlayStrength` (0.6) is the
knob if it should recede.

**Caveat on the metric.** "Visibly deader" is about *motion*; mean luminance cannot see that.
Judge the final call on the render.

## Open / next session

1. **Retune is expected at the new resolution.** 675 → 900 px tall moves `ResolutionScale`
   0.3125 → 0.4167; every pixel-unit param shifts ~33 %. This is a consequence of hitting the
   spec's aspect, not a regression.
2. ~~`spawnScale` is `(0.15, 0.75)` — confirm the layout is intended.~~ → **Resolved: it was
   wrong and is now `(0.9, 0.75)`** (measured, see above). The y component was never swept;
   0.75 is still the authored guess.
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
