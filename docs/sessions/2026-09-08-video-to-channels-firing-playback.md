---
status: closed
date: 2026-09-08
tags: [session, biome, video, playback, shanghai]
related: [[../ARCHITECTURE]], [[../adr/0006-osc-neuron-firing]], [[../adr/0014-neuron-layout-single-owner]]
---
# Video → biome channels + local firing playback

Prep for the Shanghai cut: DG's videos (`11.2 SIGGRAPH Scene/videos/*.mov`, gitignored) need to
reach the ecosystem, and the organoid blob needs to play a fixed range without TD.

## Shipped
- **Finding:** 11.0's `externalInfluenceTex` is dead — `SimulationManager` assigns it to every sim,
  no 11.0 sim or compute reads it (only the 10.0 Physarum/Boid computes did). The receiver's texture
  only ever reached the composite overlay.
- `Biome.compute` `SeedChannelKernel` gained `seedSwizzle` (R/G/B/A/luma); `Biome.SeedChannelFromTexture`
  gained a `SeedSource` param (default Red — existing CA publish call unchanged) and skips
  un-created RenderTextures.
- `network/TextureChannelSeeder.cs` — routes `{from, channel, gain, mode}` over a receiver's
  `OutputTexture` (or `textureOverride`), `seedEvery` decimation, "Add RGB → Temperature/Oxygen/Nutrient"
  button. `SimulationManager.textureSeeder` + step 3.6 hook after `injector.Inject`.
- `core_math/FramePlayhead.cs` — pure fractional playhead over an inclusive range (loop / hold,
  seek, swap reversed range). `Assets/Tests/EditMode/FramePlayheadTests.cs`, 9 tests.
- `network/NeuronFiringPlayback.cs` — `startFrame/endFrame/durationSeconds/loop`, Play/Pause/
  Restart/Stop buttons, pushes via `SetFrame` (same entry as OSC `/index`). Defaults to the Shanghai
  range 125000–131000 over 120 s.
- `11.2 SIGGRAPH Scene/assets/DAC_params/<NN_Name>/` — ten `BiomeFieldConfig` + 3×`UmweltMapping` sets for
  the video-in-Excitability cut (01 Trace, 02 Negative, 03 Pooling, 04 Currents minimal; 05 Ecotone,
  06 LongExposure, 07 Storm, 08 TermiteCity, 09 Predation, 10 Breath complex). Generated from the DAC
  baseline; `DAC_params/README.md` carries the per-set idea + recommended seeder mode. Finding: the DAC
  physarum umwelt's `Excitability Avoidance -2` was a no-op (avoidance clamps negative weights to 0).
  Each folder also gets `Scene_DAC_<NN_Name>.unity`: the DAC scene with the four asset GUIDs rewired and
  the seeder route set per set (06 → Additive 0.02). Snapshots of the parent scene, not linked to it.
- Verified: `dotnet build` of `Assembly-CSharp` + `Biomes.Sequencer.Tests` clean; tests run via a
  reflection runner against Unity's nunit (Editor had the project open, batchmode unavailable).
  Compute change not yet compiled by the Editor at time of writing.

## Decided
- Video enters as **ecology, not force**: raster → channel → Umwelt, never a direct steering
  texture. Same seam as the injector so the PDE owns it afterwards. Not promoted to an ADR — it
  reuses the ADR-0011-era `SeedChannelKernel` path rather than adding a new one.
- Playback is a separate component, not a mode inside `NeuronFiringSource` — keeps ADR-0014's
  single-owner contract and ADR-0006's "OSC = playhead" symmetric (both are just `SetFrame` callers).
- Scene wiring left to the Editor (scene file is mid-edit in the working tree).

## Open / next session
1. Wire in `Scene_SIGGRAPH`: `TextureChannelSeeder` on `TextureIO` → `SimulationManager.textureSeeder`;
   receiver `Debug Use Video Input` + clip; `NeuronFiringPlayback` on `NetworkIO` → source.
2. Confirm `Biome.compute` compiles in the Editor (swizzle branch) and eyeball the Biome debug grid.
3. Extract the debug `VideoPlayer` out of `ExternalTextureReceiver` into an `ExternalVideoReader`
   (seeder already takes a `Texture`, so only the receiver changes).
4. Audition the ten DAC_params sets against the DG clip; keep 2–3, retune. If the clip fights the sims,
   re-render from `data/Algorithms-main-jarrett` (`Algorithms.toe` / `Neural.toe`) or stream TD → Syphon
   straight into the receiver (same seeder path, no clip).
5. Optional: OSC `/play`, `/seek` onto `NeuronFiringPlayback` for TD-side transport.

## Parked
- `tools/channel_wiring.py` — per-set channel-wiring boards (writers → channels → readers, PDE
  couplings, three-level row dimming) generated from the `DAC_params` assets + scene and pushed to
  Paper (*SIGGRAPH DAC Shanghai* › *EoC Interaction Map*, next to the poster). Verdict: less
  informative than hoped; parked, kept for later. Usage in `tools/README.md`.
- Not committed: `ProfilerCaptures/` and `11.2 SIGGRAPH Scene/data/` (TD project, 2.9 GB) — now
  gitignored; un-ignore deliberately if the `.toe` should travel with the repo.
