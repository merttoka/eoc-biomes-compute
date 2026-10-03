---
status: closed
date: 2026-10-03
tags: [session, review, performance, regression, biomes, siggraph]
related: [[../ARCHITECTURE]], [[../ROADMAP]], [[../../Assets/Workspace/11.0 Biomes/docs/PERFORMANCE]], [[2026-06-09-second-performance-pass]]
---
# 11.0 Biomes + 11.2 SIGGRAPH — regression & performance review

Branch `review/biomes-siggraph-perf-regression` (off `main` @ 4db77a3). Regression window
`ca085ff..4db77a3` (the 2026-09-08 → 09-10 DAC burst). Four review passes (5 parallel reviewers →
adversarial diff review + fresh hunt → two closure reviews of the fix commits); findings verified
before applying. Rule: apply only fixes and output-identical optimizations; anything that
alters the look is listed under *Deferred*.

## Shipped

### Regressions fixed
- **Show scene lost its physarum preset** — `Scene_SIGGRAPH.unity` `paramsSO` pointed at a guid
  that died when 7588c5d renamed the snapshot to `…_SHOWVERSION 1` (new guid); the sim silently ran
  `CreateInstance` defaults. Repointed to `d52b0cd…` (same asset the DAC takes use).
- **Fading sim on freed GPU resources** (7588c5d) — a non-`startOnPlay` sim fading when the
  manager reallocated (rez change / disable→enable) kept `FadeStep`-ing on released trail arrays,
  and `outTex` survived `Release()` as a destroyed RT bound to the composite. Now stopped on
  realloc; `outTex` nulled.
- **`liveUmwelt` clone leak** (a2969ad) — edit-mode Reset never destroyed the old clone; OnDestroy
  never destroyed it at all. Now destroyed in both modes (DestroyImmediate outside Play).
- **Interpolator restart did nothing** (b485517/67923a8) — with `keepRunningOnSimReset` off,
  `SyncLiveInstances` re-imposed the waypoint before reset detection, so the restarted leg ran
  W0→W0; Done never restarted. Reset detection now runs first.
- **Interpolator 'from' taken from a stopped sim's stale clone** (67923a8) — at `Play()` a
  Stopped sim's leftover clone was snapshotted (second take in a session inherited the previous
  waypoint). A Stopped or Fading sim's clone counts as stale until a leg of the current take has written it, so `Play`,
  `Advance` and the MIDI pause/resume all snapshot lazily from the fresh clone; mid-queue legs of
  a sim that was live this take keep continuity.
- **SimTimeline vs group `playOnStart`** — two `Start()`s raced on script order. Timeline now
  leaves a `playOnStart` group running and warns.
- **Receiver output RT** — a freshly created output RT is cleared to transparent (a stopped clip
  no longer copies over undefined memory into the overlay).

### Latent bugs fixed (pre-window, dormant in current scenes)
- `keepout.hlsl` eviction: rect flush to a canvas edge / corner / full-width band / abutting rects
  could spawn agents inside the hole. Now picks the nearest on-canvas side outside every rect,
  else hops into the abutting rect which evicts it next (two sweeps); inclusive inside-test;
  evicted point kept below 1. Checked on 7 layouts with a CPU port.
- Trail writes `round(position)` could hit pixel `rez` (OOB UAV write, undefined on Metal) — wrapped
  (torus).
- Composite overlay loaded the receiver texture at composite pixel coords (texture is clip-sized)
  — now UV-sampled; identical at equal resolution.
- `ParameterRecorder` replayed raw recorded values through `SetParameter` (which maps 0..1 into the
  range — moveSpeed 1 → ~50), in both playback and seek. Replays via `LiveParamSet.SetValue`.
- Debug-grid off→on toggle stacked 11–15 ARGBFloat RTs; edit-mode `Destroy` errors in
  `GPUResourceManager`/Biome.

### Performance (output-identical)
| Change | Where | Saving (DAC scenes) |
|---|---|---|
| Mound overlay fused into `CompositeRenderKernel` (f16 quantize reproduces old store/reload) | SimulationManager.compute/.cs | one full-res RMW pass + dispatch per frame — ~133 MB/frame at 4k |
| Debug grid: ARGBHalf, uniforms bound once, material set once, invisible quads skipped | Biome.cs | ~150 MB → ~75 MB writes per PDE step (15 channels + legacy view at 1024×576), 0 when off-screen |
| PDE passes copy only channels they don't write; advect single store per channel | Biome.compute | 16 of ~61 stores per cell per step |
| Diffuse skips per-tap flow/permeability terms when no channel uses them | Biome.compute + Biome.cs flags | 8 loads + 8 normalizes per cell per step |
| Boid neighbour loop drops redundant self-skip index load | BoidSim.compute | 4 B of 24 B per neighbour iteration |
| Receiver: one copy per frame (was per step + selfDrive), none while stopped, no CommandBuffer alloc | ExternalTextureReceiver.cs | 1–2 full-res blits/frame |
| Cached PropertyToIDs (composite, biome write/perception/build/inject, sims), no `"simInput"+i` | core + Sim | ~60 string hashes + 8 string allocs per frame |
| No per-step `int[]`/`params` arrays (kernel lists, bind helpers) | Sim/*.cs, SimulationBase | ~9 arrays per step |
| `UmweltKeys` span parsing | core_math | 1–3 Substrings per key per frame while an umwelt leg runs |
| PNG export drops `Apply()` re-upload; cached readback delegate | FigureExporter, BiomeInjector | 1 texture upload per exported image |
| Editor: receiver repaints only while receiving, 1 Hz discovery; layout preview 10 Hz; cached gizmo style | Editor/, Biome.cs | editor-time only |

### Verification
- C#: `dotnet build Assembly-CSharp-Editor.csproj` (covers runtime + editor asmdefs) — green.
- Shaders: all 49 kernels compile through glslang's HLSL→SPIR-V front-end (baseline 51; the 2
  removed kernels are the only delta); negative test confirms the checker catches errors.
- EditMode tests via offline reflection runner: 62/66 pass, the 4 others are `[TestCaseSource]`
  cases the runner can't feed (not failures).
- **Not verified in Editor:** the user's Editor was in Play mode throughout, so Unity's own
  compile/import and a visual A/B are pending. `com.unity.pipeline` added to `Packages/manifest.json`
  (uncommitted) for CLI driving.

## Decided
- Output-identical only. Behaviour-changing items go to the user with evidence, not into commits.
- Recorder blit kept unconditional — unassigning `recorderTarget` is the switch; a toggle would add
  nothing.

## Deferred (behaviour-changing or needs measurement / authoring)
1. **Flow channels clamped to [0,1]** in `DiffuseFieldsKernel` (`clamp(relaxed, 0, 1)`, e1a8654) —
   negative flow dies each step, so advection drifts +x/+y. Fix = signed clamp for FLOW_X/Y. Changes look.
2. **Field sims' `time` uniform always 0** — `FieldSimulationBase` never advances `_simStep`; CA
   noise/stochastic gate become a fixed mask. Changes look.
3. **Interpolator waypoints not resolution-scaled** — live clone is scaled by rezY/2160, waypoint
   assets are raw (and 0-minimal-test Physarum/Boid waypoints look baked at ×0.463); `_DAC_4k`
   spatial params drop to 46% at the 69–73 s transition. Needs re-authoring + scaling the target per leg.
4. **Boid spatial-hash seam** at the wrap (partial last cell; gw<3 double counts). Per-axis cell size.
5. **Physarum `Random2` chirality bias** at large ids; same seed pattern copied into `spawn.hlsl`.
6. Perf, measure first: groupshared tiles for trail/biome diffuse (72 loads/px for 8-type
   physarum at 4k); fuse the three perception builds; per-sim resolution/perception scale for the
   131-agent termites; skip composite on step-less frames (or just `targetFPS` = `simRate`);
   `AsyncGPUReadback` + threaded PNG encode for FigureExporter takes; Generate-flow+advect fusion.
7. Scene config (11.2): `showDebugGrid`/labels on in all 5 scenes; `stepEvery`/`metabolismEvery` 1,
   `perceptionResScale` 0.75 (doc: 2–4 / 2–4 / 0.25); `targetFPS 90` vs `simRate 60`; two HDRP
   cameras; receiver `selfDrive` on although manager drives it; `debugOutputMat` set with no renderer;
   unassign `recorderTarget` when not recording.
8. 11.2 media not in git at all (`.gitignore` ignores `data/`; no LFS rule) — takes not reproducible
   from a clone; ~3 GB ProRes would ship in a player build.
9. Verify intent: 7588c5d switched `Scene_SIGGRAPH` to 3100×1000 / receiver off; e06f3e8 termite
   `wallBuildAmount` 0.003→0.03; 5453471 DiurnalSun disabled + seeder routes in `Scene_SIGGRAPH DAC`.

## Open / next session
1. Exit Play mode → let Unity compile; check Console for shader errors; A/B one `_DAC_4k` frame
   against `main` (should be identical apart from the debug grid's half precision).
2. Decide deferred 1–5 (each changes the look).
3. Keep or drop `com.unity.pipeline` in the manifest.
