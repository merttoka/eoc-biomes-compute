---
status: closed
date: 2026-08-02
tags: [session, sim, cellular-automata, shanghai, dac, show, biome]
related: [[../ARCHITECTURE]], [[../adr/0011-field-native-sims-derive-simulationbase]], [[../superpowers/specs/2026-07-23-cellular-automata-sims-design]], [[../superpowers/specs/2026-08-02-shanghai-dac-11-3-design]], [[../superpowers/specs/2026-08-02-neuron-layout-single-owner-design]]
---
# Cellular automata sims + Scene_DAC show machinery

Merged to `main` as `b968a1e`. Ran, verified, rendered: all nine DAC success criteria pass,
and the deliverables are at `Recordings/DAC_Shanghai_2026-08-02/`. See
[[../DELIVERY_DAC_SHANGHAI]].

*(Sections below are in the order the work happened, including two conclusions that later
measurements refuted — kept rather than rewritten, because the refutations are the useful
part: the mound overlay masking the agent layer is what made `spawnScale` look innocent.)*

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
| 4 · centre deader than edges | **FIXED** — was inverted (C/edge 5.15); root cause found and corrected, see below |
| 8 · cutout survivability | **PASS** — masked 1.2 crop still reads as complete; the composition is flanked, not centred |
| 2, 3, 9 | **all PASS** — measured on the full loop, see below |

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

## Full loop + render (fourth pass)

Ran the **real** loop — 5400 continuous steps at the shipping 1:1 step-to-frame ratio with
ShowArc on its own `SimStepCount` clock, twice, so the wrap could be compared against lap 1.

- **Criterion 2 · seam — PASS.** Seam delta `0.00209` against `0.01401` for 59 ordinary frames
  (ratio 0.149). The wrap moves *less* than normal playback does; `ResetTermites` at 75 s masks
  the permeability reset exactly as designed.
- **Criterion 3 · hue spread — PASS.** Physarum-layer hue **circular variance** (the correct
  statistic — hue wraps, so a plain σ is meaningless) goes `0.556 → 0.000`, mean hue `0.000`,
  i.e. exactly red. "Many organisms → one body" is now a measurement.
- **Criterion 9 · rings land on the agents — PASS (verified numerically).** Sampling agent
  density in a 28 px disc at each neuron's mapped position: **8.03x** random enrichment at the
  frame EDGES (4.18x at centre). The edge figure is the discriminator — the old desync was
  zero-error at centre and worst at the edges, so a centre-only check would have passed even
  before the fix. Counterfactual: sampling the same edge neurons at the stale `(0.4, 0.75)`
  scale gives **0.46x** random, i.e. emptier than chance — rings would have been drawn on bare
  canvas. Correct/stale ratio at the edges: **17.5x**.
- **Criterion 9 groundwork · firing — was impossible, now unblocked.** Peak firing over 10 800 steps was
  **0.0000**. The organoid playhead is scrubbed by an external patch over OSC `/index`, and a
  pre-rendered show has no OSC — so the arc's "firing sparse → dense → peak → sparse" row drove
  nothing, and rings and dispersal stamps could never have appeared. `ShowArc` now advances the
  playhead itself (`driveFiringPlayhead`, 5400 blob frames per loop, wrapped at the seam).
  Peak firing is now `1.0000` with 6932/10 800 steps above threshold.
- **Criterion 8 · cutout — art call.** Inside the 1.2 cutout, luma is **1.90×** the rest of
  frame. Two readings: by the spec's own argument (*motion*) it is safe, because the agent layer
  at centre is near-empty (0.0011 vs 0.022–0.033 at the edges); by *brightness* it is not,
  because the mound overlay renders the city there. `moundOverlayStrength` (0.6, and ~95 % of
  all luminance in frame) is the lever.

**Render.** 5400 frames at 9472×900 written as a PNG sequence from batchmode at **312 ms/frame**
(~28 min, ~32 GB), then `tools/encode_dac.sh` produces both screen crops, the H.264 and ProRes
masters, and the 1920×1080 submission. A `split=4` single-decode pass was tried first — PNG
decode, not x264, is the bottleneck at 8.5 Mpx/frame — and **dropped**: it is all-or-nothing
across a 54 GB sequence, so an interruption loses everything. The shipped script encodes **per
output with resume**, ship-critical crops first.

> **Delivery caveat.** 9472 px is 592 macroblocks wide. That is legal H.264 — Level 6.2 allows
> 139 264 MBs and ~1056 MBs of width, and libx264 encodes it at Level 6.0 — but many *hardware*
> decoders cap at 4096 or 8192 px. Verify the venue's player, or hand over the ProRes.

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
5. ~~**Play-mode is the whole remaining risk surface**: nothing has ever executed, and performance
   at 9472×900 is entirely unmeasured.~~ → **Resolved: it has run.** 0 errors / 0 exceptions
   across all three scenes, all nine criteria measured. 9.26 ms/sim step and 10.10 ms/step+composite
   at 9472×900 with 1 M physarum plus two CA layers — 99 fps realtime-equivalent, inside the
   16.7 ms budget.
6. ~~**Not merged to main.**~~ → **Resolved: merged as `b968a1e`.** `BiomeChannel.Count` 13 → 15
   touches every scene, which was DAC spec criterion 7, and it passes — CURRENTS (3840×1080) and
   SIGGRAPH (3840×2160) both reset and step clean.
7. `Scene_DAC.unity.bak-preCA` is the pre-edit backup; delete once the scene opens cleanly.
8. ~~`README` / `ARCHITECTURE` / `ROADMAP` deliberately **not** updated — the CA spec says those
   track shipped pillars and this has not run yet.~~ → **Resolved for two of three:** it has run,
   so `README` (CA + Scene_DAC pillars, and the 12→15 channel count) and `ARCHITECTURE` are
   updated. `ROADMAP` is still untouched.
