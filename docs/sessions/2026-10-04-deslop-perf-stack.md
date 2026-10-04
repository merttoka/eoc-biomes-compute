---
status: closed
date: 2026-10-04
tags: [session, deslop, refactor, fixes, biomes]
related: [[2026-10-04-deferred-behaviour-fixes]], [[2026-10-03-biomes-siggraph-regression-perf-review]], [[../ARCHITECTURE]]
---
# Deslop pass over the Biomes perf/behaviour-fix stack

Scope: everything unmerged on top of `main` (`4db77a3..`, 30 code files) — the
`review/biomes-siggraph-perf-regression` review plus [[2026-10-04-deferred-behaviour-fixes]].
Committed on `review/deferred-behaviour-fixes` as three commits.

## Shipped
- `9039a1d` refactor, no behaviour change:
  - `GPUResourceManager.DestroySafe` / `DestroyTexture` (statics) replace five Play/edit-mode
    destroy copies and six hand-written sender RT frees.
  - Shared helpers for code the stack edited in lockstep: `SimulationBase.BindTypeParams` and a
    default trail `Render()` (three identical agent-sim copies), `Biome.BindDebugRender` (grid,
    `RenderChannelTo`, `RenderChannelNormalizedTo`), `SimulationManager.DispatchComposite`,
    receiver `HaltDebugVideo`, playback `Rewind`, `spawn.hlsl` `SpawnHash2`/`SpawnHeading`,
    boid `CellOf` (binning and neighbour lookup must agree).
  - Removed `_blendPrimed` (always `_blendStepSerial == -1`), dead `RandomDirection2`, the one-use
    `NeuronFiringOrDummy`, dead null guards; history-narrating comments rewritten as WHY.
- `5b2023b` fixes found by `/code-review` (11 kept):
  - Boid separation/cohesion used the raw, unwrapped diff: a neighbour in range across the seam
    pushed with a canvas-width vector the wrong way. Now minimum-image (`ToroidalDiff`); the
    torus-tiling grid had made those pairs common.
  - Spawn: resets run at `time = 0`, where the position hash and the heading hash shared a seed
    (heading = f(x); bottom-edge spawns fanned toward the centre). `SpawnHash2` is salted.
  - Keep-out eviction clamps to the border when every pushed side is off-canvas (regressed vs `main`).
  - frameBlend: double clocks (a float `Time.time` quantized the weight after ~a day of uptime),
    step interval measured when a cell rig drives the steps, recomposite while stepping stalls
    (paused mixer / StopSim / keep-out edits never showed).
  - Live pacing edits restore the project's vsync / targetFrameRate when `vSyncDivisor` and
    `limitFPS` are both off.
  - Receiver tracks blank output; `IsDebugVideoStopped` → `IsOutputBlank`. The seeder no longer
    SetTowards zeros into its channel when debug input is switched off in Play.
  - Sender: an `enabled` / protocol / name edit rebuilds only that stream (the others' servers
    stay up); the NDI crop keys on the protocol the backend was built with;
    `[DefaultExecutionOrder(1001)]` so crop/downscale copies read this frame's composite.
  - Injector drops agent readbacks requested before a stop; `PngExport` restores the right active RT.
- `b565d19` follow-up fixes:
  - `SimulationBase.CloneParams`: `Reset()` destroys the previous params clone (all five sims
    leaked one per reset). Only the clone it made is destroyed, never the serialized slot.
  - Agent sims' `Get/SetParameter` no-op before the first `Reset()` (null slot; MIDI / recorder
    could NRE), as the CA sims already did; `ParameterRecorder` skips unstarted sims, so starting
    one no longer records a burst of fake change events.
  - Edit-mode-safe destroys in `Biome.ExportPNGs` and the receiver (`gpu.Release` for tracked RTs).

## Verification
- C#: `dotnet build` clean for both assemblies at every commit; `9039a1d` also built in isolation.
  Unity compile clean, with a reflection probe confirming the new members were loaded.
- Metal: 10/10 compute shaders, 0 errors / 0 warnings after a forced reimport
  (`com.unity.pipeline` reinstalled for the check, then removed). 35/35 `FindKernel` names declared.
- EditMode 78/78 — the suite covers `Biomes.Core` / `Biomes.Sequencer.Core` only, no runtime components.
- Play-checked after `5b2023b` (looks right). `b565d19` is compile-verified only.

## Decided
- `/code-review` fixes reverted: debug-grid staleness while paused (documented trade-off of the
  visibility gating); a `Biome.RuntimeFieldConfig` accessor + MFT rewrite (only for reassigning
  `fieldConfig` mid-Play). Composite vs per-sim PNG desync under frameBlend left as documented.
- No `SpawnPosition` helper covering mode 0: BoidSim declares its neuron uniforms after
  including `spawn.hlsl`.
- GPU perf ideas not taken, unmeasured: thread-group-size caching, `%` → conditional wraps,
  inject-cull atomics, boid sorted-order Move.

## Open / next session
1. Merge order unchanged: `review/biomes-siggraph-perf-regression` first, then this branch.
2. Play-check `b565d19`: repeated sim resets, recording with an unstarted sim, edit-mode Export PNGs.
3. Stale doc references to `CopyAllChannels`: `11.0 Biomes/docs/PERFORMANCE.md:119`,
   `INTEGRATION_DESIGN.md:127`.
4. Pre-existing dead shader code: `Biome.compute` `channelCount` / `noiseScale` / `noiseThreshold`;
   `random.hlsl` `Map` / `Random1` / `SimplexNoise` / `FractalSimplexNoise`.
5. Policy calls left open: SimTimeline's `playOnStart` skip applies to every Play; inspector
   `simRate` edits don't apply live; `ParameterRecorder` / `PngExport` per-frame allocations.
