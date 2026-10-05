---
status: closed
date: 2026-10-04
tags: [session, performance, allocations, gpu, measurement, biomes]
related: [[2026-10-04-deslop-perf-stack]], [[2026-10-03-biomes-siggraph-runtime-pass]], [[../ARCHITECTURE]], [[../../Assets/Workspace/11.0 Biomes/docs/PERFORMANCE]]
---
# Measured follow-ups to the Biomes perf/deslop work

Branch `perf/measured-followups` off `main` @ `ebb41b6`: the open items of
[[2026-10-04-deslop-perf-stack]], one commit per item. GPU changes were applied only when
measured faster and output-identical.

## Shipped
- `d8eaaba` — `CopyAllChannels` → `CopyChannelsExcept(xy, skipMask)` wording in
  `11.0 Biomes/docs/PERFORMANCE.md` and `INTEGRATION_DESIGN.md`.
- `b60e843` + `f6e8904` — dead noise terrain removed: `Biome.compute` `channelCount` /
  `noiseScale` / `noiseThreshold`, their `Biome.cs` feeders and the unused `random.hlsl` include;
  then (user's call) the MFT "noiseScale" knob, `BiomeFieldConfig.noiseScale/noiseThreshold`
  and the `MIDI_OSC.md` mention. Comments/tooltip that still said permeability relaxes toward
  the noise terrain now name `permOpenBaseline`. No other dead uniforms in the 10 computes;
  `random.hlsl` untouched.
- `50983a7` — `SimTimeline.Play()` calls `PlayAll()` for a `playOnStart` group, else `StopAll()`;
  the warning is gone, every take restarts the queue from waypoint 0. A second
  `ParameterInterpolator.Play()` in the same frame lands in the same state (both calls snapshot
  the same untouched clones at the same step, before any `Update`). No shipped scene sets the
  group's `playOnStart`.
- `0e35053` — `ParameterRecorder`: per-sim flat `float[]` snapshots, double-buffered, no keys or
  `ParseKey`. Type probing kept, so the event stream is unchanged.
- `2c1981c` — `PngExport`: per-size `ReadbackCache` owned by `FigureExporter` (released on
  stop, disable and after `ExportAll`), `EncodeNativeArrayToPNG`, file written through a reused
  64 KB buffer.
- `0ba9fe9` — `includes/torus.hlsl` `WrapTorus` (int2 + uint2) replaces integer `%` in the sims'
  diffuse + structure-tensor taps, `WriteTrails`, and the biome diffuse taps.
- `9bbd8ea` — `InjectStampKernel` cull: every group thread tests every 64th stamp and
  `InterlockedOr`s hits into the mask.
- `20aa2af` — boid `MoveAgentsKernel` runs in cell-sorted order (thread j = sorted boid j,
  result to the boid's own slot); `BoidSim.cs` binds `sortedIndicesRead` for Move.

## Measured
Headless batchmode copy of the project, M4 Max, `_DAC_4k` (method and table:
[[../../Assets/Workspace/11.0 Biomes/docs/PERFORMANCE|PERFORMANCE]] §4c).
- Recorder: 20 264 → 0 B/frame (29 180 → 0 with a CA sim), ~42 → ~4 µs/frame; 826 and 1 622
  events bit-identical to the old algorithm under random edits and a type-count change.
- PNG: 4K save 13.6 MB → 3.9 KB managed, 386 → 376 ms (encode ≈ 370 ms of it); 1024×576
  420 KB → 2.7 KB; bytes identical to HEAD (sRGB + linear, ARGBHalf + ARGBFloat). An 11-frame
  export wrote all 5 sources, held 2 cached sizes, 0 after Stop, leaked no textures.
- 6a `GetKernelThreadGroupSizes`: 25 ns/call, 0 B → ~1 µs/frame. **Not applied.**
- 6b wraps: termite diffuse −10 %; with `trailAnisotropy` 0.5 physarum −5 %, boid −7 %,
  termite −11 %; biome PDE unchanged.
- 6c inject cull: 0.087 → 0.045 ms at 135 stamps (0.068 → 0.023 at 32, 0.173 → 0.138 at 512).
- 6d boid Move: hash + Move 0.48 → 0.22 ms dispersed, 1.66 → 0.64 ms in post-spawn clusters.
- All three GPU changes: frame median 10.43 → 10.17 ms (−2.4 %, every interleaved pair).

## Verification
- Output identity per GPU change: same-snapshot readbacks bit-identical (6b: diffuse ±anisotropy
  for all sims, termite step, `Biome.Step`; 6c: 4 stamp counts; 6d: Move). Physarum/boid full
  steps differ only at their own A/A race level.
- Live Editor after `unity pipeline install`: EditMode 78/78; Metal gate 10/10 computes,
  0 errors / 0 warnings after a forced reimport; 35/35 `FindKernel` names declared.
- `dotnet build` of Assembly-CSharp(+Editor) at every code commit (stale csproj: pipeline refs
  stripped, `--no-restore`). `Packages/manifest.json` + lock reverted after.

## Decided
- Benchmarks ran in an APFS copy of the project driven headless (batchmode Editor + Pipeline):
  the screen was locked, so the live Editor could not take focus to load the package. Absolute
  ms differ from the live Editor (no Game view present); only A/B deltas are reported.
- `GC.GetAllocatedBytesForCurrentThread()` returns 0 in Unity's Mono; allocations were read from
  `ProfilerRecorder("GC Allocated In Frame")` deltas, exact within a frame.
- The recorder keeps the 8-type probe rather than `LiveParamSet.TypeCount`, which would change the
  events (CA sims ignore the index and record each change 8×).
- The readback cache is FigureExporter-owned, not static: one-shot button exports keep
  create/destroy, nothing outlives the component or a domain reload.
- 6d needs no float-sum caveat: each boid's neighbour iteration order is unchanged.
- Async PNG encode not done: optional, and the encoder alone was proven byte-identical.

## Open / next session
1. Async frame-export encode (AsyncGPUReadback + `EncodeNativeArrayToPNG` on a worker): ~370 ms
   per 4K frame left on the main thread. Readback-vs-`ReadPixels` byte equality still untested.
2. Agent sims set `rezX/rezY` only in `Reset()` / `FadeStep()`: a `.compute` edit in Play leaves
   `rez = 0` until the next reset (Editor-only). Bind them per step if live shader iteration matters.
3. CA sims record each param change 8× (probe quirk, preserved). Switching to `TypeCount` changes
   recordings — decide.
4. `BiomeFieldConfig` assets still carry serialized `noiseScale` / `noiseThreshold` until next save.
5. Carried over: Play-check `b565d19` (repeated sim resets, recording with an unstarted sim,
   edit-mode Export PNGs).
