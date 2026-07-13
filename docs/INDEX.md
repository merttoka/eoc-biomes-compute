---
status: living
date: 2026-04-26
tags: [meta, index]
---
# Docs Index

## Architecture (living)
- [[ARCHITECTURE]] — system reference (Unity runtime + memory overview)
- [[migration]] — memory architecture plan + open questions
- [[ROADMAP]] — backlog + deeper-biology roadmap (shipped / in design / below), verified against code
- [[../Assets/Workspace/11.0 Biomes/docs/PERFORMANCE]] — M4 exhibition perf deep dive (memory, dispatch, agent budgets)

## Specs / plans
- [[superpowers/specs/2026-07-11-fps-independent-sim-design]] — fixed 60 Hz timestep (`FixedUpdate`) + render decoupled to `LateUpdate`; FPS-independent sim ([[superpowers/plans/2026-07-11-fps-independent-sim|plan]])
- [[../Assets/Workspace/11.0 Biomes/docs/INTERACTION_DESIGN_II]] — follow-on interaction design: outside-signal routing (audio/organoid/sensors), generalizing field→agent beyond the 4 perception slots, lifecycle (death→succession). Builds on INTEGRATION_DESIGN after Humidity shipped
- [[../Assets/Workspace/11.0 Biomes/docs/INTEGRATION_DESIGN]] — layer/sim coupling exploration: stamp injector (shipped), Q10/decay, Humidity (shipped), perm-topography, mortality
- [[superpowers/specs/2026-06-08-osc-neuron-firing-design]] — OSC-driven shared neuron firing ([[superpowers/plans/2026-06-08-osc-neuron-firing|plan]])
- [[superpowers/specs/2026-06-07-parameter-interpolator-design]] — slow preset crossfade interpolator

## Sessions (newest first)
- [[sessions/2026-07-13-resolution-independent-params]] — spatial + trail-density params scale by `rezY/2160` on Reset (resolution-independent, grounded at 2160; no-op at reference); + 3-agent backlog audit + new ROADMAP
- [[sessions/2026-07-11-diurnal-sun]] — diurnal sun: procedural `BiomeInjector` source sweeps Temperature L→R, phased off the neuron playhead (one blob playthrough = one day); kept indirect (no temp reads) — retuned temp diffuse/evap/Q10/flow + cut metabolic heat for headroom; OSC per-family resets
- [[sessions/2026-07-11-fps-independent-sim]] — fixed 60 Hz timestep via `FixedUpdate`; composite render decoupled to `LateUpdate`; sim RNG seeded from monotonic sim step; `stepsPerFrame`+`stepMod` → `simRate`/`maxAllowedTimestep`/`stepsPerTick`; all 3 scenes migrated
- [[sessions/2026-06-23-humidity-channel]] — Humidity biome channel (11→12): high-diffusion, flow-advected, relaxes to ambient baseline, Temperature evaporates it (`|∇Humidity|` = termite build cue); both Homeostatic assets + docs updated
- [[sessions/2026-06-17-per-show-scene-split]] — split 11.0 Biomes into per-show folders (11.1 CURRENTS, 11.2 SIGGRAPH); engine stays in 11.0; organoid blob rename; Unity 6 build profile + HDRP
- [[sessions/2026-06-13-reset-clear-in-place]] — clear-in-place reset (stable GPU resources) kills Syphon reset teardown/flash; OSC reset main-thread marshal
- [[sessions/2026-06-10-branch-validation-equilibrium-fix]] — branch validation, 15-agent review (9 findings), homeostatic equilibrium fix, boid 0–64 rescale, scene adopts homeostatic config
- [[sessions/2026-06-09-ecosystem-io-investigation]] — ecosystem/IO investigation: GPU-free richness, mush fix, neuron-display redesign, injector ergonomics
- [[sessions/2026-06-09-second-performance-pass]] — second perf pass: perception downscale, ring compaction, boid coalescing, persistence knob
- [[sessions/2026-06-09-osc-neuron-firing]] — shared OSC-driven neuron firing + ring overlay
- [[sessions/2026-04-26-split-and-daemon-v0]] — repo split via rsync, memory daemon v0

## ADRs (newest first)
- [[adr/0009-per-show-scene-workspaces]] — one workspace folder per show; shared engine stays in `11.0 Biomes/`
- [[adr/0008-clear-in-place-reset]] — sim reset clears GPU resources in place (stable `outTex` → no Syphon teardown)
- [[adr/0007-mass-conserving-diffusion-relax-channels]] — diffusion operator gated per channel class (homeostatic vs stigmergic)
- [[adr/0006-osc-neuron-firing]] — neuron firing is an external OSC-driven shared signal
- [[adr/0005-includes-copied-verbatim]] — `Includes/` not vendored
- [[adr/0004-td-as-orchestration-hub]] — TouchDesigner as central hub
- [[adr/0003-local-first-storage]] — SQLite + LanceDB per node
- [[adr/0002-folder-as-event-log]] — snapshot folder is source of truth
- [[adr/0001-rsync-over-filter-repo]] — split via plain rsync

## Memory daemon
- [[../memory/README]]
- [[../memory/docs/osc-contract]]
