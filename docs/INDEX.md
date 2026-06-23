---
status: living
date: 2026-04-26
tags: [meta, index]
---
# Docs Index

## Architecture (living)
- [[ARCHITECTURE]] — system reference (Unity runtime + memory overview)
- [[migration]] — memory architecture plan + open questions
- [[../Assets/Workspace/11.0 Biomes/docs/PERFORMANCE]] — M4 exhibition perf deep dive (memory, dispatch, agent budgets)

## Specs / plans
- [[superpowers/specs/2026-06-08-osc-neuron-firing-design]] — OSC-driven shared neuron firing ([[superpowers/plans/2026-06-08-osc-neuron-firing|plan]])
- [[superpowers/specs/2026-06-07-parameter-interpolator-design]] — slow preset crossfade interpolator

## Sessions (newest first)
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
