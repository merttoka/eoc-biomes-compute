# Event-Driven CA Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the CA sims from continuously-running layers into event-driven bursts that are seeded from a biome channel, sized independently of output resolution, and leave an eroding trace behind.

**Architecture:** All new *pure* logic (grid sizing, burst clock) goes into the `Biomes.Core` assembly at `src/core_math/`, following the `NeuronLayout` precedent — that is the only assembly the EditMode test project can reference. `FieldSimulationBase` consumes those pure types and keeps all Unity/GPU concerns. The two CA compute shaders gain a shared seeding helper and an output-envelope multiply via `cellular_common.hlsl`.

**Tech Stack:** Unity 6000.3.10f1, HDRP, C#, HLSL compute shaders, NUnit (Unity Test Framework), EasyButtons.

## Global Constraints

- Unity **6000.3.10f1** — the project version; do not upgrade.
- New pure logic MUST live in `Assets/Workspace/11.0 Biomes/src/core_math/` (assembly `Biomes.Core`). Everything else in the project is `Assembly-CSharp`, which `Assets/Tests/EditMode/Biomes.Sequencer.Tests.asmdef` cannot reference (`overrideReferences: true`, `autoReferenced: false`).
- All burst durations are counted in **sim steps**, never rule steps. `stepEvery` decimates only the rule.
- The output envelope must NOT be written into `compositeWeight` (dirties the scene) or `caParams.brightness` (corrupts a MIDI/OSC-bindable live param clone).
- `BiomeChannel.Count` stays **15**. `Excitability` = 13, `Substrate` = 14. Both channels are kept (spec decision 7).
- Namespace for all new C# is `Biomes`.
- Commit after every task. Branch is `ca-dev`.
- The user's Unity Editor is usually open and holds the project lock, so **batchmode cannot run**. Tests are run from the Editor's Test Runner window unless the Editor is closed.

---

## File Structure

**Create:**
- `Assets/Workspace/11.0 Biomes/src/core_math/CellGrid.cs` — pure cell-grid sizing. One responsibility: turn an absolute cell height + master aspect into a grid.
- `Assets/Workspace/11.0 Biomes/src/core_math/BurstEnvelope.cs` — pure burst clock (`BurstPhase`, `BurstEnvelope`, `RisingEdge`). One responsibility: what phase is the burst in and how loud is it.
- `Assets/Tests/EditMode/CellGridTests.cs`
- `Assets/Tests/EditMode/BurstEnvelopeTests.cs`

**Modify:**
- `Assets/Workspace/11.0 Biomes/src/components/core/FieldSimulationBase.cs` — resolution field, burst state, `Step()` gating, `TriggerBurst()`, seeding + envelope binds.
- `Assets/Workspace/11.0 Biomes/src/components/core/SimulationBase.cs` — add `neuronIntensity`.
- `Assets/Workspace/11.0 Biomes/src/components/core/SimulationManager.cs:281-286` — broadcast the intensity.
- `Assets/Workspace/11.0 Biomes/src/components/core/BiomeFieldConfig.cs:86-87` — channel PDE rates.
- `Assets/Workspace/11.0 Biomes/src/computes/includes/cellular_common.hlsl` — envelope + seed uniforms and helper.
- `Assets/Workspace/11.0 Biomes/src/computes/LookupCA.compute` — seed + envelope.
- `Assets/Workspace/11.0 Biomes/src/computes/CyclicCA.compute` — seed + envelope.
- The three `BiomeFieldConfig_Homeostatic.asset` files (11.1, 11.2, 11.3).
- `Assets/Workspace/11.3 SIGGRAPH DAC Scene/Scene_DAC.unity` — migrate resolution fields.

---

### Task 1: `CellGrid` — absolute cell-grid sizing

**Files:**
- Create: `Assets/Workspace/11.0 Biomes/src/core_math/CellGrid.cs`
- Test: `Assets/Tests/EditMode/CellGridTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `Biomes.CellGrid.MinRez` (`const int`), `CellGrid.Height(int cellRezHeight) -> int`, `CellGrid.Width(int cellRezHeight, int masterRezX, int masterRezY) -> int`, `CellGrid.CellCount(int cellRezHeight, int masterRezX, int masterRezY) -> int`. Task 2 calls `Height` and `Width`.

- [ ] **Step 1: Write the failing test**

Create `Assets/Tests/EditMode/CellGridTests.cs`:

```csharp
using NUnit.Framework;
using Biomes;

/// <summary>
/// Guards the property the absolute-cell-resolution change exists to buy: the CA's grid —
/// and therefore its on-screen cell size — must not move when the master resolution moves.
/// The old `cellResolutionScale` was a fraction of master, so 1080p and 4K produced different
/// grids from identical settings. Resolution_DoesNotChangeGrid is the test that would have failed.
/// </summary>
public class CellGridTests
{
    [Test]
    public void Height_ClampsToMinimum()
    {
        Assert.That(CellGrid.Height(2), Is.EqualTo(CellGrid.MinRez));
    }

    [Test]
    public void Height_PassesThroughAboveMinimum()
    {
        Assert.That(CellGrid.Height(540), Is.EqualTo(540));
    }

    [Test]
    public void Width_PreservesSixteenNineAspect()
    {
        // 540 * 3840/2160 = 960
        Assert.That(CellGrid.Width(540, 3840, 2160), Is.EqualTo(960));
    }

    [Test]
    public void Width_PreservesUltraWideAspect()
    {
        // 540 * 9472/900 = 5683.2 -> 5683. Deliberately asserted: an absolute height on an
        // 11.84:1 canvas is EXPENSIVE (3.07 M cells). Authors must lower cellRezHeight there.
        Assert.That(CellGrid.Width(540, 9472, 900), Is.EqualTo(5683));
    }

    [Test]
    public void Resolution_DoesNotChangeGrid()
    {
        Assert.That(CellGrid.Width(540, 1920, 1080), Is.EqualTo(CellGrid.Width(540, 3840, 2160)));
        Assert.That(CellGrid.CellCount(540, 1920, 1080), Is.EqualTo(CellGrid.CellCount(540, 3840, 2160)));
    }

    [Test]
    public void Width_HandlesDegenerateMaster()
    {
        Assert.That(CellGrid.Width(540, 3840, 0), Is.EqualTo(540));
        Assert.That(CellGrid.Width(540, 0, 2160), Is.EqualTo(540));
    }

    [Test]
    public void Width_ClampsToMinimum()
    {
        Assert.That(CellGrid.Width(8, 1, 4096), Is.EqualTo(CellGrid.MinRez));
    }

    [Test]
    public void CellCount_IsWidthTimesHeight()
    {
        Assert.That(CellGrid.CellCount(540, 3840, 2160), Is.EqualTo(960 * 540));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

In the Editor: `Window > General > Test Runner > EditMode > Run All`.

Expected: FAIL — compile error, `The name 'CellGrid' does not exist in the current context`.

If the Editor is closed you may instead run:
`/Applications/Unity/Hub/Editor/6000.3.10f1/Unity.app/Contents/MacOS/Unity -batchmode -projectPath "$PWD" -runTests -testPlatform EditMode -testResults /tmp/ca-tests.xml`

- [ ] **Step 3: Write the minimal implementation**

Create `Assets/Workspace/11.0 Biomes/src/core_math/CellGrid.cs`:

```csharp
using UnityEngine;

namespace Biomes
{
    /// <summary>
    /// Cell-grid sizing for field sims. Pure math with no scene dependencies so it can be
    /// unit-tested — the same reason <see cref="NeuronLayout"/> lives in this assembly.
    ///
    /// <para><b>Height is absolute, width is derived.</b> The grid height is authored in cells
    /// and the width comes from the master's aspect. This is the whole point of the type:
    /// <c>cellResolutionScale</c> was a fraction of master, so changing output resolution
    /// silently changed the automaton's on-screen cell size. The rule never changed; the
    /// picture did.</para>
    ///
    /// <para>Note the cost on wide canvases: at 11.84:1 an absolute height of 540 gives a
    /// 5683-wide grid (3.07 M cells). Aspect preservation means width scales with the canvas,
    /// so ultra-wide scenes want a smaller height.</para>
    /// </summary>
    public static class CellGrid
    {
        /// <summary>Smallest grid any field sim will allocate, per axis.</summary>
        public const int MinRez = 8;

        public static int Height(int cellRezHeight) => Mathf.Max(MinRez, cellRezHeight);

        /// <summary>
        /// Width preserving the master's aspect at the given cell height. A degenerate master
        /// (either axis at or below zero) falls back to a square grid rather than dividing by zero.
        /// </summary>
        public static int Width(int cellRezHeight, int masterRezX, int masterRezY)
        {
            int h = Height(cellRezHeight);
            if (masterRezX <= 0 || masterRezY <= 0) return h;
            return Mathf.Max(MinRez, Mathf.RoundToInt(h * (float)masterRezX / masterRezY));
        }

        /// <summary>Total cells — the number that actually costs GPU time.</summary>
        public static int CellCount(int cellRezHeight, int masterRezX, int masterRezY)
            => Width(cellRezHeight, masterRezX, masterRezY) * Height(cellRezHeight);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Test Runner > EditMode > Run All. Expected: PASS, 8 new tests green, and the pre-existing 31 still green (39 total).

- [ ] **Step 5: Commit**

```bash
git add "Assets/Workspace/11.0 Biomes/src/core_math/CellGrid.cs" \
        "Assets/Workspace/11.0 Biomes/src/core_math/CellGrid.cs.meta" \
        Assets/Tests/EditMode/CellGridTests.cs Assets/Tests/EditMode/CellGridTests.cs.meta
git commit -m "feat(core): CellGrid — absolute cell-grid sizing, aspect from master"
```

---

### Task 2: Adopt absolute cell resolution in `FieldSimulationBase`

**Files:**
- Modify: `Assets/Workspace/11.0 Biomes/src/components/core/FieldSimulationBase.cs` (the `cellResolutionScale` field near line 42, and `CellRezX`/`CellRezY` near line 145)
- Modify: `Assets/Workspace/11.3 SIGGRAPH DAC Scene/Scene_DAC.unity` (in the Editor, not by hand)

**Interfaces:**
- Consumes: `CellGrid.Height`, `CellGrid.Width` from Task 1.
- Produces: `public int cellRezHeight` on `FieldSimulationBase`. `cellResolutionScale` no longer exists — Task 4 and Task 5 must not reference it.

- [ ] **Step 1: Replace the serialized field**

In `FieldSimulationBase.cs`, replace this block:

```csharp
        [Tooltip("State-grid resolution as a fraction of this sim's resolution. CAs are coarse " +
                 "by nature and a Moore neighbourhood of radius r costs O((2r+1)^2) samples per " +
                 "cell per step, so running the rule at half res is a ~4x saving that is nearly " +
                 "invisible — the composite UV-samples every layer, so a smaller outTex upscales " +
                 "for free. Takes effect on Reset.")]
        [Range(0.05f, 1f)] public float cellResolutionScale = 0.5f;
```

with:

```csharp
        [Tooltip("State-grid HEIGHT in cells, absolute. Width follows the master's aspect. " +
                 "Absolute rather than a fraction of master so the automaton's on-screen cell " +
                 "size does not move when output resolution does — 1080p and 4K give the same " +
                 "picture, upscaled differently by the composite. On very wide canvases width " +
                 "scales with the aspect, so lower this there. Takes effect on Reset.")]
        [Range(64, 2048)] public int cellRezHeight = 540;
```

- [ ] **Step 2: Replace the derivation**

Replace:

```csharp
        protected int CellRezX => Mathf.Max(8,
            Mathf.RoundToInt(rezX * Mathf.Clamp(cellResolutionScale, 0.05f, 1f)));
        protected int CellRezY => Mathf.Max(8,
            Mathf.RoundToInt(rezY * Mathf.Clamp(cellResolutionScale, 0.05f, 1f)));
```

with:

```csharp
        protected int CellRezX => CellGrid.Width(cellRezHeight, rezX, rezY);
        protected int CellRezY => CellGrid.Height(cellRezHeight);
```

- [ ] **Step 3: Verify it compiles and nothing else references the old field**

```bash
grep -rn "cellResolutionScale" "Assets/Workspace/11.0 Biomes/src" docs/ || echo "NO REFERENCES — good"
```

Expected: no hits under `src/`. (Hits in `docs/` are historical prose and are fine.)

In the Editor, confirm the Console shows no compile errors.

- [ ] **Step 4: Migrate the scene components**

`cellResolutionScale` will not deserialize into `cellRezHeight`, so every CA component silently
falls back to 540. Open `Assets/Workspace/11.3 SIGGRAPH DAC Scene/Scene_DAC.unity` and set each
one, so the look is preserved. Master `rezY` is 2160, so `cellRezHeight = round(oldScale * 2160)`:

| Component | old `cellResolutionScale` | set `cellRezHeight` |
|---|---|---|
| `2DCA` (LookupCASim, under the active `SimulationManager`) | 0.25 | **540** |
| `CyclicCA` (CyclicCASim) | 0.35 | **756** |
| any CA under the disabled `SimulationManager (1)` backup | 0.25 / 0.35 | **540** / **756** |

Save the scene.

- [ ] **Step 5: Verify the grid is unchanged**

Enter Play mode. In the Console, confirm no errors. The CA layer should look **identical** to
before this task — same cell size, same pattern scale. If it looks coarser or finer, a
`cellRezHeight` was missed.

- [ ] **Step 6: Run the tests**

Test Runner > EditMode > Run All. Expected: 39 PASS.

- [ ] **Step 7: Commit**

```bash
git add "Assets/Workspace/11.0 Biomes/src/components/core/FieldSimulationBase.cs" \
        "Assets/Workspace/11.3 SIGGRAPH DAC Scene/Scene_DAC.unity"
git commit -m "feat(sim): field sims size their grid by absolute cell height, not a fraction of master"
```

---

### Task 3: `BurstEnvelope` — the burst clock

**Files:**
- Create: `Assets/Workspace/11.0 Biomes/src/core_math/BurstEnvelope.cs`
- Test: `Assets/Tests/EditMode/BurstEnvelopeTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `Biomes.BurstPhase` (enum: `Idle=0, Attack=1, Sustain=2, Release=3`), `Biomes.BurstEnvelope` (struct with fields `Phase`, `Age`, `Value`; members `IsIdle`, `bool Trigger()`, `void Advance(int fadeInSteps, int sustainSteps, int fadeOutSteps)`), `Biomes.RisingEdge` (struct with `bool Update(float value, float threshold)`). Task 4 uses all three.

- [ ] **Step 1: Write the failing test**

Create `Assets/Tests/EditMode/BurstEnvelopeTests.cs`:

```csharp
using NUnit.Framework;
using Biomes;

/// <summary>
/// The burst clock, in isolation from Unity. Durations are SIM steps: a burst's wall-clock
/// length must not change when stepEvery changes, because stepEvery decimates only the rule.
///
/// The retrigger contract is the subtle part. A trigger arriving during a live burst extends
/// it — resets the clock, keeps the lattice — so sustained firing sustains ONE evolving
/// automaton instead of restarting it. Only a trigger from Idle re-seeds.
/// </summary>
public class BurstEnvelopeTests
{
    const float Eps = 1e-4f;

    // fadeIn 4, sustain 3, fadeOut 2 — small enough to step by hand in the assertions below.
    static void Advance(ref BurstEnvelope e, int times)
    {
        for (int i = 0; i < times; i++) e.Advance(4, 3, 2);
    }

    [Test]
    public void NewEnvelope_IsIdleAndSilent()
    {
        var e = new BurstEnvelope();
        Assert.That(e.IsIdle, Is.True);
        Assert.That(e.Value, Is.EqualTo(0f).Within(Eps));
    }

    [Test]
    public void AdvanceWhileIdle_IsANoOp()
    {
        var e = new BurstEnvelope();
        Advance(ref e, 10);
        Assert.That(e.IsIdle, Is.True);
        Assert.That(e.Value, Is.EqualTo(0f).Within(Eps));
    }

    [Test]
    public void TriggerFromIdle_RequestsReseed()
    {
        var e = new BurstEnvelope();
        Assert.That(e.Trigger(), Is.True);
        Assert.That(e.Phase, Is.EqualTo(BurstPhase.Attack));
    }

    [Test]
    public void TriggerDuringBurst_DoesNotRequestReseed()
    {
        var e = new BurstEnvelope();
        e.Trigger();
        Advance(ref e, 2);
        Assert.That(e.Trigger(), Is.False, "a live burst must keep its lattice");
    }

    [Test]
    public void Attack_RampsToFullOverFadeInSteps()
    {
        var e = new BurstEnvelope();
        e.Trigger();
        Advance(ref e, 1); Assert.That(e.Value, Is.EqualTo(0.25f).Within(Eps));
        Advance(ref e, 1); Assert.That(e.Value, Is.EqualTo(0.50f).Within(Eps));
        Advance(ref e, 1); Assert.That(e.Value, Is.EqualTo(0.75f).Within(Eps));
        Advance(ref e, 1);
        Assert.That(e.Value, Is.EqualTo(1f).Within(Eps));
        Assert.That(e.Phase, Is.EqualTo(BurstPhase.Sustain));
    }

    [Test]
    public void Sustain_HoldsThenReleases()
    {
        var e = new BurstEnvelope();
        e.Trigger();
        Advance(ref e, 6);   // 4 attack + 2 sustain
        Assert.That(e.Phase, Is.EqualTo(BurstPhase.Sustain));
        Assert.That(e.Value, Is.EqualTo(1f).Within(Eps));
        Advance(ref e, 1);   // age 7 == fadeIn(4) + sustain(3)
        Assert.That(e.Phase, Is.EqualTo(BurstPhase.Release));
    }

    [Test]
    public void Release_FadesToIdle()
    {
        var e = new BurstEnvelope();
        e.Trigger();
        Advance(ref e, 8);   // one release step
        Assert.That(e.Value, Is.EqualTo(0.5f).Within(Eps));
        Advance(ref e, 1);
        Assert.That(e.IsIdle, Is.True);
        Assert.That(e.Value, Is.EqualTo(0f).Within(Eps));
        Assert.That(e.Age, Is.EqualTo(0));
    }

    [Test]
    public void RetriggerDuringSustain_DoesNotDipTheOutput()
    {
        var e = new BurstEnvelope();
        e.Trigger();
        Advance(ref e, 5);                       // in sustain, Value == 1
        Assert.That(e.Value, Is.EqualTo(1f).Within(Eps));
        e.Trigger();
        Advance(ref e, 1);
        Assert.That(e.Value, Is.EqualTo(1f).Within(Eps), "re-attack must not restart from zero");
    }

    [Test]
    public void RetriggerDuringRelease_ClimbsBack()
    {
        var e = new BurstEnvelope();
        e.Trigger();
        Advance(ref e, 8);                       // releasing, Value == 0.5
        e.Trigger();
        Advance(ref e, 1);
        Assert.That(e.Value, Is.GreaterThan(0.5f));
        Assert.That(e.Phase, Is.EqualTo(BurstPhase.Attack));
    }

    [Test]
    public void ZeroFadeIn_ReachesFullImmediately()
    {
        var e = new BurstEnvelope();
        e.Trigger();
        e.Advance(0, 3, 2);
        Assert.That(e.Value, Is.EqualTo(1f).Within(Eps));
        Assert.That(e.Phase, Is.EqualTo(BurstPhase.Sustain));
    }

    [Test]
    public void ZeroFadeOut_GoesIdleImmediately()
    {
        var e = new BurstEnvelope();
        e.Trigger();
        for (int i = 0; i < 4; i++) e.Advance(0, 0, 0);
        Assert.That(e.IsIdle, Is.True);
    }

    [Test]
    public void RisingEdge_FiresOnceOnCrossing()
    {
        var edge = new RisingEdge();
        Assert.That(edge.Update(0.1f, 0.35f), Is.False);
        Assert.That(edge.Update(0.5f, 0.35f), Is.True,  "crossing up fires");
        Assert.That(edge.Update(0.9f, 0.35f), Is.False, "staying above must not refire");
        Assert.That(edge.Update(0.0f, 0.35f), Is.False);
        Assert.That(edge.Update(0.4f, 0.35f), Is.True,  "crossing again fires again");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Test Runner > EditMode > Run All. Expected: FAIL — `The name 'BurstEnvelope' does not exist in the current context`.

- [ ] **Step 3: Write the implementation**

Create `Assets/Workspace/11.0 Biomes/src/core_math/BurstEnvelope.cs`:

```csharp
using UnityEngine;

namespace Biomes
{
    public enum BurstPhase { Idle = 0, Attack = 1, Sustain = 2, Release = 3 }

    /// <summary>
    /// The clock for one event-driven CA burst. Pure and allocation-free so it can be
    /// unit-tested away from Unity — the same reason <see cref="NeuronLayout"/> and
    /// <see cref="CellGrid"/> live in this assembly.
    ///
    /// <para><b>Durations are SIM steps, never rule steps.</b> <c>stepEvery</c> decimates only
    /// the rule, so counting a burst in rule steps would make its wall-clock length change
    /// whenever the author retuned the CA's pace.</para>
    ///
    /// <para><b>Retrigger extends, it does not restart.</b> <see cref="Trigger"/> reports
    /// whether the caller must re-seed, and that is true only from <see cref="BurstPhase.Idle"/>.
    /// Sustained firing therefore sustains one evolving lattice rather than repeatedly wiping
    /// it. The envelope is rate-based rather than looked up from age precisely so a retrigger
    /// mid-sustain continues from the current value instead of dipping to zero.</para>
    /// </summary>
    public struct BurstEnvelope
    {
        public BurstPhase Phase;
        /// <summary>Sim steps since the most recent trigger.</summary>
        public int Age;
        /// <summary>0..1 output multiplier.</summary>
        public float Value;

        public bool IsIdle => Phase == BurstPhase.Idle;

        /// <summary>Start or extend a burst. Returns true when the grid must be re-seeded.</summary>
        public bool Trigger()
        {
            bool reseed = Phase == BurstPhase.Idle;
            Phase = BurstPhase.Attack;
            Age = 0;
            return reseed;
        }

        /// <summary>Advance exactly one sim step.</summary>
        public void Advance(int fadeInSteps, int sustainSteps, int fadeOutSteps)
        {
            int fadeIn  = Mathf.Max(0, fadeInSteps);
            int sustain = Mathf.Max(0, sustainSteps);
            int fadeOut = Mathf.Max(0, fadeOutSteps);

            switch (Phase)
            {
                case BurstPhase.Idle:
                    Value = 0f;
                    Age = 0;
                    break;

                case BurstPhase.Attack:
                    Age++;
                    Value = fadeIn <= 0 ? 1f : Mathf.Min(1f, Value + 1f / fadeIn);
                    if (Age >= fadeIn) { Value = 1f; Phase = BurstPhase.Sustain; }
                    break;

                case BurstPhase.Sustain:
                    Age++;
                    Value = 1f;
                    if (Age >= fadeIn + sustain) Phase = BurstPhase.Release;
                    break;

                case BurstPhase.Release:
                    Age++;
                    Value = fadeOut <= 0 ? 0f : Mathf.Max(0f, Value - 1f / fadeOut);
                    if (Value <= 0f) { Value = 0f; Age = 0; Phase = BurstPhase.Idle; }
                    break;
            }
        }
    }

    /// <summary>
    /// Fires once when a signal crosses a threshold upward, and not again until it has fallen
    /// back below. Without this, any sampling of a firing level above threshold would retrigger
    /// on every single step and the burst would never end.
    /// </summary>
    public struct RisingEdge
    {
        private bool _wasAbove;

        public bool Update(float value, float threshold)
        {
            bool above = value >= threshold;
            bool fired = above && !_wasAbove;
            _wasAbove = above;
            return fired;
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Test Runner > EditMode > Run All. Expected: PASS, 12 new tests green (51 total).

- [ ] **Step 5: Commit**

```bash
git add "Assets/Workspace/11.0 Biomes/src/core_math/BurstEnvelope.cs" \
        "Assets/Workspace/11.0 Biomes/src/core_math/BurstEnvelope.cs.meta" \
        Assets/Tests/EditMode/BurstEnvelopeTests.cs Assets/Tests/EditMode/BurstEnvelopeTests.cs.meta
git commit -m "feat(core): BurstEnvelope + RisingEdge — the burst clock, in sim steps"
```

---

### Task 4: Wire the burst into `FieldSimulationBase`

**Files:**
- Modify: `Assets/Workspace/11.0 Biomes/src/components/core/SimulationBase.cs` (near line 58)
- Modify: `Assets/Workspace/11.0 Biomes/src/components/core/SimulationManager.cs:277-286`
- Modify: `Assets/Workspace/11.0 Biomes/src/components/core/FieldSimulationBase.cs`
- Modify: `Assets/Workspace/11.0 Biomes/src/computes/includes/cellular_common.hlsl`
- Modify: `Assets/Workspace/11.0 Biomes/src/computes/LookupCA.compute:144`
- Modify: `Assets/Workspace/11.0 Biomes/src/computes/CyclicCA.compute:130`

**Interfaces:**
- Consumes: `BurstEnvelope`, `BurstPhase`, `RisingEdge` from Task 3.
- Produces: `FieldSimulationBase.TriggerBurst()` (public, `[Button]`), `FieldSimulationBase.OutputEnvelope` (protected float), `SimulationBase.neuronIntensity` (`[NonSerialized] public float`), HLSL uniform `outputEnvelope`.

- [ ] **Step 1: Give sims a CPU-side firing level**

`NeuronFiringSource.Intensity` is a public CPU float, but sims only receive the GPU buffer.
In `SimulationBase.cs`, after the existing broadcast fields (near line 58):

```csharp
        // Shared neuron firing (assigned by SimulationManager from NeuronFiringSource)
        [NonSerialized] public ComputeBuffer neuronFiring;
        [NonSerialized] public int neuronFiringCount;
        // CPU-side aggregate of the same source, broadcast alongside the buffer. Field sims
        // detect a rising edge on this to auto-trigger a burst; reading it CPU-side avoids a
        // GPU readback and its sync point.
        [NonSerialized] public float neuronIntensity;
```

- [ ] **Step 2: Broadcast it**

In `SimulationManager.cs`, in the block at lines 277-286, add the intensity:

```csharp
            // 0b. Update neuron firing source (OSC frame + decay) and broadcast its buffer
            neuronFiring?.UpdateFiring();
            ComputeBuffer firingBuf = neuronFiring != null ? neuronFiring.Buffer : null;
            int firingCount = neuronFiring != null ? neuronFiring.NeuronCount : 0;
            float firingIntensity = neuronFiring != null ? neuronFiring.Intensity : 0f;
            foreach (var sim in simulations)
            {
                if (sim == null) continue;
                sim.neuronFiring = firingBuf;
                sim.neuronFiringCount = firingCount;
                sim.neuronIntensity = firingIntensity;
            }
```

- [ ] **Step 3: Add burst fields and state to `FieldSimulationBase`**

After the `[Header("Neuron ignition")]` block and before `[Header("Centre keep-out …")]`:

```csharp
        [Header("Burst (event-driven)")]
        [Tooltip("Off = the legacy continuous layer: the rule runs every step and the output " +
                 "is always visible. On = the sim is idle until triggered, then seeds, runs, " +
                 "fades and goes idle again, dispatching nothing in between.")]
        public bool burstEnabled = true;
        [Tooltip("How long a burst holds at full output, in SIM steps (not rule steps — " +
                 "stepEvery decimates only the rule). Measured from the trigger, so the " +
                 "release begins at fadeInSteps + burstSustainSteps.")]
        public int burstSustainSteps = 240;
        [Tooltip("Sim steps to ramp the output up after a trigger.")]
        public int fadeInSteps = 30;
        [Tooltip("Sim steps to ramp the output back down once the sustain ends.")]
        public int fadeOutSteps = 90;
        [Tooltip("A rising edge of the neuron firing level past this starts a burst. A trigger " +
                 "during a live burst extends it (resets the clock, keeps the lattice).")]
        [Range(0f, 1f)] public float burstFiringThreshold = 0.35f;
```

Next to the existing private state (`private int _ruleStep;`):

```csharp
        private BurstEnvelope _burst;
        private RisingEdge _firingEdge;
        private bool _outputDirty;   // outTex holds a frame that must be cleared on going idle

        /// <summary>Output multiplier for the render kernel. Always 1 when bursts are off.</summary>
        protected float OutputEnvelope => burstEnabled ? _burst.Value : 1f;
```

- [ ] **Step 4: Add the shader property ID**

In the `#region Shader property IDs` block of `FieldSimulationBase`:

```csharp
        protected static readonly int s_OutputEnvelopeID = Shader.PropertyToID("outputEnvelope");
```

- [ ] **Step 5: Bind the envelope**

In `BindCommon(int kernel)`, alongside the other per-kernel uniforms:

```csharp
            cs.SetFloat(s_OutputEnvelopeID, OutputEnvelope);
```

- [ ] **Step 6: Add `TriggerBurst()` and the idle clear**

Add to `FieldSimulationBase`, after `Reset()`:

```csharp
        /// <summary>
        /// Start a burst, or extend the one already running. Only a trigger arriving from idle
        /// re-seeds the grid — see <see cref="BurstEnvelope.Trigger"/>.
        /// </summary>
        [Button("Trigger burst")]
        public void TriggerBurst()
        {
            if (!IsConfigured) return;

            if (_burst.Trigger())
            {
                if (NeedsAllocation()) Allocate();
                GPUReset();
                _ruleStep = 0;
            }
            _outputDirty = true;
        }

        /// <summary>Blank the composited output so an idle sim leaves no stale frame behind.</summary>
        private void ClearOutput()
        {
            if (outTex == null) return;
            var prev = RenderTexture.active;
            RenderTexture.active = outTex;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = prev;
        }
```

- [ ] **Step 7: Gate `Step()`**

Replace the whole of `FieldSimulationBase.Step()` with:

```csharp
        public override void Step()
        {
            if (!IsConfigured) return;

            if (burstEnabled)
            {
                if (_firingEdge.Update(neuronIntensity, burstFiringThreshold))
                    TriggerBurst();

                _burst.Advance(fadeInSteps, burstSustainSteps, fadeOutSteps);

                if (_burst.IsIdle)
                {
                    // Nothing dispatches while idle — not the rule, not the render, not the
                    // publish. Publishing stops here deliberately: the last lattice stays in
                    // the biome channel and the PDE erodes it from now on.
                    if (_outputDirty) { ClearOutput(); _outputDirty = false; }
                    return;
                }
                _outputDirty = true;
            }

            // Rule decimation. Render still runs every step so the composite never stutters.
            _ruleStep++;
            if (_ruleStep % Mathf.Max(1, stepEvery) == 0)
            {
                GPUStep();
                SwapState();
            }
            Render();
            PublishToChannel();
        }
```

- [ ] **Step 8: Declare the uniform in the shared include**

In `cellular_common.hlsl`, after the centre keep-out block (around line 48):

```hlsl
// --- Output envelope (event-driven bursts) ----------------------------------------
// Multiplied into the RENDER only. The deposit published into the biome channel is NOT
// enveloped: the picture fades, the trace stays, and the biome PDE erodes it from there.
float outputEnvelope;
```

- [ ] **Step 9: Multiply it into both render kernels**

`LookupCA.compute` line 144 — replace:

```hlsl
    outTex[id.xy] = hsb2rgb(float3(hue, caSaturation, caBrightness * alive * keep), 1.0);
```

with:

```hlsl
    outTex[id.xy] = hsb2rgb(float3(hue, caSaturation, caBrightness * alive * keep * outputEnvelope), 1.0);
```

`CyclicCA.compute` line 130 — replace:

```hlsl
    outTex[id.xy] = hsb2rgb(float3(hue, ccaSaturation, ccaBrightness * keep), 1.0);
```

with:

```hlsl
    outTex[id.xy] = hsb2rgb(float3(hue, ccaSaturation, ccaBrightness * keep * outputEnvelope), 1.0);
```

- [ ] **Step 10: Verify in the Editor**

Enter Play mode with `burstEnabled` **off** on the CA. Expected: identical to before — continuous layer, always visible.

Now turn `burstEnabled` on. Expected: the layer goes dark and stays dark (no firing yet). Click
**Trigger burst** on the component. Expected: the lattice seeds, fades up over ~30 steps, holds
~240, fades out over ~90, then disappears and stays gone.

Console must show no errors.

- [ ] **Step 11: Confirm idle really costs nothing**

Temporarily restore the perf probe from the scratchpad backup if it is available, or simply
confirm by inspection that `Step()` returns before `GPUStep`/`Render`/`PublishToChannel` when
`_burst.IsIdle`. Expected saving when idle: ≈ 0.19 ms/step (measured 2026-08-10).

- [ ] **Step 12: Run the tests**

Test Runner > EditMode > Run All. Expected: 51 PASS.

- [ ] **Step 13: Commit**

```bash
git add "Assets/Workspace/11.0 Biomes/src/components/core/SimulationBase.cs" \
        "Assets/Workspace/11.0 Biomes/src/components/core/SimulationManager.cs" \
        "Assets/Workspace/11.0 Biomes/src/components/core/FieldSimulationBase.cs" \
        "Assets/Workspace/11.0 Biomes/src/computes/includes/cellular_common.hlsl" \
        "Assets/Workspace/11.0 Biomes/src/computes/LookupCA.compute" \
        "Assets/Workspace/11.0 Biomes/src/computes/CyclicCA.compute"
git commit -m "feat(sim): event-driven CA bursts — trigger, envelope, idle dispatches nothing"
```

---

### Task 5: Seed a burst from a biome channel

**Files:**
- Modify: `Assets/Workspace/11.0 Biomes/src/components/core/FieldSimulationBase.cs`
- Modify: `Assets/Workspace/11.0 Biomes/src/computes/includes/cellular_common.hlsl`
- Modify: `Assets/Workspace/11.0 Biomes/src/computes/LookupCA.compute` (`ResetStateKernel`, around line 71-85)
- Modify: `Assets/Workspace/11.0 Biomes/src/computes/CyclicCA.compute` (`ResetStateKernel`, around line 39-50)

**Interfaces:**
- Consumes: `FieldSimulationBase.publishTarget` (existing `Biome` reference), `Biome.FieldReadArray` (existing public `RenderTexture`).
- Produces: `FieldSimulationBase.seedFromChannel` (bool), `.seedChannel` (int), `.seedThreshold` (float); HLSL `SeededByChannel(uint2)`.

- [ ] **Step 1: Add the seeding fields**

In `FieldSimulationBase`, after the `[Header("Biome channel publish (CA as substrate)")]` block:

```csharp
        [Header("Seed from a biome channel")]
        [Tooltip("Seed the grid from a biome channel instead of the rule's own figure, so the " +
                 "automaton grows out of what the ecosystem is actually doing. Uses the same " +
                 "Biome as publishTarget. Off = the rule's built-in seeding.")]
        public bool seedFromChannel = false;
        [BiomeChannelField]
        [Tooltip("Channel sampled at reset. Read by UV, so the biome and the CA grid need not " +
                 "share dimensions.")]
        public int seedChannel = BiomeChannel.Pheromone0;
        [Tooltip("Cells at or above this normalized channel value are seeded live. Keep the seed " +
                 "SPARSE — a lookup CA forces the all-quiescent neighbourhood to stay quiescent, " +
                 "so scattered seeds grow fronts while a dense seed just boils on step one.")]
        [Range(0f, 1f)] public float seedThreshold = 0.5f;
```

- [ ] **Step 2: Add the shader property IDs**

In the `#region Shader property IDs` block:

```csharp
        protected static readonly int s_SeedFieldID = Shader.PropertyToID("seedField");
        protected static readonly int s_SeedChannelID = Shader.PropertyToID("seedChannelIndex");
        protected static readonly int s_SeedThresholdID = Shader.PropertyToID("seedThreshold");
        protected static readonly int s_SeedFromChannelID = Shader.PropertyToID("seedFromChannel");
```

- [ ] **Step 3: Bind them on reset**

In `GPUReset()`, immediately after `BindRuleParams(resetStateKernel);`:

```csharp
            // Channel seeding. Falls back to the rule's own figure whenever the wiring is
            // incomplete, so a half-configured sim seeds rather than throwing.
            bool canSeedFromChannel =
                seedFromChannel && publishTarget != null && publishTarget.FieldReadArray != null;
            cs.SetInt(s_SeedFromChannelID, canSeedFromChannel ? 1 : 0);
            cs.SetInt(s_SeedChannelID, Mathf.Clamp(seedChannel, 0, BiomeChannel.Count - 1));
            cs.SetFloat(s_SeedThresholdID, seedThreshold);
            cs.SetTexture(resetStateKernel, s_SeedFieldID,
                canSeedFromChannel ? publishTarget.FieldReadArray : Texture2D.blackTexture);
```

- [ ] **Step 4: Add the HLSL uniforms and helper**

In `cellular_common.hlsl`, after the `outputEnvelope` block from Task 4:

```hlsl
// --- Seeding from a biome channel -------------------------------------------------
// Sampled by UV, not by index: the biome runs at its own resolution (640x360 in 11.3)
// while the CA grid is sized independently, so the two never share dimensions. Same
// rescale the coupling gate performs.
Texture2DArray<float> seedField;
int   seedFromChannel;
int   seedChannelIndex;
float seedThreshold;

bool SeededByChannel(uint2 id)
{
    if (seedFromChannel == 0) return false;
    float2 uv = (float2(id) + 0.5) / float2((float)cellRezX, (float)cellRezY);
    float v = seedField.SampleLevel(sampler_point_clamp, float3(uv, (float)seedChannelIndex), 0);
    return v >= seedThreshold;
}
```

- [ ] **Step 5: Use it in `LookupCA.ResetStateKernel`**

The lookup CA already has an `inside` test for its `InitMode` figures. Replace:

```hlsl
    bool inside = false;
    if (initMode == 0)                     // line: a single horizontal filament
```

with:

```hlsl
    bool inside = false;
    if (seedFromChannel != 0)
    {
        // The channel replaces the figure entirely; initMode/initSize are ignored.
        inside = SeededByChannel(id.xy);
    }
    else if (initMode == 0)                // line: a single horizontal filament
```

- [ ] **Step 6: Use it in `CyclicCA.ResetStateKernel`**

The cyclic CA has no figure — it seeds the whole field. Replace:

```hlsl
    // min() guards the r == 1.0 corner, which would otherwise produce an out-of-range state.
    stateWrite[id.xy] = floor(min(r * (float)nstates, (float)nstates - 1.0));
```

with:

```hlsl
    // min() guards the r == 1.0 corner, which would otherwise produce an out-of-range state.
    float live = floor(min(r * (float)nstates, (float)nstates - 1.0));

    // With channel seeding the field starts EMPTY except where the channel is above
    // threshold. A uniformly-0 cyclic field never advances (no neighbour is ever in the
    // next state), so the waves grow outward from the seeded regions instead of igniting
    // everywhere at once.
    stateWrite[id.xy] = (seedFromChannel != 0 && !SeededByChannel(id.xy)) ? 0.0 : live;
```

- [ ] **Step 7: Verify in the Editor**

On the CA component set `seedFromChannel` on, `seedChannel` to `Pheromone_0`, `seedThreshold`
about 0.15. Make sure `publishTarget` is assigned. Enter Play, let physarum lay some pheromone,
then click **Trigger burst**.

Expected: the lattice appears only where pheromone trails already are, and grows from there.
If nothing appears, the threshold is above anything in that channel — lower it. If the whole
frame ignites, raise it.

Console must show no errors and no shader warnings.

- [ ] **Step 8: Run the tests**

Test Runner > EditMode > Run All. Expected: 51 PASS (this task adds no unit tests — it is
entirely GPU-side, verified on the render).

- [ ] **Step 9: Commit**

```bash
git add "Assets/Workspace/11.0 Biomes/src/components/core/FieldSimulationBase.cs" \
        "Assets/Workspace/11.0 Biomes/src/computes/includes/cellular_common.hlsl" \
        "Assets/Workspace/11.0 Biomes/src/computes/LookupCA.compute" \
        "Assets/Workspace/11.0 Biomes/src/computes/CyclicCA.compute"
git commit -m "feat(sim): seed a CA burst from a biome channel by threshold"
```

---

### Task 6: Let the deposited trace erode

**Files:**
- Modify: `Assets/Workspace/11.0 Biomes/src/components/core/BiomeFieldConfig.cs:86-87`
- Modify: `Assets/Workspace/11.1 CURRENTS Scene/assets/BiomeFieldConfig_Homeostatic.asset`
- Modify: `Assets/Workspace/11.2 SIGGRAPH Scene/assets/BiomeFieldConfig_Homeostatic.asset`
- Modify: `Assets/Workspace/11.3 SIGGRAPH DAC Scene/assets/BiomeFieldConfig_Homeostatic.asset`

**Interfaces:**
- Consumes: nothing.
- Produces: nothing referenced by other tasks. Behavioural change only.

- [ ] **Step 1: Change the code defaults**

In `BiomeFieldConfig.cs`, replace lines 86-87:

```csharp
            new() { name = "Excitability",   diffuseRate = 0f,     decayRate = 0f,     advectedByFlow = false, initialValue = 0f,   relaxRate = 0f },
            new() { name = "Substrate",      diffuseRate = 0f,     decayRate = 0f,     advectedByFlow = false, initialValue = 0f,   relaxRate = 0f },
```

with:

```csharp
            // NOT inert any more. A CA burst publishes its lattice here at full gain and then
            // stops publishing when it goes idle; from that moment these rates are what turns a
            // frozen deposit into a trace that erodes, spreads and drifts on the flow field.
            // decayRate 0.004 is roughly a 3 s half-life at 60 Hz — between Pheromone (0.002)
            // and Waste (0.001). Set all three back to 0 to restore CA-owned, inert channels.
            new() { name = "Excitability",   diffuseRate = 0.96f,  decayRate = 0.004f, advectedByFlow = true,  initialValue = 0f,   relaxRate = 0f },
            new() { name = "Substrate",      diffuseRate = 0.96f,  decayRate = 0.004f, advectedByFlow = true,  initialValue = 0f,   relaxRate = 0f },
```

- [ ] **Step 2: Update the comment on the channel constants**

In `BiomeFieldConfig.cs`, the block above `Excitability` currently claims both are CA-owned and
should be left inert. Replace:

```csharp
        // by agent sims through UmweltMapping, so a species responds to a cellular automaton
        // with no change to its shader — only its mapping asset. Both are CA-OWNED: leave
        // diffuseRate/relaxRate at 0 unless you deliberately want the pattern to bleed or
        // advect through the flow field.
```

with:

```csharp
        // by agent sims through UmweltMapping, so a species responds to a cellular automaton
        // with no change to its shader — only its mapping asset. A CA owns its channel WHILE
        // it is bursting (it publishes SetToward at full gain); once the burst goes idle it
        // stops publishing and the PDE takes the deposit over, so these channels deliberately
        // do bleed and advect. Zero the rates to get the old inert, CA-only behaviour back.
```

- [ ] **Step 3: Update the three scene assets**

For each of the three `BiomeFieldConfig_Homeostatic.asset` files, find the `Excitability` and
`Substrate` entries and set:

```yaml
  - name: Excitability
    diffuseRate: 0.96
    decayRate: 0.004
    advectedByFlow: 1
    initialValue: 0
    relaxRate: 0
  - name: Substrate
    diffuseRate: 0.96
    decayRate: 0.004
    advectedByFlow: 1
    initialValue: 0
    relaxRate: 0
```

Do this through the Inspector (select the asset, edit the channel rows) rather than by editing
YAML, so Unity rewrites the file consistently.

- [ ] **Step 4: Verify the erosion**

Enter Play. Trigger a burst, wait for it to fade fully to idle, then watch the Substrate channel
in the biome debug view. Expected: the lattice does not vanish with the picture and does not sit
frozen either — it softens, spreads and drifts, fading over a few seconds.

If it disappears instantly, `decayRate` is too high. If it never fades, the asset did not take —
confirm the scene is using the edited asset and not the code defaults.

- [ ] **Step 5: Confirm the other two scenes still run**

`BiomeChannel.Count` is unchanged at 15, so this is a values-only change, but open
`Scene_CURRENTS` and `Scene_SIGGRAPH`, enter Play briefly, and confirm no errors. Neither maps
ch13/ch14 in any `UmweltMapping`, so no agent behaviour should change.

- [ ] **Step 6: Commit**

```bash
git add "Assets/Workspace/11.0 Biomes/src/components/core/BiomeFieldConfig.cs" \
        "Assets/Workspace/11.1 CURRENTS Scene/assets/BiomeFieldConfig_Homeostatic.asset" \
        "Assets/Workspace/11.2 SIGGRAPH Scene/assets/BiomeFieldConfig_Homeostatic.asset" \
        "Assets/Workspace/11.3 SIGGRAPH DAC Scene/assets/BiomeFieldConfig_Homeostatic.asset"
git commit -m "feat(biome): CA channels erode, spread and advect once the burst stops publishing"
```

---

### Task 7: Prove the coupling, and write it down

The whole premise of ADR-0011 is that an agent species responds to a CA by editing one mapping
asset. As of this plan **nothing reads ch13 or ch14** — the claim has never been run. This task
tests it and records the work.

**Files:**
- Modify: `Assets/Workspace/11.3 SIGGRAPH DAC Scene/assets/UmweltBoid_Alt.asset`
- Modify: `docs/ARCHITECTURE.md`
- Create: `docs/sessions/2026-08-10-event-driven-ca.md`
- Modify: `docs/INDEX.md`

**Interfaces:**
- Consumes: everything above.
- Produces: nothing.

- [ ] **Step 1: Make one species perceive the lattice**

Select `UmweltBoid_Alt.asset`. Add one entry to `reads`:

- channel: `Substrate` (14)
- weight: `-1.5`
- effect: `Avoidance`

(Match whatever integer the `effect` dropdown shows for Avoidance; the existing rows use
`effect: 0` for Chemotaxis, `1` and `2` for others — pick from the dropdown, do not type a number.)

- [ ] **Step 2: Verify agents respond**

Enter Play, trigger a burst, and watch the boids. Expected: they steer away from the lattice
while it is up, and keep avoiding the eroding trace for a few seconds after it fades — then
resume normally once the channel has decayed.

**This is the ADR-0011 payoff being exercised for the first time.** If nothing happens, check
that `publishTarget` is assigned on the CA and that `publishGain` is not 0.

- [ ] **Step 3: Update `docs/ARCHITECTURE.md`**

In §3.4 (agent sims and field sims), append after the existing field-sim paragraph:

```markdown
Field sims are **event-driven** by default (`burstEnabled`). A burst is triggered by a rising
edge of the neuron firing level or by `TriggerBurst()`, seeds its grid — optionally from a biome
channel by threshold, so the automaton grows out of the ecosystem rather than an abstract figure
— holds for `burstSustainSteps`, then fades. While idle a field sim dispatches nothing at all:
no rule, no render, no publish. Because it stops publishing on going idle, the lattice it
deposited stays in its biome channel and is eroded, spread and advected by the PDE from then on.
Grid size is authored as an absolute cell height (`cellRezHeight`), with width derived from the
master's aspect, so the automaton's on-screen scale does not move when output resolution does.
```

Also update the §3.3 channel list note: `Excitability` and `Substrate` are no longer inert.

- [ ] **Step 4: Write the session log**

Create `docs/sessions/2026-08-10-event-driven-ca.md` following the repo's session format
(frontmatter `status: closed`, `date`, `tags`, `related`; sections Shipped / Decided / Open).
Record: the measured CA cost (0.190 ms of 9.257 ms; rule 0.011 / publish 0.007 / render 0.182)
and that this work was done for the look and the coupling, not for frames.

- [ ] **Step 5: Update `docs/INDEX.md`**

Add one line to `## Specs / plans` for the design doc and one to `## Sessions (newest first)`
for the session log, newest first in both.

- [ ] **Step 6: Run the full suite one last time**

Test Runner > EditMode > Run All. Expected: 51 PASS.

- [ ] **Step 7: Commit and push**

```bash
git add docs/ "Assets/Workspace/11.3 SIGGRAPH DAC Scene/assets/UmweltBoid_Alt.asset"
git commit -m "docs: event-driven CA session log, ARCHITECTURE; boids avoid the CA lattice"
git push origin ca-dev
```

---

## Self-Review

**Spec coverage:**

| Spec section | Task |
|---|---|
| §1 Absolute cell resolution | 1, 2 |
| §2 Seeding from a biome channel | 5 |
| §3 Burst lifecycle (trigger, retrigger-extends, envelope, idle) | 3, 4 |
| §4 Trace outlives the burst + channel config | 4 (publish stops on idle), 6 (rates) |
| §4.1 Why two CA channels | 7 (exercises the claim) |
| §5 Testing | 1, 3 (EditMode); 2, 4, 5, 6 (GPU checks) |

**Deviation from the spec, recorded here:** the spec's §5 promised EditMode tests for cell-rez
derivation, the envelope and the state machine without saying where they would live. They are
not reachable from the test assembly unless the logic leaves `Assembly-CSharp`, so Tasks 1 and 3
extract it into `Biomes.Core` — the same move `NeuronLayout` made, for the same reason. This is
a better decomposition than the spec implied, not a workaround.

**Type consistency:** `cellRezHeight` (int) is introduced in Task 2 and used nowhere else;
`CellGrid.Height/Width` signatures match their call sites; `BurstEnvelope.Trigger()` returns
`bool` (re-seed) and is consumed that way in Task 4; `OutputEnvelope` matches the HLSL uniform
name `outputEnvelope`; `seedFromChannel`/`seedChannelIndex`/`seedThreshold`/`seedField` are
spelled identically in the C# property IDs and the HLSL declarations.

**Known risk not mitigated:** `caParams` is a public serialized field holding a runtime clone, so
any Edit-mode reset writes a `ScriptableObject` into the scene file. Task 4 adds a `[Button]` that
triggers resets, which makes this more frequent. Marking `caParams` `[System.NonSerialized]` fixes
it, but that is a separate change and is deliberately not bundled here.
