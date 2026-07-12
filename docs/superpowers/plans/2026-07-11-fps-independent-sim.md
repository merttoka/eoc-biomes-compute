# FPS-Independent Simulation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Drive the sim on a fixed 60 Hz timestep (`FixedUpdate`) with rendering decoupled to `LateUpdate`, so simulation speed is independent of render FPS across all installs.

**Architecture:** Move `Step()` out of the frame-locked `Update()` into `FixedUpdate()` (Unity's built-in fixed-timestep accumulator, `Time.fixedDeltaTime = 1/simRate`). Unweld the composite `Render()` from `Step()` and call it once per frame in `LateUpdate()`. Seed the shader RNG from a monotonic per-sim step counter instead of `Time.frameCount` so the sim is deterministic across hardware. `Time.maximumDeltaTime` is the (exposed) spiral-of-death guard.

**Tech Stack:** Unity 6 (HDRP), C# MonoBehaviours, GPU compute shaders. No automated test harness exists in this project (no Unity Test Framework / NUnit); verification is **compile-clean + play-mode observation**, described per task.

## Global Constraints

- Workspace: `Assets/Workspace/11.0 Biomes` (the active engine; `10.0 Metaesthetica` is a predecessor — **do not touch it**).
- Canonical sim rate: **60 steps/sec** (`Time.fixedDeltaTime = 1/60`). This equals today's `targetFPS 60 / stepsPerFrame 1 / stepMod 1`, so **no content re-tuning**.
- No shader *math* changes. The RNG fix changes only the *source* of the `time` value, not any HLSL.
- Preserve the pause capability: `stepsPerTick` keeps a minimum of `0` (`0` = paused, as `stepsPerFrame 0` was).
- Commit messages: concise, no attribution (per repo convention).
- Every field added to the inspector carries a `[Tooltip]` with the exact copy given below.

---

### Task 1: Deterministic RNG seed in `SimulationBase`

Reseed the shader RNG from a monotonic per-sim step counter instead of `Time.frameCount`. At 1 step/frame this is behavior-identical to today (safe to land first); it becomes correctness-critical once the sim can run multiple `Step()`s per rendered frame (catch-up or `stepsPerTick > 1`).

**Files:**
- Modify: `Assets/Workspace/11.0 Biomes/src/components/core/SimulationBase.cs` (lines 219–225, 238–245, 289–291)

**Interfaces:**
- Consumes: nothing (self-contained).
- Produces: `protected int WrappedStep` (replaces `protected int WrappedFrame`); a `private int _simStep` incremented in `Step()`, zeroed in `Reset()`. No subclass overrides `Step()`; all three subclasses override `Reset()` and call `base.Reset()` (verified), so the counter stays correct everywhere.

- [ ] **Step 1: Replace the `WrappedFrame` declaration + comment (lines 219–225)**

Old:
```csharp
        // Wrapped frame counter fed to shaders as `time`. Keeps (float)time small so RNG
        // seeds (e.g. time*0.001 + id*0.0001, sin(time)) keep per-agent precision over
        // long installation runs — raw Time.frameCount degrades them within hours.
        // Wraps every 65536 frames (~18 min @60fps); the one-frame discontinuity at wrap
        // is imperceptible.
        protected const int TimeWrap = 65536;
        protected int WrappedFrame => Time.frameCount % TimeWrap;
```

New:
```csharp
        // Wrapped sim-step counter fed to shaders as `time`. Keeps (float)time small so
        // RNG seeds (e.g. time*0.001 + id*0.0001, sin(time)) keep per-agent precision over
        // long installation runs — a raw monotonic counter degrades them within hours.
        // Sourced from the sim step, NOT Time.frameCount: consecutive Step()s in one
        // render frame (catch-up on slow HW, or stepsPerTick>1) must get distinct seeds
        // so the sim advances identically regardless of frame pacing. Wraps every 65536
        // steps (~18 min @60Hz); the one-step discontinuity at wrap is imperceptible.
        protected const int TimeWrap = 65536;
        private int _simStep;
        protected int WrappedStep => _simStep % TimeWrap;
```

- [ ] **Step 2: Zero the counter in `Reset()` (line 238–241)**

Old:
```csharp
        public virtual void Reset()
        {
            if (NeedsAllocation())
                Allocate();
```

New:
```csharp
        public virtual void Reset()
        {
            _simStep = 0;
            if (NeedsAllocation())
                Allocate();
```

- [ ] **Step 3: Increment + use it in `Step()` (line 289–291)**

Old:
```csharp
        public virtual void Step()
        {
            cs.SetInt(s_TimeID, WrappedFrame);
```

New:
```csharp
        public virtual void Step()
        {
            _simStep++;
            cs.SetInt(s_TimeID, WrappedStep);
```

- [ ] **Step 4: Verify it compiles**

Focus the Unity Editor so it recompiles. Open the Console (`Window ▸ General ▸ Console`).
Expected: **no compile errors**. A stale reference to `WrappedFrame` anywhere would surface here — grep to be sure:
```bash
cd "/Users/toka/Developer/Graphics/EoC-biomes-compute" && grep -rn "WrappedFrame" --include='*.cs' Assets
```
Expected: **no matches** (all replaced by `WrappedStep`).

- [ ] **Step 5: Verify behavior unchanged (play-mode)**

Open `Assets/Workspace/11.0 Biomes/TestScene.unity`, press Play. With the current driver still running 1 step/frame, the sim looks identical to before this change (this task is a no-op at 1 step/frame).
Expected: agents move/deposit as before; no visual regression.

- [ ] **Step 6: Commit**

```bash
cd "/Users/toka/Developer/Graphics/EoC-biomes-compute"
git add "Assets/Workspace/11.0 Biomes/src/components/core/SimulationBase.cs"
git commit -m "sim: seed agent RNG from monotonic sim step, not frame count"
```

---

### Task 2: Fixed-timestep driver + decoupled render (`SimulationManager`)

Replace the frame-locked `Update()` with a `FixedUpdate()` driver at `simRate`, move the composite `Render()` to `LateUpdate()`, add the `simRate` / `maxAllowedTimestep` fields, rename `stepsPerFrame → stepsPerTick`, and retire `stepMod`.

**Files:**
- Modify: `Assets/Workspace/11.0 Biomes/src/components/core/SimulationManager.cs` (add `using`; lines 17–18; `Awake()` 114–121; `Update()` 199–204; `Step()` trailing `Render()` at 309)

**Interfaces:**
- Consumes: `Step()` and `Render()` (existing methods on this class, unchanged in signature). `SimulationBase.Step()` (Task 1) — one increment per call.
- Produces: serialized fields `float simRate` (default 60), `float maxAllowedTimestep` (default 0.1), `int stepsPerTick` (default 1, `[FormerlySerializedAs("stepsPerFrame")]`). Removes `stepMod`. New lifecycle methods `FixedUpdate()`, `LateUpdate()`.

- [ ] **Step 1: Add the serialization `using` (top of file, after line 3 `using UnityEngine;`)**

Old:
```csharp
using UnityEngine;
using EasyButtons;
```

New:
```csharp
using UnityEngine;
using UnityEngine.Serialization;
using EasyButtons;
```

- [ ] **Step 2: Replace the timing fields (lines 17–18)**

Old:
```csharp
        [Range(0, 10)] public int stepsPerFrame = 1;
        [Range(1, 50)] public int stepMod = 1;
```

New:
```csharp
        [Tooltip("Fixed simulation rate in steps/sec. The sim advances at this wall-clock rate on every install regardless of render FPS (Time.fixedDeltaTime = 1/simRate). 60 matches the legacy per-frame feel, so no content re-tuning is needed. Each scene's SimulationManager can set its own.")]
        [Range(15f, 120f)] public float simRate = 60f;
        [Tooltip("Spiral-of-death guard (Time.maximumDeltaTime). Caps how much real time one frame may hand to the fixed sim loop. At 60 Hz, 0.1s = at most ~6 catch-up sim steps per rendered frame. If a frame takes longer, the extra time is dropped: the sim slows down uniformly instead of bursting into steps that make the next frame slower still. Lower = steadier under load but lags real-time sooner; higher = tracks real-time harder but risks stutter on a hitching machine. Weak installs run timed loops long, never fast.")]
        [Range(0.02f, 0.5f)] public float maxAllowedTimestep = 0.1f;
        [Tooltip("Sim steps per fixed tick. 2 = double-speed sim, still hardware-independent — but it multiplies per-tick GPU cost with no change to tick scheduling, so a high value can push a marginal install into the maxAllowedTimestep clamp (where it also fails to hold sim rate). A dev/artist tool, not a shipping default; content tuned with it won't survive a port to weaker hardware. 0 = paused.")]
        [FormerlySerializedAs("stepsPerFrame")]
        [Range(0, 10)] public int stepsPerTick = 1;
```

- [ ] **Step 3: Set the fixed timestep in `Awake()` (lines 114–121)**

Old:
```csharp
        void Awake()
        {
            if (limitFPS)
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = targetFPS;
            }
        }
```

New:
```csharp
        void Awake()
        {
            // Fixed-timestep sim: Step() runs in FixedUpdate at simRate steps/sec,
            // independent of render FPS. maxAllowedTimestep is Unity's spiral-of-death
            // guard (see field tooltips). Both are global Time settings, but nothing else
            // in this project uses FixedUpdate/physics, so they're ours to own.
            Time.fixedDeltaTime = 1f / Mathf.Max(1f, simRate);
            Time.maximumDeltaTime = maxAllowedTimestep;

            if (limitFPS)
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = targetFPS;
            }
        }
```

- [ ] **Step 4: Replace `Update()` with `FixedUpdate()` + `LateUpdate()` (lines 199–204)**

Old:
```csharp
        void Update()
        {
            if (Time.frameCount % stepMod == 0)
                for (int i = 0; i < stepsPerFrame; i++)
                    Step();
        }
```

New:
```csharp
        // Sim advances on the fixed clock (simRate). Unity's accumulator calls FixedUpdate
        // 0..N times per rendered frame to track wall-clock, bounded by maxAllowedTimestep.
        void FixedUpdate()
        {
            for (int i = 0; i < stepsPerTick; i++)
                Step();
        }

        // Render is decoupled from the sim: exactly one composite per rendered frame,
        // showing the latest stepped state (FixedUpdate always runs before LateUpdate
        // within a frame). On fast HW render free-runs above simRate; on slow HW it
        // composites the most recent step.
        void LateUpdate() => Render();
```

- [ ] **Step 5: Unweld `Render()` from `Step()` (line ~309, end of `Step()`)**

Note: this is the *manager's composite* `Render()`. Each sim's own `SimulationBase.Step()` still renders its own output texture — that is untouched and correct.

Old:
```csharp
            if (biome != null)
                biome.Step();

            Render();
        }

        void Render()
```

New:
```csharp
            if (biome != null)
                biome.Step();
        }

        void Render()
```

- [ ] **Step 6: Verify it compiles**

Focus Unity, check Console. Expected: **no compile errors**. Confirm the retired field is gone:
```bash
cd "/Users/toka/Developer/Graphics/EoC-biomes-compute" && grep -rn "stepMod\|stepsPerFrame" --include='*.cs' Assets/Workspace/11.0*
```
Expected: **no matches** (only `stepsPerTick` remains; `stepsPerFrame` survives solely as the `[FormerlySerializedAs]` string literal in the attribute — that one line is expected).

- [ ] **Step 7: Verify FPS-independence (play-mode)**

Open `TestScene.unity`. Add a quick on-screen readout or watch the `SimulationManager` in the inspector via a debugger; the simplest check uses `SimStepCount`:
1. Play with `limitFPS = true, targetFPS = 60`. Note `SimStepCount` grows ~60/sec.
2. Stop. Set `targetFPS = 30`. Play again. `SimStepCount` **still grows ~60/sec** (now ~2 FixedUpdates per rendered frame). The visible sim runs at the **same wall-clock speed** — this is the core success criterion.
3. Stop. Set `limitFPS = false` (uncapped render). `SimStepCount` **still ~60/sec**; the sim does not speed up despite far higher FPS.

Expected: sim wall-clock speed identical across all three; only render smoothness differs.

- [ ] **Step 8: Verify graceful degradation (play-mode)**

In a heavy scene (or lower `maxAllowedTimestep` to `0.03` to force the clamp), confirm the sim slows *smoothly* under load and never bursts/stutters, and never runs faster than 60 Hz on a fast machine.
Expected: no visible hitching; sim lags real-time under load rather than catching up violently.

- [ ] **Step 9: Commit**

```bash
cd "/Users/toka/Developer/Graphics/EoC-biomes-compute"
git add "Assets/Workspace/11.0 Biomes/src/components/core/SimulationManager.cs"
git commit -m "sim: fixed 60Hz timestep via FixedUpdate; decouple composite render to LateUpdate"
```

---

### Task 3: Scene migration + architecture doc

Open each show scene so Unity migrates `stepsPerFrame → stepsPerTick` (via `[FormerlySerializedAs]`) and drops the orphaned `stepMod` serialized value, then re-save. Update the architecture doc so §3.1/§3.2 describe the new driver.

**Files:**
- Modify (re-save): `Assets/Workspace/11.0 Biomes/TestScene.unity`, `Assets/Workspace/11.1 CURRENTS Scene/Scene_CURRENTS.unity`, `Assets/Workspace/11.2 SIGGRAPH Scene/Scene_SIGGRAPH.unity`
- Modify: `docs/ARCHITECTURE.md` (§3.1 orchestration, §3.2 per-step pipeline)

**Interfaces:**
- Consumes: the `SimulationManager` fields from Task 2 (`simRate`, `maxAllowedTimestep`, `stepsPerTick`).
- Produces: migrated scenes + updated docs. No code.

- [ ] **Step 1: Migrate each scene**

For each of the three scenes: open it in Unity, select the object holding `SimulationManager`, confirm the inspector now shows **`Sim Rate = 60`**, **`Max Allowed Timestep = 0.1`**, and **`Steps Per Tick`** carrying the value its old `stepsPerFrame` had (auto-migrated). Then **File ▸ Save** to rewrite the YAML (removes the dead `stepMod` key).

Note: `Scene_SIGGRAPH.unity` already shows as modified in `git status` from prior work — save/commit its migration together here.

- [ ] **Step 2: Verify migration in YAML**

```bash
cd "/Users/toka/Developer/Graphics/EoC-biomes-compute" && grep -rn "stepMod\|stepsPerFrame\|simRate\|stepsPerTick" Assets/Workspace/11.*/*.unity
```
Expected: `simRate` and `stepsPerTick` present; **no** `stepMod` or `stepsPerFrame` keys remain in any scene.

- [ ] **Step 3: Update `docs/ARCHITECTURE.md` §3.1**

Find the sentence (around line 96–98):
> `Update()` calls `Step()` `stepsPerFrame` times every `stepMod` frames. `SimStepCount` is the canonical sim clock...

Replace with:
> `FixedUpdate()` calls `Step()` `stepsPerTick` times on a fixed clock — `Time.fixedDeltaTime = 1/simRate` (default 60 Hz) — so sim speed is independent of render FPS; `LateUpdate()` runs the composite `Render()` once per rendered frame. Unity's `Time.maximumDeltaTime` (exposed as `maxAllowedTimestep`) caps catch-up on slow hardware. `SimStepCount` is the canonical sim clock (monotonic, increments per `Step()`), used by time-based tooling.

- [ ] **Step 4: Update `docs/ARCHITECTURE.md` §3.2**

At the top of the per-step pipeline section (around line 111–113), add a sentence noting render is no longer part of `Step()`:
> `SimulationManager.Step()` runs a fixed sequence each simulation step (the composite render is **not** part of it — it runs separately in `LateUpdate`):

- [ ] **Step 5: Commit**

```bash
cd "/Users/toka/Developer/Graphics/EoC-biomes-compute"
git add "Assets/Workspace/11.0 Biomes/TestScene.unity" \
        "Assets/Workspace/11.1 CURRENTS Scene/Scene_CURRENTS.unity" \
        "Assets/Workspace/11.2 SIGGRAPH Scene/Scene_SIGGRAPH.unity" \
        docs/ARCHITECTURE.md
git commit -m "scenes+docs: migrate to simRate/stepsPerTick fixed-timestep driver"
```

---

## Post-implementation

- **Integration check (from spec §5):** confirm the injector's async agent-position readback doesn't assume one `Step()` per frame once step and frame cadence diverge. Exercise a firing/dispersal event at `targetFPS 30` and at uncapped FPS; the dispersal should look identical in wall-clock terms. If it desyncs, that's a follow-up bug, not part of this plan.
- **README/session log:** update `README.md` recent-changes and add a `docs/sessions/` entry via the `eoc-docs` skill before merging to main (repo convention).

## Self-Review

- **Spec coverage:** two-loop split (Task 2 §3.1/3.3) ✓; `simRate` 60 Hz + per-scene (Task 2 Step 2/3) ✓; `maxAllowedTimestep` exposed w/ tooltip (Task 2 Step 2) ✓; `stepsPerTick` rename + `[FormerlySerializedAs]` + footgun tooltip (Task 2 Step 2) ✓; `stepMod` retired (Task 2 Step 2/6) ✓; unweld render (Task 2 Step 5) ✓; RNG-seed determinism fix (Task 1) ✓; scene migration (Task 3) ✓; ARCHITECTURE.md update (Task 3) ✓; injector integration check (Post-implementation) ✓. No gaps.
- **Placeholder scan:** all code steps show full old/new blocks; all verification steps give exact commands + expected output. No TBD/TODO.
- **Type consistency:** `WrappedStep`/`_simStep` (Task 1) used consistently; `stepsPerTick`/`simRate`/`maxAllowedTimestep` names identical across Tasks 2 and 3; `Step()`/`Render()` signatures unchanged.
