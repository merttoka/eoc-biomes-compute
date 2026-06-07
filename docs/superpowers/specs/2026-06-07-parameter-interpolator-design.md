---
title: ParameterInterpolator design
date: 2026-06-07
status: approved
tags: [11.0-biomes, parameters, interpolation, installation]
---

# ParameterInterpolator

Slowly interpolate a simulation's live parameters from its current state through
a chainable queue of saved preset assets ("waypoints"), sim-step driven, for
long-running installations.

## Goal

Crossfade `agentParams` (the live runtime clone) from **current live state** →
target preset A → target preset B → ... over time, advancing on simulation steps
(not wall-clock). Per-parameter enable toggles let the operator exclude specific
params (e.g. `hue`, `moveSpeed`) so they stay frozen while everything else moves.

## Decisions (locked)

- **Scope:** one `ParameterInterpolator` per sim (add two components for both sims).
- **Driver:** `SimulationManager.SimStepCount` (frame-rate independent; auto-pauses
  when the sim pauses).
- **Surface:** Option A — field-level raw interpolation on the params object. All
  modulatable params, not just a subset path.
- **"From" state:** snapshot of the sim's current **live** values, re-taken at the
  start of each leg.
- **"To" states:** a `List<ScriptableObject>` of preset `.asset` files (waypoints),
  played in order (chainable queue).
- **Per-param enable/disable:** per param-**name** (applies across all agent types).
  Disabled = frozen (never written).
- **Timing:** single global `durationSteps` + `holdSteps` + easing curve applied to
  **all** transitions (not per-waypoint).
- **Hue:** shortest-arc circular lerp on 0–1.
- **Queue end:** stop & hold at the final waypoint.
- **Play:** always restarts from waypoint 0.
- **Inspector:** live read-only progress readout (state, current waypoint, t%).

## Existing constraints discovered

- Each sim holds `paramsSO` (saved asset, untouched) and `agentParams`
  (runtime `Instantiate` clone, mutated by MIDI/OSC/recorder). The clone is the
  interpolation target.
- `GPUStep()` calls `UploadTypeParams()` every step, re-reading `agentParams` into
  the GPU buffer — so mutating the live object is sufficient; no GPU plumbing.
- The param-name→field mapping (e.g. Boid `"separateRange"` → field
  `separationRange`) currently lives only inside each Sim's `Get/SetParameter`
  switch. A target `.asset` has no attached Sim, so by-name raw access must exist
  at the **data layer** (the ScriptableObject).
- `agentParams` is recreated on `Reset()`, so the interpolator must read it live
  via an accessor, never cache it.

## Architecture

### 1. `IParamSet` (new, `src/params/IParamSet.cs`)

By-name raw access to a params object, whether live clone or on-disk asset:

```csharp
public interface IParamSet
{
    int TypeCount { get; }
    float GetValue(string name, int typeIndex);
    void SetValue(string name, int typeIndex, float raw);
    (float min, float max) GetRange(string name);
}
```

### 2. `BoidParams` / `PhysarumParams` implement `IParamSet`

- `TypeCount => types.Count`
- `GetValue` / `SetValue`: a raw get/set switch over the modulatable names,
  mirroring the field mapping already in each Sim (incl. `separateRange` →
  `separationRange`, `foodSeek` → `foodSeekingStrength`). Raw — no range mapping.
- `GetRange` already exists.

(The existing Sim `Get/SetParameter` are left unchanged to limit blast radius;
they could later delegate to these, but that refactor is out of scope.)

### 3. `SimulationBase.LiveParamSet` (new abstract property)

```csharp
public abstract IParamSet LiveParamSet { get; }
```

- `PhysarumSim` / `BoidSim`: `public override IParamSet LiveParamSet => agentParams;`
  (one line each).

### 4. `ParameterInterpolator : MonoBehaviour` (new, `src/components/utils/`)

Sibling of `ParameterRecorder`.

**Inspector fields:**
- `SimulationManager simManager`, `int simIndex` — resolves target sim + clock.
- `List<ScriptableObject> waypoints` — ordered target preset assets.
- `int durationSteps = 600` — steps per transition (global).
- `int holdSteps = 0` — dwell at each waypoint before next leg (global).
- `AnimationCurve easing` — default ease-in-out (the "slowly" shaping).
- `List<ParamToggle> paramToggles` where `{ string name; bool enabled = true; }` —
  auto-filled by `[Button] Refresh Param List` from `sim.ModulatableParams`.
- `[Button]`s: Play, Pause, Stop, Skip to Next.
- Read-only state shown in inspector: phase (Idle/Interpolating/Holding/Done),
  current waypoint index, `t` percent.

**State:**
- `bool isPlaying`, `int currentWaypoint`, `int legStartStep`, `enum Phase`.
- `Dictionary<string, float[]> _from` — snapshot of live values per param-name,
  one float per type index, taken at leg start.

**Flow (per step, in `Update`, gated on `SimStepCount`):**

1. On **Play**: `currentWaypoint = 0`; snapshot live → `_from`;
   `legStartStep = SimStepCount`; phase = Interpolating.
2. Each step while Interpolating:
   - `elapsed = SimStepCount - legStartStep`
   - `t = clamp01(elapsed / durationSteps)`, `te = easing.Evaluate(t)`
   - target = `waypoints[currentWaypoint] as IParamSet`
   - for each **enabled** param name, for `i` in `0 .. min(live.TypeCount, target.TypeCount)`:
     - `from = _from[name][i]`, `to = target.GetValue(name, i)`
     - `v = name == "hue" ? LerpHue01(from, to, te) : Mathf.Lerp(from, to, te)`
     - `live.SetValue(name, i, v)`
   - when `t >= 1`: if `holdSteps > 0` → phase = Holding; else advance.
3. Holding: when `elapsed >= durationSteps + holdSteps` → advance.
4. **Advance:** `currentWaypoint++`; if past end → stop & hold (isPlaying = false,
   phase = Done); else re-snapshot live → `_from`, `legStartStep = SimStepCount`,
   phase = Interpolating.

**Hue shortest-arc (0–1):**
```csharp
float d = Mathf.Repeat(b - a + 0.5f, 1f) - 0.5f;
return Mathf.Repeat(a + d * t, 1f);
```

**Edge handling:**
- Target with fewer types than live → extra live types stay frozen.
- Wrong-type / non-`IParamSet` asset assigned → unknown names return 0 from
  `GetValue`; skip those names and log a warning once.
- Empty waypoint list or null sim → no-op with warning.

## Files

| File | Change |
|---|---|
| `src/params/IParamSet.cs` | new interface |
| `src/params/BoidParams.cs` | implement `IParamSet` (GetValue/SetValue) |
| `src/params/PhysarumParams.cs` | implement `IParamSet` (GetValue/SetValue) |
| `src/components/core/SimulationBase.cs` | add abstract `LiveParamSet` |
| `src/components/Sim/BoidSim.cs` | `LiveParamSet => agentParams` |
| `src/components/Sim/PhysarumSim.cs` | `LiveParamSet => agentParams` |
| `src/components/utils/ParameterInterpolator.cs` | new component |

No changes to existing Sim `Get/SetParameter` or any GPU/compute code.

## Testing / verification

Unity has no unit-test harness wired here; verification is in-editor:
- Assign sim + 2–3 preset assets, set short `durationSteps`, Play → observe live
  `agentParams` fields sweep toward each waypoint, hold, advance, stop at last.
- Disable `hue` + `moveSpeed` toggles → those fields stay frozen while others move.
- Confirm `hue` 0.9 → 0.1 takes the short way (through 1.0/0.0), not the long sweep.
- Pause/Stop/Skip behave; progress readout updates.

The pure helper `LerpHue01` is static and trivially unit-testable if a test
assembly is later added.
