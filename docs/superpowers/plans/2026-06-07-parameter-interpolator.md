# ParameterInterpolator Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a sim-step-driven `ParameterInterpolator` component that slowly crossfades a simulation's live parameters from current state through an ordered queue of preset `.asset` waypoints, with per-parameter enable toggles and shortest-arc hue.

**Architecture:** A small data-layer seam (`IParamSet` interface implemented by `BoidParams`/`PhysarumParams`, surfaced on the sim via `SimulationBase.LiveParamSet`) gives by-name raw access to any params object — live clone or on-disk asset. The `ParameterInterpolator` MonoBehaviour reads the live "from" snapshot and each waypoint "to" through that interface, lerping per type each simulation step. `GPUStep()` already re-uploads `agentParams` every step, so mutating the live object is sufficient — no GPU changes.

**Tech Stack:** Unity (HDRP), C#, EasyButtons (already used project-wide for `[Button]`). No test-runner harness exists; verification is Unity compile + in-editor behavior.

**Spec:** `docs/superpowers/specs/2026-06-07-parameter-interpolator-design.md`

**Working directory:** `Assets/Workspace/11.0 Biomes/`

**Note on `.meta` files:** Unity generates `.cs.meta` on import. After each task that creates a `.cs` file, let Unity recompile (focus the editor) so it generates the `.meta`, then `git add` both the `.cs` and its `.meta`. If working headless, add `.cs` now; the `.meta` will appear on next Unity import and should be committed then.

---

### Task 1: `IParamSet` interface

**Files:**
- Create: `Assets/Workspace/11.0 Biomes/src/params/IParamSet.cs`

- [ ] **Step 1: Create the interface**

```csharp
namespace Biomes
{
    /// <summary>
    /// By-name raw access to a parameter set, whether it is a live runtime clone
    /// (agentParams) or an on-disk preset asset (paramsSO). Values are raw (not
    /// 0-1 normalized). Used by ParameterInterpolator to read "from"/"to" states.
    /// </summary>
    public interface IParamSet
    {
        int TypeCount { get; }
        float GetValue(string name, int typeIndex);
        void SetValue(string name, int typeIndex, float raw);
        (float min, float max) GetRange(string name);
    }
}
```

- [ ] **Step 2: Verify Unity compiles**

Focus the Unity Editor, wait for recompile. Expected: Console shows no errors (interface alone compiles).

- [ ] **Step 3: Commit**

```bash
git add "Assets/Workspace/11.0 Biomes/src/params/IParamSet.cs"*
git commit -m "params: add IParamSet interface"
```

---

### Task 2: `BoidParams` implements `IParamSet`

**Files:**
- Modify: `Assets/Workspace/11.0 Biomes/src/params/BoidParams.cs`

Note the name→field mapping mirrors `BoidSim.GetParameter`: `separateRange`→`separationRange`, `alignRange`→`alignmentRange`, `attractRange`→`attractionRange`, `foodSeek`→`foodSeekingStrength`. `GetRange` already exists on the class.

- [ ] **Step 1: Change the class declaration**

Change line `public class BoidParams : ScriptableObject` to:

```csharp
    public class BoidParams : ScriptableObject, IParamSet
```

- [ ] **Step 2: Add `IParamSet` members**

Insert immediately after the existing `GetRange` method (keep the existing `GetRange` — it already satisfies the interface):

```csharp
        public int TypeCount => types.Count;

        public float GetValue(string name, int typeIndex)
        {
            if (typeIndex < 0 || typeIndex >= types.Count) return 0f;
            var t = types[typeIndex];
            return name switch
            {
                "separateRange" => t.separationRange,
                "alignRange"    => t.alignmentRange,
                "attractRange"  => t.attractionRange,
                "maxSpeed"      => t.maxSpeed,
                "maxForce"      => t.maxForce,
                "depositAmount" => t.depositAmount,
                "eatAmount"     => t.eatAmount,
                "foodSeek"      => t.foodSeekingStrength,
                "hue"           => t.hue,
                "saturation"    => t.saturation,
                "diffuseRate"   => t.diffuseRate,
                _ => 0f,
            };
        }

        public void SetValue(string name, int typeIndex, float raw)
        {
            if (typeIndex < 0 || typeIndex >= types.Count) return;
            var t = types[typeIndex];
            switch (name)
            {
                case "separateRange": t.separationRange     = raw; break;
                case "alignRange":    t.alignmentRange      = raw; break;
                case "attractRange":  t.attractionRange     = raw; break;
                case "maxSpeed":      t.maxSpeed            = raw; break;
                case "maxForce":      t.maxForce           = raw; break;
                case "depositAmount": t.depositAmount       = raw; break;
                case "eatAmount":     t.eatAmount           = raw; break;
                case "foodSeek":      t.foodSeekingStrength = raw; break;
                case "hue":           t.hue                = raw; break;
                case "saturation":    t.saturation          = raw; break;
                case "diffuseRate":   t.diffuseRate         = raw; break;
            }
        }
```

- [ ] **Step 3: Verify Unity compiles**

Focus Unity, wait for recompile. Expected: no Console errors. (If "BoidParams does not implement IParamSet.GetRange" appears, the existing `GetRange` signature already matches `(float,float) GetRange(string)` — confirm it is `public`.)

- [ ] **Step 4: Commit**

```bash
git add "Assets/Workspace/11.0 Biomes/src/params/BoidParams.cs"
git commit -m "params: BoidParams implements IParamSet"
```

---

### Task 3: `PhysarumParams` implements `IParamSet`

**Files:**
- Modify: `Assets/Workspace/11.0 Biomes/src/params/PhysarumParams.cs`

Physarum modulatable names match field names directly (`moveSpeed`, `senseAngle`, `turnAngle`, `senseDistance`, `depositAmount`, `eatAmount`, `diffuseRate`, `hue`, `saturation`).

- [ ] **Step 1: Change the class declaration**

Change `public class PhysarumParams : ScriptableObject` to:

```csharp
    public class PhysarumParams : ScriptableObject, IParamSet
```

- [ ] **Step 2: Add `IParamSet` members**

Insert immediately after the existing `GetRange` method:

```csharp
        public int TypeCount => types.Count;

        public float GetValue(string name, int typeIndex)
        {
            if (typeIndex < 0 || typeIndex >= types.Count) return 0f;
            var t = types[typeIndex];
            return name switch
            {
                "moveSpeed"     => t.moveSpeed,
                "senseAngle"    => t.senseAngle,
                "turnAngle"     => t.turnAngle,
                "senseDistance" => t.senseDistance,
                "depositAmount" => t.depositAmount,
                "eatAmount"     => t.eatAmount,
                "diffuseRate"   => t.diffuseRate,
                "hue"           => t.hue,
                "saturation"    => t.saturation,
                _ => 0f,
            };
        }

        public void SetValue(string name, int typeIndex, float raw)
        {
            if (typeIndex < 0 || typeIndex >= types.Count) return;
            var t = types[typeIndex];
            switch (name)
            {
                case "moveSpeed":     t.moveSpeed     = raw; break;
                case "senseAngle":    t.senseAngle    = raw; break;
                case "turnAngle":     t.turnAngle     = raw; break;
                case "senseDistance": t.senseDistance = raw; break;
                case "depositAmount": t.depositAmount = raw; break;
                case "eatAmount":     t.eatAmount     = raw; break;
                case "diffuseRate":   t.diffuseRate   = raw; break;
                case "hue":           t.hue           = raw; break;
                case "saturation":    t.saturation    = raw; break;
            }
        }
```

- [ ] **Step 3: Verify Unity compiles**

Focus Unity, wait for recompile. Expected: no Console errors.

- [ ] **Step 4: Commit**

```bash
git add "Assets/Workspace/11.0 Biomes/src/params/PhysarumParams.cs"
git commit -m "params: PhysarumParams implements IParamSet"
```

---

### Task 4: Expose live params on the sim base

**Files:**
- Modify: `Assets/Workspace/11.0 Biomes/src/components/core/SimulationBase.cs`
- Modify: `Assets/Workspace/11.0 Biomes/src/components/Sim/BoidSim.cs`
- Modify: `Assets/Workspace/11.0 Biomes/src/components/Sim/PhysarumSim.cs`

- [ ] **Step 1: Add abstract property to `SimulationBase`**

In `SimulationBase.cs`, find the IControllableSim interface block:

```csharp
        // IControllableSim interface
        public abstract IReadOnlyList<string> ModulatableParams { get; }
        public abstract void SetParameter(string paramName, int index, float value);
        public abstract void SetParameterDelta(string paramName, int index, float delta);
        public abstract float GetParameter(string paramName, int index);
```

Add one line after `GetParameter`:

```csharp
        /// <summary>Live runtime params (agentParams) exposed for interpolation.</summary>
        public abstract IParamSet LiveParamSet { get; }
```

- [ ] **Step 2: Implement in `BoidSim`**

In `BoidSim.cs`, after the `agentParams` field declaration block (after `public BoidParams agentParams;`), add:

```csharp
        public override IParamSet LiveParamSet => agentParams;
```

- [ ] **Step 3: Implement in `PhysarumSim`**

In `PhysarumSim.cs`, after `public PhysarumParams agentParams;`, add:

```csharp
        public override IParamSet LiveParamSet => agentParams;
```

- [ ] **Step 4: Verify Unity compiles**

Focus Unity, wait for recompile. Expected: no Console errors. (Both sims must override the new abstract member or compilation fails — that is the check.)

- [ ] **Step 5: Commit**

```bash
git add "Assets/Workspace/11.0 Biomes/src/components/core/SimulationBase.cs" \
        "Assets/Workspace/11.0 Biomes/src/components/Sim/BoidSim.cs" \
        "Assets/Workspace/11.0 Biomes/src/components/Sim/PhysarumSim.cs"
git commit -m "sim: expose LiveParamSet for interpolation"
```

---

### Task 5: `ParameterInterpolator` component

**Files:**
- Create: `Assets/Workspace/11.0 Biomes/src/components/utils/ParameterInterpolator.cs`

- [ ] **Step 1: Create the component**

```csharp
using System.Collections.Generic;
using UnityEngine;
using EasyButtons;

namespace Biomes
{
    /// <summary>
    /// Slowly interpolates one sim's live parameters from its current state through
    /// an ordered queue of preset assets (waypoints), advancing on simulation steps.
    /// For long-running installations. One component per sim.
    /// </summary>
    public class ParameterInterpolator : MonoBehaviour
    {
        public enum Phase { Idle, Interpolating, Holding, Done }

        [System.Serializable]
        public class ParamToggle
        {
            public string name;
            public bool enabled = true;
        }

        [Header("References")]
        public SimulationManager simManager;
        public int simIndex = 0;

        [Header("Waypoints (target preset assets, played in order)")]
        public List<ScriptableObject> waypoints = new();

        [Header("Timing (simulation steps)")]
        [Min(1)] public int durationSteps = 600;
        [Min(0)] public int holdSteps = 0;
        public AnimationCurve easing = AnimationCurve.EaseInOut(0, 0, 1, 1);

        [Header("Per-parameter enable (click Refresh after assigning sim)")]
        public List<ParamToggle> paramToggles = new();

        [Header("Progress (read-only)")]
        [SerializeField] private Phase phase = Phase.Idle;
        [SerializeField] private int currentWaypoint;
        [SerializeField, Range(0f, 1f)] private float progress;

        // "from" snapshot: paramName -> value per type index, taken at each leg start
        private readonly Dictionary<string, float[]> _from = new();
        private int _legStartStep;
        private bool _warnedWrongType;

        private SimulationBase Sim =>
            (simManager != null && simIndex >= 0 && simIndex < simManager.simulations.Count)
                ? simManager.simulations[simIndex] : null;

        private int StepNow() => simManager != null ? simManager.SimStepCount : 0;

        // ─────────── Param list ───────────

        [Button("Refresh Param List")]
        public void RefreshParamList()
        {
            var sim = Sim;
            if (sim == null) { Debug.LogWarning("ParameterInterpolator: no sim resolved (check simManager/simIndex)"); return; }

            var prev = new Dictionary<string, bool>();
            foreach (var t in paramToggles) prev[t.name] = t.enabled;

            paramToggles.Clear();
            foreach (var name in sim.ModulatableParams)
                paramToggles.Add(new ParamToggle
                {
                    name = name,
                    enabled = prev.TryGetValue(name, out bool e) ? e : true,
                });
        }

        private bool IsEnabled(string name)
        {
            foreach (var t in paramToggles)
                if (t.name == name) return t.enabled;
            return true; // not listed -> default on
        }

        // ─────────── Transport ───────────

        [Button("Play")]
        public void Play()
        {
            var sim = Sim;
            if (sim == null || sim.LiveParamSet == null) { Debug.LogWarning("ParameterInterpolator: no sim/live params (enter Play mode and Reset sims first)"); return; }
            if (waypoints == null || waypoints.Count == 0) { Debug.LogWarning("ParameterInterpolator: no waypoints assigned"); return; }

            currentWaypoint = 0;
            _warnedWrongType = false;
            SnapshotFrom();
            _legStartStep = StepNow();
            phase = Phase.Interpolating;
            progress = 0f;
        }

        [Button("Pause")]
        public void Pause()
        {
            if (phase == Phase.Interpolating || phase == Phase.Holding)
                phase = Phase.Idle;
        }

        [Button("Stop")]
        public void Stop()
        {
            phase = Phase.Idle;
            progress = 0f;
        }

        [Button("Skip to Next")]
        public void SkipToNext()
        {
            if (phase == Phase.Interpolating || phase == Phase.Holding)
                Advance();
        }

        // ─────────── Drive ───────────

        void Update()
        {
            if (phase != Phase.Interpolating && phase != Phase.Holding) return;
            var sim = Sim;
            if (sim == null || sim.LiveParamSet == null) return;

            int elapsed = StepNow() - _legStartStep;

            if (phase == Phase.Interpolating)
            {
                float t = Mathf.Clamp01(durationSteps > 0 ? (float)elapsed / durationSteps : 1f);
                progress = t;
                ApplyLeg(sim.LiveParamSet, easing.Evaluate(t));

                if (t >= 1f)
                {
                    if (holdSteps > 0) phase = Phase.Holding;
                    else Advance();
                }
            }
            else // Holding
            {
                if (elapsed >= durationSteps + holdSteps)
                    Advance();
            }
        }

        private void ApplyLeg(IParamSet live, float te)
        {
            var target = waypoints[currentWaypoint] as IParamSet;
            if (target == null)
            {
                if (!_warnedWrongType)
                {
                    Debug.LogWarning($"ParameterInterpolator: waypoint {currentWaypoint} is not an IParamSet preset; skipping leg");
                    _warnedWrongType = true;
                }
                return;
            }

            int typeCount = Mathf.Min(live.TypeCount, target.TypeCount);
            foreach (var kv in _from)
            {
                string name = kv.Key;
                if (!IsEnabled(name)) continue;
                float[] fromArr = kv.Value;
                for (int i = 0; i < typeCount && i < fromArr.Length; i++)
                {
                    float from = fromArr[i];
                    float to = target.GetValue(name, i);
                    float v = name == "hue" ? LerpHue01(from, to, te) : Mathf.Lerp(from, to, te);
                    live.SetValue(name, i, v);
                }
            }
        }

        private void Advance()
        {
            currentWaypoint++;
            if (currentWaypoint >= waypoints.Count)
            {
                currentWaypoint = waypoints.Count - 1;
                phase = Phase.Done;
                progress = 1f;
                return;
            }
            SnapshotFrom();
            _legStartStep = StepNow();
            phase = Phase.Interpolating;
            progress = 0f;
        }

        private void SnapshotFrom()
        {
            _from.Clear();
            var sim = Sim;
            var live = sim.LiveParamSet;
            int typeCount = live.TypeCount;
            foreach (var name in sim.ModulatableParams)
            {
                var arr = new float[typeCount];
                for (int i = 0; i < typeCount; i++)
                    arr[i] = live.GetValue(name, i);
                _from[name] = arr;
            }
        }

        /// <summary>Shortest-arc hue interpolation on 0..1 (wraps through 1/0).</summary>
        public static float LerpHue01(float a, float b, float t)
        {
            float d = Mathf.Repeat(b - a + 0.5f, 1f) - 0.5f;
            return Mathf.Repeat(a + d * t, 1f);
        }
    }
}
```

- [ ] **Step 2: Reason through `LerpHue01` (no test runner)**

Manually verify the shortest-arc math for the spec's case (hue 0.9 → 0.1):
- `b - a + 0.5 = 0.1 - 0.9 + 0.5 = -0.3`; `Repeat(-0.3, 1) = 0.7`; `d = 0.7 - 0.5 = 0.2`.
- At `t=1`: `Repeat(0.9 + 0.2, 1) = Repeat(1.1, 1) = 0.1` ✓ (arrives at target).
- At `t=0.5`: `Repeat(0.9 + 0.1, 1) = Repeat(1.0, 1) = 0.0` ✓ (passes through 0, the SHORT way, not back down through 0.5).
- Sanity, the long way would be `d ≈ -0.8` giving midpoint 0.5 — confirmed NOT what this produces.

- [ ] **Step 3: Verify Unity compiles**

Focus Unity, wait for recompile. Expected: no Console errors. `ParameterInterpolator` appears as an addable component, with EasyButtons buttons (Refresh/Play/Pause/Stop/Skip) and read-only progress fields visible.

- [ ] **Step 4: Commit**

```bash
git add "Assets/Workspace/11.0 Biomes/src/components/utils/ParameterInterpolator.cs"*
git commit -m "feat: ParameterInterpolator component"
```

---

### Task 6: In-editor verification

**Files:** none (manual verification + docs)

- [ ] **Step 1: Wire up in the test scene**

In Unity (`11.0 Biomes/TestScene.unity`): add a `ParameterInterpolator` component to a GameObject. Assign `simManager`, set `simIndex` to a Physarum or Boid sim. Click **Refresh Param List** → toggles populate from that sim's modulatable params.

- [ ] **Step 2: Create a second waypoint preset**

Duplicate the sim's assigned `paramsSO` asset (Project window → Ctrl+D) or create a new one via `Assets/Create/Biomes/PhysarumParams` (or `BoidParams`). Tweak several values so it visibly differs. Assign the sim's current `paramsSO` and the new asset into `waypoints` (order = play order).

- [ ] **Step 3: Run and observe**

Enter Play mode, ensure the `SimulationManager` is running (steps advancing). Set `durationSteps` small (e.g. 120) for a quick test. Click **Play**. Watch the sim's `agentParams` (Runtime Parameters) fields sweep toward each waypoint, hold for `holdSteps`, advance, and stop at the last waypoint. Progress field animates 0→1 per leg.

- [ ] **Step 4: Verify toggles + hue**

Stop, deselect `hue` and `moveSpeed` (or `maxSpeed`) in toggles, Play again → those fields stay frozen while others interpolate. With a waypoint whose hue is ~0.1 and live hue ~0.9, confirm hue moves the short way (through 1.0/0.0), not down through 0.5.

- [ ] **Step 5: Update docs**

Per repo convention, add a short session note and update `docs/INDEX.md` if it lists features. Then per project CLAUDE.md, update `README.md` (Concepts or a Tools line) to mention `ParameterInterpolator` for slow preset crossfades. Commit:

```bash
git add README.md docs/
git commit -m "docs: note ParameterInterpolator"
```

---

## Self-Review

**Spec coverage:**
- Sim-step driven → `StepNow()`/`SimStepCount` (Task 5) ✓
- Option A field-level → `IParamSet.GetValue/SetValue` raw (Tasks 1–3) ✓
- From = current live, re-snapshot per leg → `SnapshotFrom()` on Play + Advance (Task 5) ✓
- To = list of `.asset` waypoints, chainable → `List<ScriptableObject> waypoints` + `Advance` (Task 5) ✓
- Per-param-name enable/disable, frozen when off → `paramToggles`/`IsEnabled` skip (Task 5) ✓
- Shortest-arc hue → `LerpHue01` (Task 5, verified Step 2) ✓
- Global duration + hold + easing for all transitions → `durationSteps`/`holdSteps`/`easing` (Task 5) ✓
- Stop & hold at queue end → `Advance` past-end → `Phase.Done` (Task 5) ✓
- Play restarts from waypoint 0 → `Play()` sets `currentWaypoint = 0` (Task 5) ✓
- Inspector progress readout → serialized `phase`/`currentWaypoint`/`progress` (Task 5) ✓
- One per sim → `simManager` + `simIndex` (Task 5) ✓
- Edge: fewer target types → `Mathf.Min` type count (Task 5) ✓
- Edge: wrong-type asset → `as IParamSet` null guard + warn-once (Task 5) ✓

**Placeholder scan:** none — all code blocks complete, no TBD/TODO.

**Type consistency:** `IParamSet` members (`TypeCount`, `GetValue`, `SetValue`, `GetRange`) identical across Tasks 1–3; `LiveParamSet` property name consistent Tasks 4–5; `Phase` enum, `ParamToggle`, `_from`, `Advance`, `SnapshotFrom`, `LerpHue01` all defined once and referenced consistently.
