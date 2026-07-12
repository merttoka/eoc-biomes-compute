---
status: closed
date: 2026-07-11
tags: [session, sim, timing, fixedupdate, hdrp, determinism]
related: [[../ARCHITECTURE]], [[superpowers/specs/2026-07-11-fps-independent-sim-design]], [[../adr/0009-per-show-scene-workspaces]]
---
# FPS-independent sim — fixed 60 Hz timestep + decoupled render

Drove the sim on a fixed 60 Hz clock (`FixedUpdate`) with the composite render decoupled
to `LateUpdate`, so sim speed is now identical across installs regardless of render FPS.
60 Hz = legacy per-frame feel, so **no content re-tuning**. Spec + Tasks 1–2 landed
earlier this session; this handoff closes Task 3 (scene + doc migration).

## Shipped
Five commits on `main` (`b5e0d10` → `76e4e06`), unpushed until wrap-up.

- **Spec** (`b5e0d10`): fps-independent sim via fixed 60 Hz timestep + decoupled render.
- **Task 1 — deterministic RNG** (`3b9f7ce`): `SimulationBase` seeds the shader `time`
  from a monotonic per-sim `_simStep` (`WrappedStep`, wraps 65536), not `Time.frameCount`.
  Zeroed in `Reset()`, incremented in `Step()`. No-op at 1 step/frame; correctness-critical
  once multiple `Step()`s run per rendered frame (catch-up / `stepsPerTick>1`).
- **Task 2 — fixed-timestep driver** (`53a717c`): `SimulationManager` — `Update()` →
  `FixedUpdate()` (loops `Step()` `stepsPerTick`×), composite `Render()` → `LateUpdate()`
  (unwelded from `Step()`). New fields `simRate` (60), `maxAllowedTimestep` (0.1,
  spiral-of-death guard), `stepsPerTick` (`[FormerlySerializedAs("stepsPerFrame")]`);
  retired `stepMod`. `Time.fixedDeltaTime = 1/simRate` set in `Awake`.
- **MIDI rebind** (`7184bdd`): MidiFighterTwister rebound stepsPerFrame→`stepsPerTick`/
  `simRate`; added `SimulationManager.ApplySimRate()` so a live `simRate` change re-applies
  `Time.fixedDeltaTime` mid-run (MIDI-driven).
- **Task 3 — scene + doc migration** (`76e4e06`): `Scene_CURRENTS`, `TestScene` YAML
  migrated `stepsPerFrame`+`stepMod` → `simRate: 60`/`maxAllowedTimestep: 0.1`/
  `stepsPerTick: 1` (SIGGRAPH already done in prior work). Hand-edited to match SIGGRAPH's
  already-migrated block — deterministic, no in-editor pass needed. ARCHITECTURE §3.1
  (FixedUpdate/simRate/LateUpdate/maxAllowedTimestep driver) + §3.2 (render not part of
  `Step()`). All 3 scenes verified: zero `stepMod`/`stepsPerFrame` keys remain.

## Decided
- **60 Hz canonical, per-scene overridable** — equals legacy `targetFPS 60 / stepsPerFrame 1
  / stepMod 1`, so existing content survives untouched.
- **`maxAllowedTimestep` = Unity's `Time.maximumDeltaTime`** — weak installs run timed loops
  *long*, never *fast*: sim slows uniformly under load rather than bursting catch-up steps.
- **Scene YAML hand-edited, not in-editor migrated** — a sibling scene (SIGGRAPH) already
  produced the canonical field block; replicating it is deterministic + reviewable. Unity
  reads the new keys natively (`[FormerlySerializedAs]` becomes a no-op for these scenes).
- **No new ADR this session** — decision captured in spec + ARCHITECTURE §3.1. Promote to an
  ADR later if the timing model needs a standalone decision record.

## Open / next session
1. **Play-mode verification (not yet run)** — the FPS-independence proof: play `TestScene`
   at `targetFPS 60`, then `30`, then uncapped; confirm `SimStepCount` grows ~60/sec in all
   three (sim wall-clock speed identical, only render smoothness differs). Plan Task 2 Steps 7–8.
2. **Injector integration check** (spec §5) — fire a firing/dispersal event at `targetFPS 30`
   vs uncapped; the async agent-position readback must not assume one `Step()`/frame. If the
   dispersal desyncs in wall-clock terms, that's a follow-up bug.
3. **Housekeeping** — `PhysarumParams.asset` (SIGGRAPH) carries unrelated MIDI param tuning,
   left uncommitted.
4. Consider an ADR for the fixed-timestep timing model if it accrues more constraints.
