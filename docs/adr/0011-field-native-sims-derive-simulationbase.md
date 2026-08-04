---
status: accepted
date: 2026-08-02
tags: [adr, sim, cellular-automata, engine-core, gpu]
related: [[../ARCHITECTURE]], [[../sessions/2026-08-02-cellular-automata-sims]], [[../superpowers/specs/2026-07-23-cellular-automata-sims-design]], [[0008-clear-in-place-reset]]
---
# ADR-0011: Field-native sims derive from `SimulationBase` with a sealed no-op agent contract

## Context
Every sim so far is an **agent** sim. `SimulationBase` reflects that: it owns per-type trail
`Texture2DArray`s, agent buffers, and `MoveAgents`/`WriteTrails`/`Diffuse` kernels, and its
abstract contract demands `GetAgentPositionBuffer()` / `GetAgentCount()` / `TypeCount`. A
cellular automaton uses none of it — its entire state is one integer per cell.

`SimulationManager` holds `List<SimulationBase>` and already null-guards the agent path in the
two places that matter: the write-back loop skips a sim whose position buffer is null
(`SimulationManager.cs:335`), and the perception build skips a sim with no umwelt or perception
texture (`:306`).

Options: (a) a parallel `FieldSimulationBase` beside `SimulationBase`, plus a shared `ISimLayer`
interface the manager iterates; (b) derive from `SimulationBase` and answer the agent contract
with no-ops.

## Decision
(b). `FieldSimulationBase : SimulationBase`, with the agent contract **sealed**:
`GetAgentPositionBuffer() => null`, `GetAgentCount() => 0`, `TypeCount => 1`. `Allocate()` is
overridden wholesale so a CA's compute shader is never asked for kernels it does not declare;
the base's trail/agent machinery is skipped rather than left dead.

This required one seam in `SimulationBase`: extract `MarkAllocated()` and make
`NeedsAllocation()` `virtual`. The clear-in-place allocation signature (`_allocRezX`,
`_allocTypeCount`, …) is private, so a subclass overriding `Allocate()` could never stamp it —
`NeedsAllocation()` would stay true forever, every `Reset()` would reallocate, `outTex` would
change instance each time, and downstream Syphon servers would tear down and re-announce on
every reset. That is precisely the failure [[0008-clear-in-place-reset|ADR-0008]] exists to
prevent, and it would have been reintroduced silently.

The base also **mandates** the double-buffered `stateRead`/`stateWrite` pair. The ported CCA
already ping-ponged, but the ported CA2D bound a single texture as both read and write — threads
reading neighbours other threads had already overwritten. Owning the pair at this level makes
that race inexpressible in a rule, not merely discouraged.

## Consequences
- **Zero orchestrator change.** A CA drops into `simulations` and the existing null-guards route
  around it. It gets the additive 8-layer composite, `compositeWeight`, MIDI/OSC string params,
  per-type reset and neuron-firing binding for free.
- **Sealed, not virtual.** A field sim having agents is a category error, not a variation.
- **`MarkAllocated()` is now load-bearing.** Any future `Allocate()` override must call it. The
  signature fields stay private so it remains the only way to write them.
- **Inspector cost.** Field sims inherit agent-oriented serialized fields (dispersal speed
  response, behavior multipliers, `umwelt`, `renderPersistence`) that are meaningless for a grid
  process. Cosmetic; a custom editor would fix it if it starts confusing authoring.
- **The next field sim is a subclass, not a refactor.** Reaction-diffusion is the obvious one.
- CA output rides the composite's linear-clamp UV sampler, so a CA may run at a fraction of sim
  resolution (`cellResolutionScale`) and upscale for free — the mitigation for Moore's
  O((2r+1)²) per-cell cost.

## Related
[[0008-clear-in-place-reset]] · [[0005-includes-copied-verbatim]] ·
[[../superpowers/specs/2026-07-23-cellular-automata-sims-design]] ·
`Assets/Workspace/11.0 Biomes/src/components/core/FieldSimulationBase.cs`
