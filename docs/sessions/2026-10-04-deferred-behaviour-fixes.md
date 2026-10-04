---
status: closed
date: 2026-10-04
tags: [session, fixes, behaviour, performance, biomes, siggraph, ndi]
related: [[2026-10-03-biomes-siggraph-runtime-pass]], [[2026-10-03-biomes-siggraph-regression-perf-review]], [[../ARCHITECTURE]]
---
# Deferred items from the Biomes/SIGGRAPH review — decided and shipped

The user picked from the deferred lists of [[2026-10-03-biomes-siggraph-regression-perf-review]]
and [[2026-10-03-biomes-siggraph-runtime-pass]] (CA items excluded). Look-changing work lives on
`review/deferred-behaviour-fixes`, stacked on `review/biomes-siggraph-perf-regression`, so the
output-identical review can merge on its own. Everything verified in the live Editor (Unity CLI).

## Shipped

On `review/biomes-siggraph-perf-regression` (no look change):
- **11.3 Brave New Work scene** committed (scene, materials, param assets; 192 KB).
- **NDI sizes** (`ExternalTextureSender`) — KlakNDI only encodes width % 16 / height % 8.
  Frames are now cropped, centred (≤ 15 columns / 7 rows): downscaled streams inside their
  existing Blit (free), full-res via `CopyTexture` (0.13 ms at 3100×1000, 0.35 ms at 4K-ish;
  none when already aligned, e.g. 3840×2160). Verified: 480×108 → 480×104, 551×124 → 544×120,
  no KlakNDI warnings.

On `review/deferred-behaviour-fixes` (changes the look, approved):
- **Flow keeps its sign** — `DiffuseFieldsKernel` clamped every channel to 0..1 since e1a8654
  (2026-06-09), erasing negative flow: half the circulation gone and all advection drifting
  +x/+y. FlowX/Y now clamp to −1..1. Live: ~46% of flow pixels negative, range ≈ ±0.25.
- **Boid seam** — ceil-sized cells left a partial row/column at the wrap, so boids within range
  across it could sit two cells apart and never interact. Cells are now `rez / floor(rez/range)`
  per axis; axes with < 3 cells no longer visit a cell twice.
- **Spawn RNG** — measured, not guessed: the physarum turn tie-break bias was negligible
  (49.2 vs 50%, the earlier log overstated it), but spawn headings from
  `RandomDirection2(id·0.001 + sin t)` lost float precision, so some directions were 2× likelier
  (21k vs 44k agents per 10° bin) and every reset's starburst was lopsided. Physarum/boid
  headings and all scatter/alt-spawn positions now use `Hash1u`/`Hash2u` (bins within ±2%).
- **Injector stamps** — `InjectStampKernel` looped every stamp at every biome pixel (0.42 ms per
  step at 1024×576, 135 stamps). Rewritten: per-8×8-tile culling into a bitmask, pixels apply
  survivors in index order on register copies of touched channels, one write per texel.
  0.12–0.19 ms. Proving it exposed a real bug: re-reading a texel the thread just wrote is not
  coherent on Metal, so overlapping same-channel stamps (the 131 dispersal pulses, wherever
  termites cluster) were randomly lost — the old kernel differed from itself run to run and
  from a double-precision CPU chain by up to 0.90; the new one is deterministic and within
  half precision (max error 0.0005).
- **MFT biome knobs** — `Biome` runs on a Play-mode copy of `fieldConfig`; knobs resolve
  `biome.fieldConfig` per call (bindings are built in `OnEnable`, possibly before the copy
  exists). The shared asset (`Scene_SIGGRAPH` + `_DAC_4k`) no longer drifts; **Save Field Config
  To Asset** on `Biome` keeps tweaks deliberately. Live: knob moved clone 0.0200 → 0.0777,
  asset stayed 0.0200.
- **Firing playback** — `Play()` after a finished non-looping pass restarts from the first frame.
- **targetFPS 90 → 60** in `Scene_SIGGRAPH`, `Scene_SIGGRAPH DAC`, `_DAC_interior`. Measured: at
  90 fps 34% of frames carried no sim step and frame-time sd was 21 ms; at 60 it is 2.7 ms and
  every frame has new sim state (~29% fewer renders). Not changed in `_DAC_4k` (GPU-bound ~32 fps,
  no gain) or `_DAC_exterior` (FigureExporter exports per rendered frame — its export cadence
  would change).
- **README** lists the git-ignored 11.2 media (~2.8 GB, two ProRes clips + the take's wav); the
  firing blob and positions CSV are tracked.

## Verification
- Unity Metal compile: Biome/BoidSim/PhysarumSim/TermiteSim — 0 messages; C# clean; EditMode
  78/78.
- Injector rewrite: bit-identical to the old loop after one dispatch where the old loop was
  coherent; deterministic across 6 resolution/count cases incl. edge tiles (551×124) and the
  > 1024-stamp fallback; CPU-reference check above.
- `_DAC_4k` in Play: 0 errors, composite frame visually healthy (no NaN/seams), 20k boids finite
  and in bounds across the full width, playback restart confirmed (131000 → 125000, playing).

## Decided
- Not changed: receiver `selfDrive` (debug-video copy already deduped per frame — no gain);
  perf-doc cadence presets (visible); debug grid (≈ 0 ms); waypoint resolution scaling (user
  declined); physarum per-step turn tie-break RNG (measured fine).
- 11.2 media stays out of git (documented instead).
- `BiomeWriteFused` has the same in-thread re-read pattern, but only if one umwelt writes a
  channel twice (e.g. Temperature + metabolic heat); no umwelt in 11.1–11.3 does. Left as is.

## Follow-up (same session)
- MFT inspector shows a note: biome knobs edit the Play-mode config copy; keep changes via
  Biome › Save Field Config To Asset; Project-window asset edits don't reach a running sim.
- 11.3 `Scene_BraveNewWork` `targetFPS` 90 → 60.
- Intent checks closed — all deliberate, still being explored: `Scene_SIGGRAPH` 3100×1000 +
  receiver off (7588c5d), termite `wallBuildAmount` 0.03 (e06f3e8), DiurnalSun off in
  `Scene_SIGGRAPH DAC`.
- `com.unity.pipeline` removed: it is an Editor-only bridge for the Unity CLI/agents (eval, Play,
  capture, tests) with no runtime or build effect. Reinstall with `unity pipeline install` when
  an agent needs to drive the Editor.
- Rendering above 60 fps adds nothing while `simRate` is 60: extra frames repeat the last sim
  state (34% empty frames at 90). Smoother motion on the 120/240 Hz displays needs either a
  higher `simRate` (agent motion and decay are per step, so the sim also runs faster and costs
  more) or render-side frame blending between the last two composites (not built).

- Built on request: **`frameBlend`** (default off) and **`vSyncDivisor`** (0–4) on
  `SimulationManager`. Blend composites only on new steps and blends the last two composites
  per frame by the time since the latest step. Verified in Play (Scene_BraveNewWork): with it
  off, frames without a sim step were exact repeats (0 of 72 changed); on, every such frame
  showed a new image (60/60, 99/99) while the sim held 60 steps/s; buffers exist only while on;
  `vSyncDivisor 4` sets `QualitySettings.vSyncCount = 4` (the Editor applies it only with the
  Game view's VSync on — it was off). `com.unity.pipeline` reinstalled for the test and removed
  again.

## Open / next session
1. Merge order: `review/biomes-siggraph-perf-regression` first, then this branch.
2. Try `frameBlend` + `vSyncDivisor 1` (240 fps) or `targetFPS 240` on the 240 Hz monitor —
   the Editor's fps here was throttled by the test's per-frame readbacks, so smoothness is
   unmeasured. For recordings, keep `frameBlend` off unless blended frames are wanted.
