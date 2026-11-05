# Primordial Particle System Sim — Plan

**Date:** 2026-10-20
**Spec:** [[../specs/2026-10-20-primordial-sim-design]]
**Branch:** `feat/primordial-sim`

The order is set by risk. Gate 1 (cells emerge) blocks everything after it.

1. **CPU reference + tests (TDD).** `core_math/PrimordialMotion.cs`: `TurnDelta`, `IsRight`,
   `Step` (synchronous, torus, minimum image). `PrimordialMotionTests`: handedness, turn-then-move,
   α = 180° isolated particles return within two steps, the sign(0) branch, the seam neighbour.
2. **Base hooks.** `SimulationBase.TrailLayerCount` (default `TypeCount + 1`) used by `Allocate`;
   `OscPrefix` (default first letter, lowercase) used by `OSCMapping`.
3. **Params.** `PrimordialParams` + `PrimordialAgentType` (`IAgentColorParams`), and
   `AgentFamily.Primordial` appended.
4. **Kernel + sim.** `primordial_type_params.hlsl`, `PrimordialSim.compute`, `PrimordialSim.cs`
   (derived count cached in `Reset`, hash sizing, substeps, `BindFadeParams`, density layers).
   `PrimordialKernelTests`: GPU Move vs `PrimordialMotion.Step`, struct mirror; render test row.
5. **Gate 1.** A throwaway rig in Play (Scatter, paper defaults, no umwelt) → `ExportPNG` →
   look for cells. Fix before continuing.
6. **Coupling.** Drift + agitation in Move (already in the kernel, zero by default); check one
   cell survives a uniform gradient.
7. **Integration.** Manager buttons, timeline actions, OSC reset + prefix, MFT colour/type
   count, params inspector, `MIDI_OSC.md`.
8. **Scene.** `Editor/PrimordialSceneSetup.cs` → `11.4 PPS Scene/Scene_PPS.unity`, its
   assets (params, umwelts, field config) and materials. Tune in Play: creatures + veins.
9. **Docs.** ARCHITECTURE §3.4, README, ROADMAP, INDEX, ADR (space units + derived count),
   session log. Revert the pipeline package from `Packages/` before the merge.
