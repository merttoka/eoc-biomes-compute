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

## Open / next session
1. Answer the three intent checks from the review: 7588c5d `Scene_SIGGRAPH` 3100×1000 + receiver
   off; e06f3e8 termite `wallBuildAmount` 0.003 → 0.03; DiurnalSun disabled in
   `Scene_SIGGRAPH DAC`.
2. Merge order: `review/biomes-siggraph-perf-regression` first, then this branch.
3. `com.unity.pipeline` still uncommitted in `Packages/` — keep or revert.
4. 11.3 `Scene_BraveNewWork` has `targetFPS 90` / `simRate 60` and no frame export — the same 60
   cap would apply (left alone: outside this review's scope).
