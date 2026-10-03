---
status: closed
date: 2026-10-03
tags: [session, review, performance, regression, biomes, siggraph, runtime, hdrp]
related: [[2026-10-03-biomes-siggraph-regression-perf-review]], [[../ARCHITECTURE]], [[../ROADMAP]], [[../../Assets/Workspace/11.0 Biomes/docs/PERFORMANCE]]
---
# 11.0 Biomes + 11.2 SIGGRAPH — pass 5: runtime verification in Unity

Follow-on to [[2026-10-03-biomes-siggraph-regression-perf-review]] (passes 1–4, static only — the
Editor was in Play mode throughout). This pass drove the live Editor via the Unity CLI (Pipeline
package) to verify the branch, profile it, and hunt what static review could not see. Two parallel
reviewers covered the per-frame code passes 1–4 never touched (network/media inputs, MIDI/OSC/
timeline control); a closure reviewer checked this pass's commits. Rule unchanged: output-identical
or regression fixes only; look-changing items go to *Deferred*.

## Verified (open item 1 of the previous log — closed)
- Unity's own Metal compile: all 10 compute shaders under `11.0 Biomes` — 0 errors, 0 warnings
  (`ShaderUtil.GetComputeShaderMessages`); 34/34 `FindKernel` names are declared kernels.
- EditMode tests in Unity: 78/78 before and after this pass's code changes.
- Full `_DAC_4k` take in Play (120 s, frame export off in memory): all 14 timeline cues fired in
  order (starts, stops/fades, two ResetSimsOnly, interpolators, debug video) — 0 errors, 0 warnings.
  All 5 scenes play with 0 errors (only the pre-existing Game-view aspect notice).
- CPU side is clean: `SimulationManager.FixedUpdate` ≈ 0.3 ms; PlayerLoop scripts allocate 0 B/frame
  (the ~32 KB/frame GC seen in Play is EditorLoop). The frame is GPU-bound.

## Shipped

### Performance — scene cameras (all 5 scenes, bit-identical)
Every scene rendered **two full HDRP cameras** into the 3840×2160 Game view: `Main Camera`
(depth −1, full rect) was completely overdrawn by `Rendering Camera` (depth 0, clears to Sky), and
the scene is unlit quads, yet HDRP ran shadow cascades (soft-shadow Directional Light), SSAO/SSR,
volumetrics, motion vectors and transparency passes every frame.
- `Main Camera` → Camera component off (GameObject + AudioListener kept). Nothing reads it: no
  `Camera.main`, Unity Recorder targets tag `Recording`.
- `Rendering Camera` → custom frame settings with ShadowMaps, ContactShadows, ScreenSpaceShadows,
  Shadowmask, SSAO, SSR, SSGI, Volumetrics (+reprojection), VolumetricClouds, (Object)MotionVectors,
  Decals, SSS, Transmission, RoughRefraction, Distortion, Transparent pre/post/low-res, LightLayers,
  Reflection/Planar probes **off**. Post-processing, fog and custom passes untouched, so Volume
  edits still behave.
- Proof: HDRP `StandardRequest` renders (RGBAHalf, sims running) are byte-identical with and
  without each change in every scene; a per-setting sweep showed only ColorGrading/Postprocess
  change pixels (kept on).

| Scene_SIGGRAPH, 1 sim step/frame | ms/frame |
|---|---|
| before (2 cameras, default frame settings) | 26.2–30.0 |
| Main Camera off | 18.8–21.1 |
| + trimmed frame settings | **12.1–13.0** |

One HDRP camera costs ≈ 7.9 ms at 3840×2160; trimmed ≈ 4.2 ms. `_DAC_4k` now holds real time
with all three sims at 4K: 60.0 sim steps/s, ~32 fps, game time = wall time.

### Fixes (`11.0 Biomes/src/components/network/`)
- **OSC server killed by one early message** (pre-window, live in `Scene_SIGGRAPH DAC`) — OscJack
  ends its receive thread for good on the first callback exception; `/p_* /b_* /t_*` before a
  timeline-started sim's cue hit null `agentParams`, after which `/index` (that scene's only firing
  source), `/inject/*` and resets were silently dead. Every callback now goes through `On()`
  (try/catch → main-thread `LogException`), param callbacks drop messages until `LiveParamSet`
  exists, and the never-firing `"*"` monitor is gone (OscJack's monitor key is `""`). Verified
  live: params sent at t≈1–3 s, then `/index 4321` and `/index 777` both landed.
- **Injector kept stamping from stopped termites** (regression, 7588c5d) — `AgentPositions`
  dispersal predates stop/fade states and was never gated: after StopTermites it pinned pulses at
  frozen positions (forever in `Scene_SIGGRAPH DAC`, where TD drives `/index`), and after a manager
  reallocation it read back a released buffer. Now Running-only, like the manager's own deposits
  and `StopSim`'s contract. *Look changes in the last 7 s of the DAC takes (113–120 s) and after.*
- `ExternalTextureSender`: toggling a stream's `enabled` in Play rebuilds (was ignored; a disabled
  live stream kept sending); teardown is edit-mode safe (`DestroySafe`, the Rebuild button).
- `MidiFighterTwister`: cross-field knobs no longer re-pack + re-upload three unchanged channel
  buffers per CC (their four scalars are re-set from `fieldConfig` every PDE step); `SendCC` uses a
  stack span instead of `new byte[3]` per message.

## Decided
- Camera/frame-settings changes count as output-identical: proven per scene, per setting.
- Kernel-level GPU timing is not obtainable through Editor eval: Unity does not order a readback
  fence behind compute dispatches, so wall-clock-per-dispatch reads as noise. Component deltas
  (toggle a sim off, measure ms/frame at 1 step/frame via `Time.captureDeltaTime`) are the method;
  per-kernel numbers need an Xcode GPU capture / Metal System Trace.
- Not fixed: `NeuronFiringSource` re-reads the 47 MB blob after disable→enable→Reset — no scene
  reaches it, and a clean fix must keep the blob-less TestScene path.

## Deferred (new this pass; previous list still stands minus the two-camera item)
1. **MFT biome knobs mutate the shared `BiomeFieldConfig` asset** (bank 2, column 15) — no runtime
   clone, so edits survive Play and leak into the other scene sharing it (`Scene_SIGGRAPH`,
   `_DAC_4k`), and reach disk on any SaveAssets. Fix = live clone like `liveUmwelt`. Changes behaviour.
2. `NeuronFiringPlayback.Play()` after a finished non-loop pass does nothing (only Restart).
3. NDI: KlakNDI needs width % 16, height % 8. The saved stream (0.1× → 310×100) and a full-res
   3100-wide composite don't qualify; KlakNDI warns and encodes anyway (frames unverified).
4. `InjectStampKernel` loops every stamp at every biome pixel (≤135 × 1024×576); a per-stamp bbox
   dispatch is output-identical. Estimated sub-ms, unmeasured.
5. Remaining GPU is the 4K agent sims: per step physarum ≈ 5 ms, boids ≈ 4.8 ms, termites
   ≈ 1.7 ms (`_DAC_4k`); grid, PDE (1024×576) and recorder blit are within noise. This is where
   previous deferred #6 (tiled trail diffuse, half-rez tensor spec, fused perception builds) pays.

## Open / next session
1. Decide on `com.unity.pipeline` (0.8.0-exp.1): added to `Packages/manifest.json` + lock to drive
   the Editor, **left uncommitted**. Keep it for CLI-driven checks or `git checkout Packages/`.
2. Decide deferred 1–5 above and 1–5, 7–9 of the previous log.
3. If 4K headroom is still wanted: GPU-capture one `_DAC_4k` frame for per-kernel cost before
   building any of deferred #5.
