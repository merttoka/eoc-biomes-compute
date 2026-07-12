# FPS-Independent Simulation — Design Spec

**Date:** 2026-07-11
**Workspace:** `Assets/Workspace/11.0 Biomes`
**Goal:** Decouple simulation *speed* from render *frame rate* so the same assets
play at the same wall-clock pace across every install and GPU. Drive the sim on a
fixed 60 Hz timestep (`FixedUpdate`), render separately (`LateUpdate`), and seed the
agent RNG from the monotonic sim clock so the sim is genuinely deterministic across
hardware.

---

## 1. Motivation

The sim has **no notion of time**. `SimulationManager.Update()`
(`src/components/core/SimulationManager.cs:199-204`) runs `Step()` a fixed number of
times per *rendered frame*:

```csharp
void Update()
{
    if (Time.frameCount % stepMod == 0)
        for (int i = 0; i < stepsPerFrame; i++)
            Step();
}
```

So **sim speed = (FPS ÷ stepMod) × stepsPerFrame** steps/sec. Every rate in the
system — agent movement, diffusion, decay, firing envelopes — is tuned *per-step*
(confirmed: position integrates as `position += direction` with no `dt`, e.g.
`computes/PhysarumSim.compute:167`; diffusion/decay are per-step fractions,
`computes/Biome.compute:287-330`). The current mitigation,
`Application.targetFrameRate = targetFPS` (`SimulationManager.cs:119`), only pins the
rate *if the hardware sustains it*. A heavier scene or slower install that dips below
`targetFPS` silently runs the whole sim in slow motion — the cross-hardware drift
that makes common assets hard to manage.

The fix is a **fixed-timestep accumulator**, implemented with Unity's built-in
`FixedUpdate` machinery. Every rate stays per-step; we only make `Step()` fire at a
fixed wall-clock rate regardless of render FPS. **No shader or parameter math
changes** — safe for the PDE (diffusion/advection) stability limits that a
`dt`-scaling approach would violate.

**Why FixedUpdate is free of side effects here:** the active project has **zero**
`FixedUpdate` methods and **zero** physics (`Rigidbody`/`Physics.`/collision) code,
so `Time.fixedDeltaTime` and `Time.maximumDeltaTime` are ours to repurpose with no
collateral damage. Unity already runs an internal accumulator that calls
`FixedUpdate` 0..N times per frame to track wall-clock, capped by
`Maximum Allowed Timestep` — that cap *is* the spiral-of-death guard, for free.

---

## 2. Current state (what exists)

- **Driver:** `SimulationManager.Update()` — sole sim clock, gated by
  `Time.frameCount % stepMod`; runs `Step()` `stepsPerFrame` times
  (`SimulationManager.cs:199-204`).
- **Render welded to sim:** `Render()` is called at the *end* of every `Step()`
  (`SimulationManager.cs:309`). If the loop runs `Step()` 3× in a frame, it
  composites 3× and discards the first two.
- **Timing knobs:** `stepsPerFrame` (0–10) and `stepMod` (1–50), integer
  multiplier/divider on the frame-locked rate (`SimulationManager.cs:17-18`).
  `targetFPS`/`limitFPS` pin render rate (`:19-20`, applied `:114-121`).
- **Sim clock:** `_simStepCount` — monotonic, increments once per `Step()`
  (`SimulationManager.cs:208`); already drives per-step decimation
  (`metabolismEvery`, `:257-259`). Reset to 0 on `Reset()` (`:135`).
- **RNG seed (the determinism hole):** `SimulationBase.WrappedFrame =>
  Time.frameCount % TimeWrap` (`SimulationBase.cs:225`) is uploaded to shaders as
  `time` and used *only* as the per-agent RNG seed (e.g.
  `computes/PhysarumSim.compute:69,73`). It is sourced from the **render frame
  counter**, not the sim step — so any two `Step()`s in the same frame share an
  identical seed.
- **Init path:** `OnEnable() → Reset()` (`SimulationManager.cs:447-451`) primes state
  and runs one `Render()` (`:163`) before any update runs, so a decoupled render
  always has state to composite. Manual `Reset()`/`ExportPNG()` buttons unaffected.
- **Project time settings** (`ProjectSettings/TimeManager.asset`): `Fixed Timestep:
  0.02`, `Maximum Allowed Timestep: 0.1`.

---

## 3. Design

### 3.1 Two loops, cleanly split

| Concern | Runs in | Rate | Job |
|---|---|---|---|
| **Sim** | `FixedUpdate()` | fixed `simRate` = 60/s, HW-independent | `Step()` — sense/move/writeback/biome PDE |
| **Render** | `LateUpdate()` | render FPS (`targetFPS` cap) | `Render()` — composite + overlays |

Unity runs `FixedUpdate` before `LateUpdate` within a frame, so the composite always
sees the latest sim state. On fast HW `FixedUpdate` runs 0..1× per frame (sim holds
60 Hz, render free-runs); on slow HW it runs ≤ N× bounded by `Maximum Allowed
Timestep` (uniform slowdown, never a burst).

### 3.2 New / changed fields on `SimulationManager`

- **`simRate`** (new, serialized `float`, default `60`). In `Awake()`:
  `Time.fixedDeltaTime = 1f / simRate`. It is a per-instance field, so **each scene's
  `SimulationManager` can override it** for artistic pacing while staying
  FPS-independent.
- **`maxAllowedTimestep`** (new, serialized `float`, default `0.1`). In `Awake()`:
  `Time.maximumDeltaTime = maxAllowedTimestep`. Tooltip (verbatim intent):

  > *"Spiral-of-death guard. Caps how much real time one frame may hand to the fixed
  > sim loop. At 60 Hz, 0.1 s = at most ~6 catch-up sim steps per rendered frame. If
  > a frame takes longer than this, the extra time is dropped: the sim slows down
  > uniformly instead of exploding into a burst of steps that would make the next
  > frame even slower. Lower = safer/steadier under load but the sim lags real-time
  > sooner; higher = tracks real-time harder but risks stutter on a hitching machine.
  > Weak installs will run timed loops long, never fast."*

- **`stepsPerTick`** (renames `stepsPerFrame`, serialized `int`, default `1`; carry
  `[FormerlySerializedAs("stepsPerFrame")]` so existing scene values aren't silently
  reset on the rename). Applied inside `FixedUpdate`:
  `for (int i = 0; i < stepsPerTick; i++) Step();`. A
  fast-forward multiplier that stays FPS-independent because it runs *per fixed tick*,
  not per render frame. Tooltip must flag the sharp edge:

  > *"Sim steps per fixed tick. 2 = double-speed sim, still hardware-independent — but
  > it multiplies per-tick GPU cost with no change to tick scheduling, so a high value
  > can push a marginal install into the Maximum Allowed Timestep clamp (where it then
  > also fails to hold sim rate). A dev/artist tool, not a shipping default; content
  > tuned with it will not survive a port to weaker hardware."*

- **`stepMod`** — **retired.** Its job (dividing the frame-locked rate) is subsumed by
  `simRate`.
- **`targetFPS` / `limitFPS`** — **kept**, now purely a render/thermal cap with no sim
  coupling. `Awake()` still sets `Application.targetFrameRate`/`vSyncCount` under
  `limitFPS`.

### 3.3 The three code moves

1. **Move the driver.** Delete the `Update()` frame-gate (`:199-204`). Add:
   ```csharp
   void FixedUpdate() { for (int i = 0; i < stepsPerTick; i++) Step(); }
   void LateUpdate()  { Render(); }
   ```
2. **Unweld render.** Remove the trailing `Render();` from `Step()` (`:309`). `Step()`
   becomes pure simulation; the composite runs exactly once per rendered frame.
   `Reset()` keeps its explicit `Render()` (`:163`) so editor buttons still composite.
3. **Set the timestep.** In `Awake()`, before/around the existing `limitFPS` block:
   `Time.fixedDeltaTime = 1f / simRate;` and
   `Time.maximumDeltaTime = maxAllowedTimestep;`.

### 3.4 RNG-seed fix (folded in) — determinism across hardware

Reseed the shader RNG from the **monotonic sim clock**, not the render frame counter,
so consecutive `Step()`s in one frame (catch-up or `stepsPerTick > 1`) get distinct
seeds and the sim advances identically regardless of frame pacing.

- `SimulationBase` gains a per-sim monotonic step counter, incremented once at the top
  of its `Step()` and zeroed on `Reset()` (mirrors the manager's `_simStepCount`).
- `WrappedFrame` becomes `WrappedStep => _simStep % TimeWrap` (rename for clarity;
  same `TimeWrap = 65536`, ~18 min of steps at 60 Hz — the existing precision-wrap
  rationale is unchanged).
- The shader upload site (`s_TimeID`) and the `TimeWrap` comment update to say
  "sim step" instead of "frame". No shader code changes — same value semantics, better
  source.

This is the difference between "consistent *speed*" and "actually *deterministic*",
and it removes RNG correlation on every degraded/fast-forward path.

---

## 4. Honest limits & non-goals

- **"Render separately" decouples scheduling, not GPU cost.** `Step()` is 20–30 GPU
  dispatches and the composite shares the same GPU. On a GPU-bound install (heavy
  physarum) sim *is* the bottleneck, so render can't outrun it — you get "sim 60 Hz,
  render 60 Hz," not "sim 60 Hz, render 120 Hz." The benefit is **speed consistency
  and uniform slowdown**, not free rendering headroom.
- **"Same wall-clock everywhere" holds only where 60 Hz is sustained.** Unity drops
  leftover accumulator time at the clamp (never banks it), so a slow install runs a
  timed loop *long*, not fast — correct and safe, but not an unconditional guarantee.
- **No render interpolation (YAGNI).** At 60 Hz per-step motion is already smooth and
  the field is slow; interpolation would mean double-buffering every output texture
  for a marginal gain. Out of scope unless visible stepping is reported.
- **60 Hz preserves the current look.** Today's `targetFPS 60 / stepsPerFrame 1 /
  stepMod 1` already equals 60 steps/s, so **no content re-tuning is required.**

---

## 5. Verification

- **Rate independence:** run at `targetFPS 30`, `60`, and uncapped (`limitFPS` off) on
  the same scene; confirm a fixed landmark (e.g. `SimStepCount` after 10 s of
  wall-clock, or a timed dispersal/firing event) lands at the same sim state within
  ±1 step across all three.
- **Graceful degradation:** force a heavy scene below 60 fps; confirm the sim slows
  smoothly (no stutter/burst) and never runs *faster* than 60 Hz on fast HW.
- **Determinism:** with a fixed input sequence, confirm identical output at
  `stepsPerTick = 1` vs a frame-rate that triggers catch-up (multiple `Step()`s/frame)
  — this exercises the RNG-seed fix.
- **No regression:** editor `Reset()`/`ExportPNG()` buttons still composite; OSC neuron
  firing and external video input still drive (now consumed at fixed 60 Hz).
- **Integration check (implementation-time):** confirm the injector's async
  agent-position readback (recent change) doesn't assume one `Step()` per frame once
  step and frame cadence diverge.

---

## 6. Files touched

- `src/components/core/SimulationManager.cs` — new `simRate` / `maxAllowedTimestep` /
  `stepsPerTick` fields + tooltips; retire `stepMod`; `Awake()` timestep setup;
  `FixedUpdate`/`LateUpdate` drivers; unweld `Render()` from `Step()`.
- `src/components/core/SimulationBase.cs` — per-sim monotonic step counter;
  `WrappedFrame` → `WrappedStep` seeded on it; reset-to-zero; comment/upload-site
  wording.
- Scene(s) using `stepsPerFrame`/`stepMod` in the inspector — re-serialize to the new
  fields (`TestScene`, `Scene_CURRENTS`, `Scene_SIGGRAPH`).
- `docs/ARCHITECTURE.md` — update §3.1/§3.2 (the sim is now FixedUpdate-driven at a
  fixed `simRate`; render is decoupled).

---

## 7. Unresolved questions

✅ No unresolved questions.
