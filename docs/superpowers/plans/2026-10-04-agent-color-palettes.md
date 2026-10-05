# Agent Colors: HSB + Palettes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Per-type agent colors become hue/saturation/brightness with an exact-match inspector color field, assignable from family-tagged palette assets (show presets + curated), and cyclable live with fades from `SimulationManager` / MFT / OSC.

**Architecture:** Pure color math and palette assignment live in `Biomes.Core` (unit-tested). The agent param sets gain `brightness` (legacy defaults, so renders stay bit-identical) and an `IAgentColorParams` family. `AgentColorPalette` assets are applied by an inspector section and by a `[Serializable]` `AgentPaletteCycler` that `SimulationManager` owns and re-imposes after every reset.

**Tech Stack:** Unity 6000.3.10f1 (HDRP, Metal), C#, HLSL compute, NUnit EditMode tests, Python 3 (one-off preset authoring).

**Spec:** `docs/superpowers/specs/2026-10-04-agent-color-palettes-design.md`

## Global Constraints

- Branch `feat/agent-color-palettes`; commit per task, conventional prefixes (`feat(colors): …`), concise subject, **no attribution trailers** (user CLAUDE.md).
- Paths below are relative to the repo root; `11.0` = `Assets/Workspace/11.0 Biomes`.
- `brightness` defaults: **0.8** Physarum and Boid, **1.0** Termite. Range entry `new("brightness", 0f, 1f)`.
- `"brightness"` is **appended** to each sim's `ModulatableParams` and **appended last** in every GPU type-params struct (C# mirror and HLSL, same commit). Struct sizes: Physarum 48 B / 12 floats, Boid 64 B / 16 floats, Termite 52 B / 13 floats.
- `AgentFamily` values are serialized ints: `Any = 0, Physarum = 1, Boid = 2, Termite = 3`. Never reorder.
- `SideButtonAction.NextPalette` / `PreviousPalette` are **appended** to the enum.
- OSC addresses: `/palette_next`, `/palette_prev`, `/palette <int>` (−1 = Preset); all queued to the main thread.
- MFT bank 2 rows per type column: **hue, saturation, brightness, diffuseRate**.
- `AgentPaletteCycler.fadeSeconds` default **2**; unscaled time; smoothstep easing; hue shortest arc via `ParameterInterpolator.LerpHue01`.
- Presets live in `11.0/assets/Palettes/Shows/` (5) and `11.0/assets/Palettes/Curated/` (5); exactly 10 palette assets.
- Every new file ships with a `.meta` whose GUID is minted (`python3 -c 'import uuid;print(uuid.uuid4().hex)'`) and written in the same tool call as the file — the author's Editor has the project open and refreshes on focus.
- Tests run headless in the APFS bench clone, never in the author's open project: `$T/benchtest.sh [filter]` (`T=/Users/toka/.claude/jobs/6043cdb2/tmp`; syncs `Assets/` + `ProjectSettings/` into `$T/bench/EoC-biomes-compute`, runs `unity test --mode EditMode`, prints totals and every non-passing test). Baseline before Task 1: 78 passed.

## Review Focus

1. **Grey/black palette swatch on a colored type** (Monochrome, SIGGRAPH's s = 0 physarum type): expect the type keeps its hue (and saturation for black). Pinned by `AgentColorTests` fallback cases and `PalettePresetTests` (grey source types compare hue via fallback).
2. **Reset or timeline start mid-fade**: expect the fresh clone gets the fade's current value, not the preset and not a jump. Pinned by the Task 8 live probe (`reset-mid-fade`).
3. **`/palette <i>` out of range, palette list edited during Play, `fadeSeconds` set to 0 mid-fade**: expect clamping, "(missing palette)" name, no exception, no NaN colors. Pinned by the Task 8 live probe (`select-clamp`, `shrink-list`, `zero-fade`).
4. **Old param assets without a `brightness` line in YAML** (every show asset and snapshot): expect the legacy default, never 0 (black agents). Pinned by `AgentColorAssetTests` (Task 3).
5. **HLSL/C# struct drift**: expect each render kernel to read brightness from the last float and color exactly as `AgentColor.ToRgb`, and each sim's C# upload struct to put it at the same offset. Pinned by `AgentRenderKernelTests` (Task 3): dispatches the real kernels, and checks the C# mirrors' `Marshal.OffsetOf` against the same layout table.

---

### Task 1: Exact HSB color math (`AgentColor`)

**Files:**
- Create: `11.0/src/core_math/AgentColor.cs` (+ `.meta`)
- Test: `Assets/Tests/EditMode/AgentColorTests.cs` (+ `.meta`)

**Interfaces:**
- Produces: `Biomes.AgentColor.ToRgb(float h, float s, float b) → Color` (alpha 1);
  `Biomes.AgentColor.FromRgb(Color c, float fallbackHue = 0f, float fallbackSat = 0f) → (float h, float s, float b)`.

- [ ] **Step 0: Baseline.** Run `$T/benchtest.sh`. Expected: `total=78 passed=78 failed=0`.

- [ ] **Step 1: Write the failing test** — `Assets/Tests/EditMode/AgentColorTests.cs`:

```csharp
using NUnit.Framework;
using UnityEngine;
using Biomes;

public class AgentColorTests
{
    private const float Eps = 1e-4f;

    private static void AssertColor(Color expected, Color actual, string because = "")
    {
        Assert.That(actual.r, Is.EqualTo(expected.r).Within(Eps), "r " + because);
        Assert.That(actual.g, Is.EqualTo(expected.g).Within(Eps), "g " + because);
        Assert.That(actual.b, Is.EqualTo(expected.b).Within(Eps), "b " + because);
    }

    private static float HueDistance(float a, float b)
    {
        float d = Mathf.Abs(a - b) % 1f;
        return Mathf.Min(d, 1f - d);
    }

    [TestCase(0f,       1f, 0f,       0f)]   // red
    [TestCase(1f / 6f,  1f, 1f,       0f)]   // yellow
    [TestCase(1f / 3f,  0f, 1f,       0f)]   // green
    [TestCase(0.5f,     0f, 1f,       1f)]   // cyan
    [TestCase(2f / 3f,  0f, 0f,       1f)]   // blue
    [TestCase(5f / 6f,  1f, 0f,       1f)]   // magenta
    [TestCase(1f,       1f, 0f,       0f)]   // hue 1 wraps to red
    [TestCase(1f / 24f, 1f, 0.15625f, 0f)]   // 15°: smoothstep(0.25); Unity HSV would give 0.25
    [TestCase(0.125f,   1f, 0.84375f, 0f)]   // 45°: smoothstep(0.75)
    public void ToRgb_FullySaturated_FollowsTheShaderCurve(float h, float r, float g, float b)
    {
        AssertColor(new Color(r, g, b), AgentColor.ToRgb(h, 1f, 1f));
    }

    [Test]
    public void ToRgb_SaturationAndBrightness_ScaleTowardWhiteAndBlack()
    {
        AssertColor(new Color(0.8f, 0.4f, 0.4f), AgentColor.ToRgb(0f, 0.5f, 0.8f));
        AssertColor(new Color(0.3f, 0.3f, 0.3f), AgentColor.ToRgb(0.42f, 0f, 0.3f));
        AssertColor(Color.black, AgentColor.ToRgb(0.42f, 0.7f, 0f));
    }

    [Test]
    public void FromRgb_InvertsReferenceColors()
    {
        var (h, s, b) = AgentColor.FromRgb(new Color(1f, 0.15625f, 0f));
        Assert.That(HueDistance(h, 1f / 24f), Is.LessThan(Eps));
        Assert.That(s, Is.EqualTo(1f).Within(Eps));
        Assert.That(b, Is.EqualTo(1f).Within(Eps));

        (h, s, b) = AgentColor.FromRgb(new Color(0.8f, 0.4f, 0.4f));
        Assert.That(HueDistance(h, 0f), Is.LessThan(Eps));
        Assert.That(s, Is.EqualTo(0.5f).Within(Eps));
        Assert.That(b, Is.EqualTo(0.8f).Within(Eps));
    }

    [Test]
    public void FromRgb_Grey_KeepsFallbackHue()
    {
        var (h, s, b) = AgentColor.FromRgb(new Color(0.5f, 0.5f, 0.5f), 0.3f, 0.9f);
        Assert.That(h, Is.EqualTo(0.3f));
        Assert.That(s, Is.EqualTo(0f));
        Assert.That(b, Is.EqualTo(0.5f).Within(Eps));
    }

    [Test]
    public void FromRgb_Black_KeepsFallbackHueAndSaturation()
    {
        var (h, s, b) = AgentColor.FromRgb(Color.black, 0.3f, 0.9f);
        Assert.That(h, Is.EqualTo(0.3f));
        Assert.That(s, Is.EqualTo(0.9f));
        Assert.That(b, Is.EqualTo(0f));
    }

    [Test]
    public void FromRgb_OfToRgb_RecoversHsbAroundTheWheel()
    {
        foreach (float s in new[] { 0.1f, 0.5f, 1f })
        foreach (float b in new[] { 0.2f, 0.8f, 1f })
        for (int i = 0; i < 48; i++)
        {
            float h = i / 48f;
            var (h2, s2, b2) = AgentColor.FromRgb(AgentColor.ToRgb(h, s, b));
            string at = $"h {h} s {s} b {b}";
            Assert.That(HueDistance(h2, h), Is.LessThan(Eps), at);
            Assert.That(s2, Is.EqualTo(s).Within(Eps), at);
            Assert.That(b2, Is.EqualTo(b).Within(Eps), at);
        }
    }

    [Test]
    public void ToRgb_OfFromRgb_RecoversAnyColor()
    {
        var rng = new System.Random(1);
        for (int i = 0; i < 500; i++)
        {
            var c = new Color((float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble());
            var (h, s, b) = AgentColor.FromRgb(c);
            AssertColor(c, AgentColor.ToRgb(h, s, b), c.ToString());
        }
    }

    [Test]
    public void FromRgb_HueWrapsIntoUnitRange()
    {
        // Red with a trace of blue lands on hue 6/6 in float; it must wrap to [0, 1).
        var (h, _, _) = AgentColor.FromRgb(new Color(1f, 0f, 1e-12f));
        Assert.That(h, Is.GreaterThanOrEqualTo(0f).And.LessThan(1f));
    }
}
```

Mint its `.meta` (MonoImporter template, fresh GUID):

```text
fileFormatVersion: 2
guid: <minted>
MonoImporter:
  externalObjects: {}
  serializedVersion: 2
  defaultReferences: []
  executionOrder: 0
  icon: {instanceID: 0}
  userData: 
  assetBundleName: 
  assetBundleVariant: 
```

- [ ] **Step 2: Run to verify it fails.** `$T/benchtest.sh AgentColorTests`. Expected: compile error / no results — `AgentColor` does not exist.

- [ ] **Step 3: Implement** — `11.0/src/core_math/AgentColor.cs` (+ `.meta`, same template):

```csharp
using UnityEngine;

namespace Biomes
{
    /// <summary>
    /// Agent color conversions that match the render kernels exactly. <see cref="ToRgb"/> ports
    /// <c>hsb2rgb</c> from <c>computes/includes/color.hlsl</c> line for line; its hue channels are
    /// smoothstepped, so between primaries it differs from <see cref="Color.HSVToRGB(float,float,float)"/>.
    /// <see cref="FromRgb"/> is its exact inverse, so a color picked in the Editor renders as picked.
    /// </summary>
    public static class AgentColor
    {
        /// <summary>The rendered color of hue / saturation / brightness (each 0..1), alpha 1.</summary>
        public static Color ToRgb(float h, float s, float b) =>
            new Color(Channel(h, s, b, 0f), Channel(h, s, b, 4f), Channel(h, s, b, 2f));

        private static float Channel(float h, float s, float b, float offset)
        {
            float p = Mathf.Clamp01(Mathf.Abs((h * 6f + offset) % 6f - 3f) - 1f);
            float q = p * p * (3f - 2f * p);
            return b * (1f - s + s * q);   // = b * lerp(1, q, s)
        }

        /// <summary>
        /// The hue / saturation / brightness that render as <paramref name="c"/>. A grey has no hue
        /// and keeps <paramref name="fallbackHue"/>; black has neither hue nor saturation and keeps both.
        /// </summary>
        public static (float h, float s, float b) FromRgb(Color c, float fallbackHue = 0f, float fallbackSat = 0f)
        {
            float r = Mathf.Clamp01(c.r), g = Mathf.Clamp01(c.g), bl = Mathf.Clamp01(c.b);
            float max = Mathf.Max(r, Mathf.Max(g, bl));
            float min = Mathf.Min(r, Mathf.Min(g, bl));
            if (max <= 0f) return (fallbackHue, fallbackSat, 0f);
            float range = max - min;
            if (range <= 0f) return (fallbackHue, 0f, max);

            // The max channel picks the sextant pair, the middle channel's smoothstepped
            // fraction (inverted) places the hue inside it.
            float h6;
            if (max == r)
                h6 = g >= bl ? InvSmooth((g - min) / range) : 6f - InvSmooth((bl - min) / range);
            else if (max == g)
                h6 = r >= bl ? 2f - InvSmooth((r - min) / range) : 2f + InvSmooth((bl - min) / range);
            else
                h6 = g >= r ? 4f - InvSmooth((g - min) / range) : 4f + InvSmooth((r - min) / range);

            float h = h6 / 6f;
            if (h >= 1f) h -= 1f;
            return (h, range / max, max);
        }

        // Inverse of the smoothstep cubic 3p² − 2p³ on [0, 1].
        private static float InvSmooth(float q) =>
            0.5f - Mathf.Sin(Mathf.Asin(Mathf.Clamp(1f - 2f * q, -1f, 1f)) / 3f);
    }
}
```

- [ ] **Step 4: Run to verify it passes.** `$T/benchtest.sh AgentColorTests`. Expected: all `AgentColorTests` pass, `failed=0`.

- [ ] **Step 5: Commit.**

```bash
git add "Assets/Workspace/11.0 Biomes/src/core_math/AgentColor.cs"* Assets/Tests/EditMode/AgentColorTests.cs*
git commit -m "feat(colors): exact C# port + inverse of shader hsb2rgb (AgentColor)"
```

---

### Task 2: Palette assignment rules (`PaletteAssign`)

**Files:**
- Create: `11.0/src/core_math/PaletteAssign.cs` (+ `.meta`)
- Test: `Assets/Tests/EditMode/PaletteAssignTests.cs` (+ `.meta`)

**Interfaces:**
- Produces: `enum AgentFamily { Any, Physarum, Boid, Termite }`;
  `[Serializable] class PaletteSwatch { Color color; AgentFamily family; PaletteSwatch(); PaletteSwatch(Color, AgentFamily); }`;
  `PaletteAssign.SwatchIndex(IReadOnlyList<PaletteSwatch>, AgentFamily, int typeIndex) → int` (−1 = none);
  `PaletteAssign.IsCandidate(IReadOnlyList<PaletteSwatch>, AgentFamily, int index) → bool`;
  `PaletteAssign.StoreFamily(List<PaletteSwatch>, AgentFamily, IReadOnlyList<Color>)`;
  `PaletteAssign.StepCycle(int active, int count, int delta) → int` (range −1..count−1).

- [ ] **Step 1: Write the failing test** — `Assets/Tests/EditMode/PaletteAssignTests.cs` (+ `.meta`):

```csharp
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Biomes;

public class PaletteAssignTests
{
    private static List<PaletteSwatch> Swatches(params AgentFamily[] tags)
    {
        var list = new List<PaletteSwatch>();
        for (int i = 0; i < tags.Length; i++)
            list.Add(new PaletteSwatch(new Color(i / 10f, 0f, 0f), tags[i]));
        return list;
    }

    private static int[] Assign(List<PaletteSwatch> swatches, AgentFamily family, int types)
    {
        var r = new int[types];
        for (int i = 0; i < types; i++) r[i] = PaletteAssign.SwatchIndex(swatches, family, i);
        return r;
    }

    [Test]
    public void TaggedSwatches_GoToTheirFamily_InOrder_Wrapping()
    {
        var sw = Swatches(AgentFamily.Physarum, AgentFamily.Physarum, AgentFamily.Boid, AgentFamily.Termite, AgentFamily.Any);
        Assert.That(Assign(sw, AgentFamily.Physarum, 3), Is.EqualTo(new[] { 0, 1, 0 }));
        Assert.That(Assign(sw, AgentFamily.Boid, 2), Is.EqualTo(new[] { 2, 2 }));
        Assert.That(Assign(sw, AgentFamily.Termite, 1), Is.EqualTo(new[] { 3 }));
    }

    [Test]
    public void FamilyWithoutTags_UsesUntaggedSwatches()
    {
        var sw = Swatches(AgentFamily.Any, AgentFamily.Physarum, AgentFamily.Any);
        Assert.That(Assign(sw, AgentFamily.Boid, 3), Is.EqualTo(new[] { 0, 2, 0 }));
    }

    [Test]
    public void NoOwnTagsAndNoUntagged_UsesEverySwatch()
    {
        var sw = Swatches(AgentFamily.Physarum, AgentFamily.Physarum);
        Assert.That(Assign(sw, AgentFamily.Termite, 3), Is.EqualTo(new[] { 0, 1, 0 }));
    }

    [Test]
    public void EmptyOrMissingPalette_HasNoSwatch()
    {
        Assert.That(PaletteAssign.SwatchIndex(new List<PaletteSwatch>(), AgentFamily.Boid, 0), Is.EqualTo(-1));
        Assert.That(PaletteAssign.SwatchIndex(null, AgentFamily.Boid, 0), Is.EqualTo(-1));
    }

    [Test]
    public void IsCandidate_AgreesWithAssignment()
    {
        var sw = Swatches(AgentFamily.Any, AgentFamily.Boid, AgentFamily.Any);
        Assert.That(PaletteAssign.IsCandidate(sw, AgentFamily.Boid, 1), Is.True);
        Assert.That(PaletteAssign.IsCandidate(sw, AgentFamily.Boid, 0), Is.False);
        Assert.That(PaletteAssign.IsCandidate(sw, AgentFamily.Physarum, 0), Is.True);
        Assert.That(PaletteAssign.IsCandidate(sw, AgentFamily.Physarum, 1), Is.False);
    }

    [Test]
    public void StoreFamily_ReplacesInPlace_KeepsOtherFamilies()
    {
        var sw = Swatches(AgentFamily.Any, AgentFamily.Physarum, AgentFamily.Boid, AgentFamily.Physarum);
        PaletteAssign.StoreFamily(sw, AgentFamily.Physarum, new[] { Color.red, Color.green, Color.blue });
        Assert.That(sw.ConvertAll(s => s.family), Is.EqualTo(new[] {
            AgentFamily.Any, AgentFamily.Physarum, AgentFamily.Physarum, AgentFamily.Physarum, AgentFamily.Boid }));
        Assert.That(sw[1].color, Is.EqualTo(Color.red));
        Assert.That(sw[3].color, Is.EqualTo(Color.blue));
    }

    [Test]
    public void StoreFamily_NewFamily_Appends()
    {
        var sw = Swatches(AgentFamily.Physarum);
        PaletteAssign.StoreFamily(sw, AgentFamily.Termite, new[] { Color.yellow });
        Assert.That(sw.Count, Is.EqualTo(2));
        Assert.That(sw[1].family, Is.EqualTo(AgentFamily.Termite));
        Assert.That(sw[1].color, Is.EqualTo(Color.yellow));
    }

    [Test]
    public void StepCycle_WrapsThroughPreset()
    {
        Assert.That(PaletteAssign.StepCycle(-1, 3, +1), Is.EqualTo(0));
        Assert.That(PaletteAssign.StepCycle(2, 3, +1), Is.EqualTo(-1));
        Assert.That(PaletteAssign.StepCycle(-1, 3, -1), Is.EqualTo(2));
        Assert.That(PaletteAssign.StepCycle(0, 3, -1), Is.EqualTo(-1));
        Assert.That(PaletteAssign.StepCycle(-1, 0, +1), Is.EqualTo(-1));
    }
}
```

- [ ] **Step 2: Run to verify it fails.** `$T/benchtest.sh PaletteAssignTests`. Expected: compile error — `PaletteAssign` / `AgentFamily` / `PaletteSwatch` missing.

- [ ] **Step 3: Implement** — `11.0/src/core_math/PaletteAssign.cs` (+ `.meta`):

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Biomes
{
    /// <summary>Which agent sim a palette swatch is for. <see cref="Any"/> = untagged, serves every sim.
    /// Serialized as int — append only.</summary>
    public enum AgentFamily { Any, Physarum, Boid, Termite }

    /// <summary>One palette color: the rendered color at full trail intensity, optionally tagged
    /// with the sim family it belongs to.</summary>
    [Serializable]
    public class PaletteSwatch
    {
        public Color color = Color.white;
        public AgentFamily family;

        public PaletteSwatch() { }
        public PaletteSwatch(Color color, AgentFamily family) { this.color = color; this.family = family; }
    }

    /// <summary>
    /// Palette → agent type assignment. A family draws from the swatches tagged with it; if there
    /// are none, from the untagged ones; if there are none either, from all of them. Type i takes
    /// the (i mod n)-th, so a show palette tagged per sim reproduces each sim, and an untagged
    /// curated palette serves every sim.
    /// </summary>
    public static class PaletteAssign
    {
        /// <summary>Index into <paramref name="swatches"/> for agent type <paramref name="typeIndex"/>;
        /// −1 when the palette has no swatches.</summary>
        public static int SwatchIndex(IReadOnlyList<PaletteSwatch> swatches, AgentFamily family, int typeIndex)
        {
            if (swatches == null || swatches.Count == 0 || typeIndex < 0) return -1;
            var pool = PoolFor(swatches, family);
            int n = 0;
            for (int i = 0; i < swatches.Count; i++)
                if (InPool(swatches[i], pool)) n++;
            int want = typeIndex % n;
            for (int i = 0, k = 0; i < swatches.Count; i++)
                if (InPool(swatches[i], pool) && k++ == want) return i;
            return -1;
        }

        /// <summary>True when swatch <paramref name="index"/> is one that <paramref name="family"/> draws from.</summary>
        public static bool IsCandidate(IReadOnlyList<PaletteSwatch> swatches, AgentFamily family, int index) =>
            InPool(swatches[index], PoolFor(swatches, family));

        /// <summary>Replaces the swatches tagged <paramref name="family"/> with <paramref name="colors"/>,
        /// in order, where the first of them was (appended when the family had none).</summary>
        public static void StoreFamily(List<PaletteSwatch> swatches, AgentFamily family, IReadOnlyList<Color> colors)
        {
            int at = swatches.FindIndex(s => s.family == family);
            if (at < 0) at = swatches.Count;
            swatches.RemoveAll(s => s.family == family);   // all at or after `at`: the index still holds
            for (int i = 0; i < colors.Count; i++)
                swatches.Insert(at + i, new PaletteSwatch(colors[i], family));
        }

        /// <summary>Steps a cycle over Preset (−1) and palettes 0..count−1, wrapping both ways.</summary>
        public static int StepCycle(int active, int count, int delta)
        {
            int stops = count + 1;
            return ((active + 1 + delta) % stops + stops) % stops - 1;
        }

        // The family's own tag, else Any, else every swatch (null).
        private static AgentFamily? PoolFor(IReadOnlyList<PaletteSwatch> swatches, AgentFamily family)
        {
            if (family != AgentFamily.Any && Has(swatches, family)) return family;
            if (Has(swatches, AgentFamily.Any)) return AgentFamily.Any;
            return null;
        }

        private static bool InPool(PaletteSwatch s, AgentFamily? pool) => pool == null || s.family == pool.Value;

        private static bool Has(IReadOnlyList<PaletteSwatch> swatches, AgentFamily tag)
        {
            for (int i = 0; i < swatches.Count; i++)
                if (swatches[i].family == tag) return true;
            return false;
        }
    }
}
```

- [ ] **Step 4: Run to verify it passes.** `$T/benchtest.sh PaletteAssignTests`. Expected: all pass.

- [ ] **Step 5: Commit.**

```bash
git add "Assets/Workspace/11.0 Biomes/src/core_math/PaletteAssign.cs"* Assets/Tests/EditMode/PaletteAssignTests.cs*
git commit -m "feat(colors): palette swatch families + assignment/cycle rules (PaletteAssign)"
```

---

### Task 3: Per-type `brightness` end to end

**Files:**
- Modify: `11.0/src/params/PhysarumParams.cs`, `BoidParams.cs`, `TermiteParams.cs` (type field, `ranges`, `GetValue`, `SetValue`)
- Modify: `11.0/src/components/Sim/PhysarumSim.cs`, `BoidSim.cs`, `TermiteSim.cs` (`s_ModulatableParams`, GPU struct, `UploadTypeParams`, `SetParameter`, `SetParameterDelta`, `GetParameter`)
- Modify: `11.0/src/computes/includes/physarum_type_params.hlsl`, `boid_type_params.hlsl`, `termite_type_params.hlsl`
- Modify: `11.0/src/computes/PhysarumSim.compute:340`, `BoidSim.compute:519`, `TermiteSim.compute:351`
- Modify: `11.0/MIDI_OSC.md` (param lists)
- Test: `Assets/Tests/EditMode/AgentRenderKernelTests.cs`, `Assets/Tests/EditMode/AgentColorAssetTests.cs` (+ `.meta`s)

**Interfaces:**
- Consumes: `AgentColor.ToRgb` (Task 1).
- Produces: `PhysarumAgentType.brightness` (0.8), `BoidAgentType.brightness` (0.8), `TermiteAgentType.brightness` (1.0); param name `"brightness"` via `GetValue`/`SetValue`/`GetParameter`/`SetParameter`/`SetParameterDelta`; HLSL `float brightness` last in each struct.

- [ ] **Step 1: Write the failing tests.**

`Assets/Tests/EditMode/AgentRenderKernelTests.cs`:

```csharp
using System.Reflection;
using System.Runtime.InteropServices;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Biomes;

/// <summary>
/// Dispatches each agent sim's RenderKernel on a hand-made trail field and compares the output
/// with AgentColor.ToRgb. Pins at once: the C# port matches the GPU, each kernel reads brightness
/// from the last float of its type-params struct, and the rest of the color path is unchanged.
/// The mirror tests pin the sims' C# upload structs to the same layout (reflection: the test
/// assembly cannot reference Assembly-CSharp).
/// </summary>
public class AgentRenderKernelTests
{
    private const string Dir = "Assets/Workspace/11.0 Biomes/src/computes/";

    // Float offsets inside each kernel's type-params struct (computes/includes/*_type_params.hlsl).
    private struct Layout
    {
        public string file, sim, gpuStruct; public int floats, hue, sat, brightness; public bool termite;
    }

    private static readonly Layout Physarum = new Layout { file = "PhysarumSim.compute", sim = "Biomes.PhysarumSim", gpuStruct = "PhysarumTypeParamsGPU", floats = 12, hue = 7,  sat = 8,  brightness = 11 };
    private static readonly Layout Boid     = new Layout { file = "BoidSim.compute",     sim = "Biomes.BoidSim",     gpuStruct = "BoidTypeParamsGPU",     floats = 16, hue = 11, sat = 12, brightness = 15 };
    private static readonly Layout Termite  = new Layout { file = "TermiteSim.compute",  sim = "Biomes.TermiteSim",  gpuStruct = "TermiteTypeParamsGPU",  floats = 13, hue = 10, sat = 11, brightness = 12, termite = true };

    // One texel per case; values per type. 1.5 exercises the >1 paths (termite → white).
    private static readonly float[] Type0 = { 1f, 0.5f, 0f, 1.5f };
    private static readonly float[] Type1 = { 0f, 0.5f, 1f, 0.25f };
    private static readonly (float h, float s, float b)[] Hsb = { (0.05f, 0.9f, 0.5f), (0.6f, 0.4f, 0.95f) };

    [Test] public void PhysarumRender_UsesTypeBrightness() => AssertRender(Physarum);
    [Test] public void BoidRender_UsesTypeBrightness()     => AssertRender(Boid);
    [Test] public void TermiteRender_UsesTypeBrightness()  => AssertRender(Termite);

    [Test] public void PhysarumUploadStruct_MatchesKernel() => AssertMirror(Physarum);
    [Test] public void BoidUploadStruct_MatchesKernel()     => AssertMirror(Boid);
    [Test] public void TermiteUploadStruct_MatchesKernel()  => AssertMirror(Termite);

    private static void AssertMirror(Layout k)
    {
        var type = System.Type.GetType(k.sim + ", Assembly-CSharp")?.GetNestedType(k.gpuStruct, BindingFlags.NonPublic);
        Assert.That(type, Is.Not.Null, k.sim + "." + k.gpuStruct);
        Assert.That(Marshal.SizeOf(type), Is.EqualTo(k.floats * 4), "size");
        Assert.That((int)Marshal.OffsetOf(type, "hue"), Is.EqualTo(k.hue * 4), "hue");
        Assert.That((int)Marshal.OffsetOf(type, "saturation"), Is.EqualTo(k.sat * 4), "saturation");
        Assert.That((int)Marshal.OffsetOf(type, "brightness"), Is.EqualTo(k.brightness * 4), "brightness");
    }

    private static void AssertRender(Layout k)
    {
        if (!SystemInfo.supportsComputeShaders) Assert.Ignore("No compute support in this Editor (-nographics?)");
        var cs = AssetDatabase.LoadAssetAtPath<ComputeShader>(Dir + k.file);
        Assert.That(cs, Is.Not.Null, k.file);
        int kernel = cs.FindKernel("RenderKernel");
        const int types = 2, w = 4, h = 1;

        var trail = new Texture2DArray(w, h, types + 1, TextureFormat.RFloat, false, true) { filterMode = FilterMode.Point };
        trail.SetPixelData(Type0, 0, 0);
        trail.SetPixelData(Type1, 0, 1);
        trail.SetPixelData(new float[w * h], 0, 2);
        trail.Apply(false);

        var outTex = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBFloat) { enableRandomWrite = true };
        outTex.Create();
        var prev = RenderTexture.active;
        RenderTexture.active = outTex;
        GL.Clear(false, true, Color.clear);
        RenderTexture.active = prev;

        var data = new float[types * k.floats];
        for (int t = 0; t < types; t++)
        {
            data[t * k.floats + k.hue] = Hsb[t].h;
            data[t * k.floats + k.sat] = Hsb[t].s;
            data[t * k.floats + k.brightness] = Hsb[t].b;
        }
        var buffer = new ComputeBuffer(types, k.floats * sizeof(float));
        buffer.SetData(data);

        try
        {
            cs.SetTexture(kernel, "trailRead", trail);
            cs.SetTexture(kernel, "outTex", outTex);
            cs.SetBuffer(kernel, "typeParams", buffer);
            cs.SetInt("typeCount", types);
            cs.SetInt("rezX", w);
            cs.SetInt("rezY", h);
            cs.SetFloat("persistence", 1f);
            cs.Dispatch(kernel, 1, 1, 1);   // numthreads(8,8,1) covers 4×1

            var request = AsyncGPUReadback.Request(outTex);
            request.WaitForCompletion();
            Assert.That(request.hasError, Is.False, "readback");
            var px = request.GetData<Color>();
            for (int x = 0; x < w; x++)
            {
                var e = Expected(k, Type0[x], Type1[x]);
                string at = $"{k.file} texel {x}";
                Assert.That(px[x].r, Is.EqualTo(e.r).Within(1e-4f), at + " r");
                Assert.That(px[x].g, Is.EqualTo(e.g).Within(1e-4f), at + " g");
                Assert.That(px[x].b, Is.EqualTo(e.b).Within(1e-4f), at + " b");
                Assert.That(px[x].a, Is.EqualTo(e.a).Within(1e-4f), at + " a");
            }
        }
        finally
        {
            buffer.Release();
            outTex.Release();
            Object.DestroyImmediate(outTex);
            Object.DestroyImmediate(trail);
        }
    }

    // RenderKernel with outTex = 0 and persistence = 1: saturate(Σ color_t / typeCount).
    private static Color Expected(Layout k, float v0, float v1)
    {
        var sum = Color.clear;
        float[] v = { v0, v1 };
        for (int t = 0; t < 2; t++)
        {
            Color c;
            if (!k.termite)
            {
                c = AgentColor.ToRgb(Hsb[t].h, Hsb[t].s, Hsb[t].b * v[t]);
                c.a = v[t];
            }
            else
            {
                float baseB = Mathf.Clamp01(v[t]), white = Mathf.Clamp01(v[t] - 1f);
                c = AgentColor.ToRgb(Hsb[t].h, Hsb[t].s * (1f - white), Hsb[t].b * baseB);
                c = new Color(Mathf.Lerp(c.r, 1f, white), Mathf.Lerp(c.g, 1f, white), Mathf.Lerp(c.b, 1f, white), baseB);
            }
            sum += c / 2f;
        }
        return new Color(Mathf.Clamp01(sum.r), Mathf.Clamp01(sum.g), Mathf.Clamp01(sum.b), Mathf.Clamp01(sum.a));
    }
}
```

`Assets/Tests/EditMode/AgentColorAssetTests.cs`:

```csharp
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Archived snapshots predate the brightness field. Unity fills a field missing from YAML from
/// its C# initializer, which must equal the constant each render kernel used to hardcode
/// (Physarum/Boid 0.8, Termite 1.0) so every exhibited look renders unchanged.
/// </summary>
public class AgentColorAssetTests
{
    private const string Snapshots = "Assets/Workspace/11.1 CURRENTS Scene/assets/Snapshots/";

    [TestCase("PhysarumParams_20260607.asset", 0.8f)]
    [TestCase("Boid_20260610_150648.asset", 0.8f)]
    [TestCase("Termite_20260610_150648.asset", 1f)]
    public void SnapshotWithoutBrightness_LoadsLegacyDefault(string file, float legacy)
    {
        var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(Snapshots + file);
        Assert.That(asset, Is.Not.Null, file);
        var types = new SerializedObject(asset).FindProperty("types");
        Assert.That(types.arraySize, Is.GreaterThan(0), file);
        for (int i = 0; i < types.arraySize; i++)
        {
            var b = types.GetArrayElementAtIndex(i).FindPropertyRelative("brightness");
            Assert.That(b, Is.Not.Null, $"{file} type {i}: no brightness field");
            Assert.That(b.floatValue, Is.EqualTo(legacy), $"{file} type {i}");
        }
    }
}
```

Mint both `.meta`s.

- [ ] **Step 2: Run to verify they fail.** `$T/benchtest.sh "AgentRenderKernelTests|AgentColorAssetTests"`. Expected: `AgentColorAssetTests` fail ("no brightness field"); the three render tests fail on the r/g/b of texels 0–3 (kernels still use 0.8 / 1.0 and the 11/15/12-float struct); the three mirror tests fail on size (`brightness` field missing → `OffsetOf` throws). If the render tests report **Ignored**, the runner has no GPU: run them through the bench Editor instead (Task 8 Step 1 launches it) before going on.

- [ ] **Step 3: Params.** In each type class add the field after `saturation`, then extend `ranges`, `GetValue`, `SetValue` right after their `saturation` entries.

`PhysarumParams.cs` / `BoidParams.cs` (type class):
```csharp
        public float saturation = 0.5f;
        public float brightness = 0.8f;   // HSB value at full trail; 0.8 = the constant the kernel used to hardcode
```
`TermiteParams.cs` (type class):
```csharp
        public float saturation = 0.7f;
        public float brightness = 1f;     // HSB value at full trail; 1 = the kernel's old implicit value
```
All three, `ranges` (after the `"saturation"` entry, keep the file's column alignment):
```csharp
            new("brightness",    0f,    1f),
```
All three, `GetValue` switch:
```csharp
                "brightness"    => t.brightness,
```
All three, `SetValue` switch:
```csharp
                case "brightness":    t.brightness    = raw; break;
```

- [ ] **Step 4: HLSL structs** — append last in each struct and update the size comment.

`physarum_type_params.hlsl`:
```hlsl
    float firingSpeedMul;
    float firingDepositAmount;
    float brightness;          // HSB value at full trail (appended last: no older field moves)
};  // 48 bytes (12 floats)
```
`boid_type_params.hlsl`:
```hlsl
    float firingSpeedMul;
    float firingDepositAmount;
    float brightness;          // HSB value at full trail (appended last: no older field moves)
};  // 64 bytes (16 floats)
```
`termite_type_params.hlsl`:
```hlsl
    float hue;
    float saturation;
    float brightness;          // HSB value at full trail (appended last: no older field moves)
};  // 52 bytes (13 floats)
```

- [ ] **Step 5: Render kernels.**

`PhysarumSim.compute` and `BoidSim.compute` `RenderKernel`:
```hlsl
        color += hsb2rgb(float3(p.hue, p.saturation, p.brightness * val), val) / typeCount;
```
`TermiteSim.compute` `RenderKernel`:
```hlsl
        float4 c = hsb2rgb(float3(p.hue, p.saturation * (1.0 - white), p.brightness * baseB), baseB);
```

- [ ] **Step 6: Sims.** In each of `PhysarumSim.cs`, `BoidSim.cs`, `TermiteSim.cs`:

`s_ModulatableParams` — append `"brightness"` as the last element (Physarum `… "hue", "saturation", "brightness" };`, Boid `… "hue", "saturation", "diffuseRate", "brightness" };`, Termite `… "hue", "saturation", "brightness" };`).

GPU struct — add as the last field:
```csharp
            public float brightness;   // last, matching the HLSL struct
```
`UploadTypeParams` initializer — add as the last member:
```csharp
                    brightness = t.brightness,
```
`SetParameter`:
```csharp
                case "brightness":    t.brightness    = R(paramName, value); break;
```
`SetParameterDelta`:
```csharp
                case "brightness":    t.brightness    = D(paramName, t.brightness, delta); break;
```
`GetParameter`:
```csharp
                "brightness"    => t.brightness,
```

- [ ] **Step 7: `MIDI_OSC.md` param lists.** Replace the "Param Ranges" sim lists with:

```markdown
### Physarum (10 params)

moveSpeed, senseAngle, turnAngle, senseDistance, depositAmount, eatAmount, diffuseRate, hue, saturation, brightness

### Boid (12 params)

maxSpeed, maxForce, separateRange, alignRange, attractRange, depositAmount, eatAmount, foodSeek, hue, saturation, diffuseRate, brightness

### Termite (9 params)

moveSpeed, senseAngle, turnAngle, senseDistance, depositAmount, diffuseRate, hue, saturation, brightness

`brightness` is the HSB value a type renders at full trail (0–1; defaults 0.8 Physarum/Boid, 1.0 Termite — the old hardcoded look).
```
and change "Both sims support **1-8 agent types**" to "Each agent sim supports **1-8 agent types**".

- [ ] **Step 8: Run to verify they pass.** `$T/benchtest.sh`. Expected: all previous tests + `AgentColorTests` + `PaletteAssignTests` + 3 render + 3 mirror + 3 asset tests pass, `failed=0`; `benchtest.log` has no `Shader error` lines (`grep -n "Shader error\|error CS" $T/benchtest.log` → nothing).

- [ ] **Step 9: Commit.**

```bash
git add -A "Assets/Workspace/11.0 Biomes/src" "Assets/Workspace/11.0 Biomes/MIDI_OSC.md" Assets/Tests/EditMode
git commit -m "feat(colors): per-type brightness for physarum/boid/termite (legacy defaults, bit-identical render)"
```

---

### Task 4: MFT bank 2 → hue, saturation, brightness, diffuseRate

**Files:**
- Modify: `11.0/src/components/network/MidiFighterTwister.cs` (`BuildBank2_VisualAndBiome`, class doc line 31, method summary)
- Modify: `11.0/MIDI_OSC.md` (soft bank table row 2)

**Interfaces:**
- Consumes: param name `"brightness"` (Task 3).

- [ ] **Step 1: Rebind rows 2–3.** In `BuildBank2_VisualAndBiome`:

```csharp
                if (e0 < TOTAL_ENCODERS) bindings[e0] = MakeSimParamBinding(simIdx, "hue", typeIdx);
                if (e1 < TOTAL_ENCODERS) bindings[e1] = MakeSimParamBinding(simIdx, "saturation", typeIdx);
                if (e2 < TOTAL_ENCODERS) bindings[e2] = MakeSimParamBinding(simIdx, "brightness", typeIdx);
                if (e3 < TOTAL_ENCODERS) bindings[e3] = MakeSimParamBinding(simIdx, "diffuseRate", typeIdx);
```
Doc lines:
```csharp
    ///   Bank 2: Visual + Biome (hue, saturation, brightness, diffuseRate per type; biome cross-field interactions)
```
```csharp
        /// <summary>Bank 2: Visual (hue, saturation, brightness, diffuseRate per type) + Biome cross-field interactions.</summary>
```

- [ ] **Step 2: Docs.** `MIDI_OSC.md` soft bank table row:

```markdown
| 2: Visual+Biome  | hue, saturation, brightness, diffuseRate per type + biome cross-field (col 3 of HW bank 4) | Green       |
```
and below the table add: "`depositAmount` left bank 2 for brightness; it stays on bank 1 for every sim."

- [ ] **Step 3: Verify.** `$T/benchtest.sh` → `failed=0` (compiles). Read the diff: rows 0/1 untouched, `depositAmount` still at `ModulatableParams` index 4–7 for all three sims (Physarum 4, Boid 5, Termite 4).

- [ ] **Step 4: Commit.**

```bash
git add "Assets/Workspace/11.0 Biomes/src/components/network/MidiFighterTwister.cs" "Assets/Workspace/11.0 Biomes/MIDI_OSC.md"
git commit -m "feat(mft): bank 2 rows hue/sat/brightness/diffuse (depositAmount stays on bank 1)"
```

---

### Task 5: Palette asset, agent-color interface, presets

**Files:**
- Create: `11.0/src/params/IAgentColorParams.cs`, `11.0/src/params/AgentColorPalette.cs` (+ `.meta`s)
- Modify: `PhysarumParams.cs`, `BoidParams.cs`, `TermiteParams.cs` (implement `IAgentColorParams`)
- Create: `11.0/assets/Palettes/{Shows,Curated}/*.asset` (+ folder and asset `.meta`s) via `$T/gen_palettes.py`
- Test: `Assets/Tests/EditMode/PalettePresetTests.cs` (+ `.meta`)

**Interfaces:**
- Consumes: `AgentColor` (Task 1), `PaletteAssign`/`PaletteSwatch`/`AgentFamily` (Task 2), `brightness` (Task 3).
- Produces: `interface IAgentColorParams : IParamSet { AgentFamily Family { get; } }`;
  extension methods `IParamSet.GetHsb(int) → (float h, float s, float b)` and `IParamSet.SetHsb(int, (float h, float s, float b))`;
  `AgentColorPalette : ScriptableObject { string notes; List<PaletteSwatch> swatches; bool TryGetTarget(AgentFamily, int typeIndex, float fallbackHue, float fallbackSat, out (float h, float s, float b)); void ApplyTo(IAgentColorParams); void StoreFrom(IAgentColorParams); }`.

- [ ] **Step 1: Write the failing test** — `Assets/Tests/EditMode/PalettePresetTests.cs` (+ `.meta`):

```csharp
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Biomes;

/// <summary>
/// Each show palette must reproduce what its scene exhibited: per family, the tagged swatches
/// in order invert (AgentColor.FromRgb) to the source asset's hue/saturation at the brightness
/// the kernels hardcoded (Physarum/Boid 0.8, Termite 1.0).
/// </summary>
public class PalettePresetTests
{
    private const string W = "Assets/Workspace/";
    private const string Palettes = W + "11.0 Biomes/assets/Palettes";
    private const float Eps = 1e-4f;

    private static IEnumerable<TestCaseData> Sources()
    {
        const string c = W + "11.1 CURRENTS Scene/assets/", s = W + "11.2 SIGGRAPH Scene/assets/", b = W + "11.3 Brave New Work Scene/assets/";
        yield return Case("CURRENTS", AgentFamily.Physarum, c + "PhysarumParams.asset");
        yield return Case("CURRENTS", AgentFamily.Boid,     c + "BoidParams.asset");
        yield return Case("CURRENTS", AgentFamily.Termite,  c + "TermiteParams.asset");
        yield return Case("SIGGRAPH", AgentFamily.Physarum, s + "Snapshots/Physarum_20260711_201424_SHOWVERSION 1.asset");
        yield return Case("SIGGRAPH", AgentFamily.Boid,     s + "Snapshots/Boid_20260711_200244.asset");
        yield return Case("SIGGRAPH", AgentFamily.Termite,  s + "Snapshots/Termite_20260711_200244.asset");
        yield return Case("SIGGRAPH DAC", AgentFamily.Physarum, s + "DAC_params/Physarum_20260908_181119_DAC.asset");
        yield return Case("SIGGRAPH DAC", AgentFamily.Boid,     s + "DAC_params/Boid_20260908_181119_DAC.asset");
        yield return Case("SIGGRAPH DAC", AgentFamily.Termite,  s + "DAC_params/Termite_20260908_181119_DAC.asset");
        yield return Case("Brave New Work", AgentFamily.Physarum, b + "PhysarumParams.asset");
        yield return Case("Brave New Work", AgentFamily.Boid,     b + "BoidParams.asset");
        yield return Case("Brave New Work", AgentFamily.Termite,  b + "TermiteParams.asset");
    }

    private static TestCaseData Case(string palette, AgentFamily family, string source) =>
        new TestCaseData(palette, family, source).SetName($"ShowPalette_Reproduces({palette}, {family})");

    [TestCaseSource(nameof(Sources))]
    public void ShowPalette_ReproducesSourceColors(string palette, AgentFamily family, string sourcePath)
    {
        var types = Load(sourcePath).FindProperty("types");
        var source = new List<(float h, float s)>();
        for (int i = 0; i < types.arraySize; i++)
        {
            var t = types.GetArrayElementAtIndex(i);
            source.Add((t.FindPropertyRelative("hue").floatValue, t.FindPropertyRelative("saturation").floatValue));
        }
        AssertFamilyMatches(palette, family, source, family == AgentFamily.Termite ? 1f : 0.8f);
    }

    [Test]
    public void Metaesthetica_ReproducesVisapPhysarum()
    {
        var so = Load(W + "10.0 Metaesthetica/assets/ParamsPhysarum_VISAP.asset");
        Vector4 hues = so.FindProperty("m_Hues").vector4Value, sats = so.FindProperty("m_Saturations").vector4Value;
        var source = new List<(float h, float s)>();
        for (int i = 0; i < 4; i++) source.Add((hues[i], sats[i]));
        AssertFamilyMatches("Metaesthetica VISAP", AgentFamily.Physarum, source, 0.8f);
    }

    [Test]
    public void TenPalettes_EachWithSwatches()
    {
        var guids = AssetDatabase.FindAssets("t:AgentColorPalette", new[] { Palettes });
        Assert.That(guids.Length, Is.EqualTo(10));
        foreach (var g in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(g);
            Assert.That(Load(path).FindProperty("swatches").arraySize, Is.GreaterThan(0), path);
        }
    }

    private static void AssertFamilyMatches(string palette, AgentFamily family, List<(float h, float s)> source, float legacyB)
    {
        var swatches = Load($"{Palettes}/Shows/{palette}.asset").FindProperty("swatches");
        var colors = new List<Color>();
        for (int i = 0; i < swatches.arraySize; i++)
        {
            var sw = swatches.GetArrayElementAtIndex(i);
            if (sw.FindPropertyRelative("family").intValue == (int)family)
                colors.Add(sw.FindPropertyRelative("color").colorValue);
        }
        Assert.That(colors.Count, Is.EqualTo(source.Count), $"{palette} {family}: swatch count");
        for (int i = 0; i < colors.Count; i++)
        {
            // Source values as fallbacks: a grey swatch (s = 0) keeps the source hue, as Apply does.
            var (h, s, b) = AgentColor.FromRgb(colors[i], source[i].h, source[i].s);
            float dh = Mathf.Abs(h - source[i].h) % 1f;
            string at = $"{palette} {family} type {i}";
            Assert.That(Mathf.Min(dh, 1f - dh), Is.LessThan(Eps), at + " hue");
            Assert.That(s, Is.EqualTo(source[i].s).Within(Eps), at + " saturation");
            Assert.That(b, Is.EqualTo(legacyB).Within(Eps), at + " brightness");
        }
    }

    private static SerializedObject Load(string path)
    {
        var asset = AssetDatabase.LoadMainAssetAtPath(path);
        Assert.That(asset, Is.Not.Null, path);
        return new SerializedObject(asset);
    }
}
```

- [ ] **Step 2: Run to verify it fails.** `$T/benchtest.sh PalettePresetTests`. Expected: failures — palette assets do not exist (`TenPalettes` finds 0; the source-matching cases fail on `Load`).

- [ ] **Step 3: Interface + palette class.**

`11.0/src/params/IAgentColorParams.cs` (+ `.meta`):
```csharp
namespace Biomes
{
    /// <summary>
    /// An agent sim's param set (Physarum / Boid / Termite): per-type hue, saturation and
    /// brightness, plus the family that palette swatches are tagged with.
    /// </summary>
    public interface IAgentColorParams : IParamSet
    {
        AgentFamily Family { get; }
    }

    public static class AgentColorParamsExtensions
    {
        public static (float h, float s, float b) GetHsb(this IParamSet p, int type) =>
            (p.GetValue("hue", type), p.GetValue("saturation", type), p.GetValue("brightness", type));

        public static void SetHsb(this IParamSet p, int type, (float h, float s, float b) c)
        {
            p.SetValue("hue", type, c.h);
            p.SetValue("saturation", type, c.s);
            p.SetValue("brightness", type, c.b);
        }
    }
}
```

`11.0/src/params/AgentColorPalette.cs` (+ `.meta`):
```csharp
using System.Collections.Generic;
using UnityEngine;

namespace Biomes
{
    /// <summary>
    /// Agent colors to assign to sim types. A swatch is the rendered color at full trail intensity;
    /// one tagged with a family is meant for that sim (assignment rule: <see cref="PaletteAssign"/>).
    /// Applied from the agent params inspector, or cycled live by SimulationManager.
    /// </summary>
    [CreateAssetMenu(fileName = "Palette", menuName = "Biomes/Agent Color Palette")]
    public class AgentColorPalette : ScriptableObject
    {
        [TextArea] public string notes;
        public List<PaletteSwatch> swatches = new();

        /// <summary>The hue/saturation/brightness that type <paramref name="typeIndex"/> of a
        /// <paramref name="family"/> sim gets; a grey or black swatch keeps the fallbacks. False when
        /// the palette is empty.</summary>
        public bool TryGetTarget(AgentFamily family, int typeIndex, float fallbackHue, float fallbackSat,
            out (float h, float s, float b) hsb)
        {
            int k = PaletteAssign.SwatchIndex(swatches, family, typeIndex);
            hsb = k < 0 ? default : AgentColor.FromRgb(swatches[k].color, fallbackHue, fallbackSat);
            return k >= 0;
        }

        /// <summary>Writes this palette into every type of <paramref name="p"/>.</summary>
        public void ApplyTo(IAgentColorParams p)
        {
            for (int i = 0; i < p.TypeCount; i++)
            {
                var current = p.GetHsb(i);
                if (TryGetTarget(p.Family, i, current.h, current.s, out var hsb))
                    p.SetHsb(i, hsb);
            }
        }

        /// <summary>Replaces this palette's swatches for <paramref name="p"/>'s family with its current colors.</summary>
        public void StoreFrom(IAgentColorParams p)
        {
            var colors = new Color[p.TypeCount];
            for (int i = 0; i < colors.Length; i++)
            {
                var (h, s, b) = p.GetHsb(i);
                colors[i] = AgentColor.ToRgb(h, s, b);
            }
            PaletteAssign.StoreFamily(swatches, p.Family, colors);
        }
    }
}
```

Params classes — change `: ScriptableObject, IParamSet` to `: ScriptableObject, IAgentColorParams` and add below `TypeCount`:
```csharp
        public AgentFamily Family => AgentFamily.Physarum;   // Boid: AgentFamily.Boid, Termite: AgentFamily.Termite
```

- [ ] **Step 4: Author the presets.** Write `$T/gen_palettes.py` (job scratch, not committed) and run it once:

```python
#!/usr/bin/env python3
"""Author AgentColorPalette presets (+ .meta) from the exhibited scenes' param assets."""
import math, os, re, uuid

W = "/Users/toka/Developer/Graphics/EoC-biomes-compute/Assets/Workspace"
OUT = W + "/11.0 Biomes/assets/Palettes"
SCRIPT_GUID = re.search(r"guid: (\w+)", open(W + "/11.0 Biomes/src/params/AgentColorPalette.cs.meta").read()).group(1)
ANY, PHYSARUM, BOID, TERMITE = 0, 1, 2, 3
LEGACY_B = {PHYSARUM: 0.8, BOID: 0.8, TERMITE: 1.0}

def hsb2rgb(h, s, b):   # computes/includes/color.hlsl
    out = []
    for o in (0.0, 4.0, 2.0):
        p = min(1.0, max(0.0, abs(math.fmod(h * 6.0 + o, 6.0) - 3.0) - 1.0))
        out.append(b * (1.0 - s + s * p * p * (3.0 - 2.0 * p)))
    return out

def agent_types(path):   # 11.x params: [(hue, saturation)] in type order
    t = open(path).read()
    return list(zip(map(float, re.findall(r"\n    hue: ([-\d.e]+)", t)),
                    map(float, re.findall(r"\n    saturation: ([-\d.e]+)", t))))

def metaesthetica_types(path):   # 10.0 Metaesthetica.PhysarumParams: Vector4 m_Hues / m_Saturations
    t = open(path).read()
    vec = lambda k: list(map(float, re.search(k + r": \{x: ([-\d.e]+), y: ([-\d.e]+), z: ([-\d.e]+), w: ([-\d.e]+)\}", t).groups()))
    return list(zip(vec("m_Hues"), vec("m_Saturations")))

def tagged(family, types):
    return [(hsb2rgb(h, s, LEGACY_B[family]), family) for h, s in types]

def hexes(*codes):
    return [([int(c[i:i + 2], 16) / 255.0 for i in (1, 3, 5)], ANY) for c in codes]

FOLDER_META = "folderAsset: yes\nDefaultImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n"
ASSET_META = "NativeFormatImporter:\n  externalObjects: {}\n  mainObjectFileID: 11400000\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n"

def meta(path, body):
    if not os.path.exists(path + ".meta"):   # re-runs keep the minted GUIDs
        open(path + ".meta", "w").write("fileFormatVersion: 2\nguid: %s\n%s" % (uuid.uuid4().hex, body))

def palette(sub, name, notes, swatches):
    folder = os.path.join(OUT, sub)
    for d in (OUT, folder):
        os.makedirs(d, exist_ok=True)
        meta(d, FOLDER_META)
    num = lambda x: "%.9g" % x
    lines = ["%YAML 1.1", "%TAG !u! tag:unity3d.com,2011:", "--- !u!114 &11400000", "MonoBehaviour:",
             "  m_ObjectHideFlags: 0", "  m_CorrespondingSourceObject: {fileID: 0}", "  m_PrefabInstance: {fileID: 0}",
             "  m_PrefabAsset: {fileID: 0}", "  m_GameObject: {fileID: 0}", "  m_Enabled: 1", "  m_EditorHideFlags: 0",
             "  m_Script: {fileID: 11500000, guid: %s, type: 3}" % SCRIPT_GUID, "  m_Name: %s" % name,
             "  m_EditorClassIdentifier: Assembly-CSharp::Biomes.AgentColorPalette",
             '  notes: "%s"' % notes.replace('"', "'"), "  swatches:"]
    for (r, g, b), fam in swatches:
        lines += ["  - color: {r: %s, g: %s, b: %s, a: 1}" % (num(r), num(g), num(b)), "    family: %d" % fam]
    path = os.path.join(folder, name + ".asset")
    open(path, "w").write("\n".join(lines) + "\n")
    meta(path, ASSET_META)

C, S, B = W + "/11.1 CURRENTS Scene/assets/", W + "/11.2 SIGGRAPH Scene/assets/", W + "/11.3 Brave New Work Scene/assets/"
palette("Shows", "Metaesthetica VISAP",
        "Metaesthetica at VISAP (10.0 Scene_10.0). Physarum: 10.0 Metaesthetica/assets/ParamsPhysarum_VISAP. Boids were grey (s 0) and are not carried over.",
        tagged(PHYSARUM, metaesthetica_types(W + "/10.0 Metaesthetica/assets/ParamsPhysarum_VISAP.asset")))
palette("Shows", "CURRENTS",
        "CURRENTS show (11.1 Scene_CURRENTS). Physarum: assets/PhysarumParams. Boid: assets/BoidParams. Termite: assets/TermiteParams.",
        tagged(PHYSARUM, agent_types(C + "PhysarumParams.asset")) + tagged(BOID, agent_types(C + "BoidParams.asset"))
        + tagged(TERMITE, agent_types(C + "TermiteParams.asset")))
palette("Shows", "SIGGRAPH",
        "SIGGRAPH show (11.2 Scene_SIGGRAPH). Physarum: Snapshots/Physarum_20260711_201424_SHOWVERSION 1. Boid: Snapshots/Boid_20260711_200244. Termite: Snapshots/Termite_20260711_200244.",
        tagged(PHYSARUM, agent_types(S + "Snapshots/Physarum_20260711_201424_SHOWVERSION 1.asset"))
        + tagged(BOID, agent_types(S + "Snapshots/Boid_20260711_200244.asset"))
        + tagged(TERMITE, agent_types(S + "Snapshots/Termite_20260711_200244.asset")))
palette("Shows", "SIGGRAPH DAC",
        "SIGGRAPH DAC take (11.2 Scene_SIGGRAPH DAC). Physarum/Boid/Termite: DAC_params/*_20260908_181119_DAC.",
        tagged(PHYSARUM, agent_types(S + "DAC_params/Physarum_20260908_181119_DAC.asset"))
        + tagged(BOID, agent_types(S + "DAC_params/Boid_20260908_181119_DAC.asset"))
        + tagged(TERMITE, agent_types(S + "DAC_params/Termite_20260908_181119_DAC.asset")))
palette("Shows", "Brave New Work",
        "Brave New Work (11.3 Scene_BraveNewWork). Physarum/Boid/Termite: 11.3 assets/*Params (Boid and Termite equal CURRENTS).",
        tagged(PHYSARUM, agent_types(B + "PhysarumParams.asset")) + tagged(BOID, agent_types(B + "BoidParams.asset"))
        + tagged(TERMITE, agent_types(B + "TermiteParams.asset")))
palette("Curated", "Bioluminescent", "Curated: deep-sea teal, cyan, blue and violet.",
        hexes("#00F5D4", "#00BBF9", "#4361EE", "#7209B7", "#9BF6FF", "#06D6A0"))
palette("Curated", "Ember", "Curated: amber to deep red.",
        hexes("#FFBA08", "#F48C06", "#E85D04", "#DC2F02", "#9D0208"))
palette("Curated", "Lichen", "Curated: pale sage, moss and ochre.",
        hexes("#DAD7CD", "#A3B18A", "#588157", "#3A5A40", "#C9A227", "#8A6F3E"))
palette("Curated", "Monochrome", "Curated: white to grey. Types keep their hue (grey has none).",
        hexes("#FFFFFF", "#D9D9D9", "#B3B3B3", "#8C8C8C"))
palette("Curated", "Viridis", "Curated: matplotlib viridis in 8 steps, dark purple to yellow.",
        hexes("#440154", "#46327E", "#365C8D", "#277F8E", "#1FA187", "#4AC16D", "#A0DA39", "#FDE725"))
print("ok")
```

Run: `python3 $T/gen_palettes.py` → `ok`. Check: `find "Assets/Workspace/11.0 Biomes/assets/Palettes" -name '*.asset' | wc -l` → 10, every `.asset` and folder has a `.meta`.

- [ ] **Step 5: Run to verify it passes.** `$T/benchtest.sh`. Expected: `failed=0`, `PalettePresetTests` 14 cases pass.

- [ ] **Step 6: Commit.**

```bash
git add -A "Assets/Workspace/11.0 Biomes/src/params" "Assets/Workspace/11.0 Biomes/assets/Palettes" "Assets/Workspace/11.0 Biomes/assets/Palettes.meta" Assets/Tests/EditMode
git commit -m "feat(colors): AgentColorPalette assets, IAgentColorParams, show + curated presets"
```

---

### Task 6: Inspector "Colors" section; Randomize Colors sets brightness

**Files:**
- Create: `11.0/src/Editor/AgentColorsGUI.cs` (+ `.meta`)
- Modify: `11.0/src/Editor/ParamsEditor.cs`
- Modify: `11.0/src/params/ColorPalette.cs` (`GenerateHS` → `GenerateHSB`)
- Modify: `PhysarumParams.cs`, `BoidParams.cs`, `TermiteParams.cs`, `CyclicCAParams.cs`, `LookupCAParams.cs` (`RandomizeColors`)

**Interfaces:**
- Consumes: `AgentColor`, `PaletteAssign`, `IAgentColorParams`, `GetHsb`/`SetHsb`, `AgentColorPalette.ApplyTo/StoreFrom` (Tasks 1–5).
- Produces: `ColorPalette.GenerateHSB(int count, float lightnessMin = 35f, float lightnessMax = 80f, float hueMinDeg = 0f, float hueMaxDeg = 360f, int candidateCount = 2000) → List<(float h, float s, float b)>`;
  `ParamsEditorGUI.DrawInspector(Editor, IAgentColorParams)`.

- [ ] **Step 1: `GenerateHSB`.** In `ColorPalette.cs` replace `GenerateHS` (summary + method) with:

```csharp
        /// <summary>
        /// Generate a palette of n perceptually distinct colors as agent hue / saturation /
        /// brightness. Converted with AgentColor.FromRgb, so each type renders as the generated
        /// Lab color, lightness included.
        /// </summary>
        public static List<(float h, float s, float b)> GenerateHSB(int count,
            float lightnessMin = 35f, float lightnessMax = 80f,
            float hueMinDeg = 0f, float hueMaxDeg = 360f,
            int candidateCount = 2000)
        {
            var labPalette = GenerateLab(count, lightnessMin, lightnessMax,
                hueMinDeg, hueMaxDeg, candidateCount);

            var result = new List<(float h, float s, float b)>();
            foreach (var lab in labPalette)
                result.Add(AgentColor.FromRgb(LabToRGB(lab)));
            return result;
        }
```

Agent params `RandomizeColors` (all three):
```csharp
        public void RandomizeColors()
        {
            var palette = ColorPalette.GenerateHSB(types.Count);
            for (int i = 0; i < types.Count && i < palette.Count; i++)
                this.SetHsb(i, palette[i]);
        }
```
CA params `RandomizeColors` (both; brightness stays theirs):
```csharp
        public void RandomizeColors()
        {
            var palette = ColorPalette.GenerateHSB(1);
            if (palette.Count > 0)
            {
                hue = palette[0].h;
                saturation = palette[0].s;
            }
        }
```

- [ ] **Step 2: `AgentColorsGUI.cs`** (+ `.meta`):

```csharp
using System;
using UnityEditor;
using UnityEngine;

namespace Biomes
{
    /// <summary>
    /// "Colors" section of the agent params inspector: a color field per type that shows (and
    /// takes) the exact rendered color, H/S/B sliders, palette apply / per-type pick, and saving
    /// the current colors into a palette.
    /// </summary>
    internal static class AgentColorsGUI
    {
        private const string PalettePrefKey = "Biomes.AgentColors.PaletteGuid";
        private const string PaletteFolder = "Assets/Workspace/11.0 Biomes/assets/Palettes";

        // One selection for every agent params inspector: pick a show once, apply it sim by sim.
        private static AgentColorPalette SelectedPalette
        {
            get
            {
                var path = AssetDatabase.GUIDToAssetPath(EditorPrefs.GetString(PalettePrefKey, ""));
                return string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<AgentColorPalette>(path);
            }
            set => EditorPrefs.SetString(PalettePrefKey,
                value != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out string guid, out long _) ? guid : "");
        }

        public static void Draw(Editor editor, IAgentColorParams p)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Colors", EditorStyles.boldLabel);
            var palette = DrawPaletteRow(editor.target, p);
            for (int i = 0; i < p.TypeCount; i++)
                DrawTypeRow(editor, p, i, palette);
            editor.serializedObject.Update();   // rows write the object directly; refresh before the types list draws
        }

        private static AgentColorPalette DrawPaletteRow(UnityEngine.Object target, IAgentColorParams p)
        {
            EditorGUI.BeginChangeCheck();
            var palette = (AgentColorPalette)EditorGUILayout.ObjectField("Palette", SelectedPalette, typeof(AgentColorPalette), false);
            if (EditorGUI.EndChangeCheck()) SelectedPalette = palette;

            if (palette != null)
            {
                DrawSwatchStrip(palette, p.Family);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button($"Apply to {p.TypeCount} types"))
                    {
                        Undo.RecordObject(target, "Apply Palette");
                        palette.ApplyTo(p);
                        EditorUtility.SetDirty(target);
                    }
                    if (GUILayout.Button(new GUIContent("Store in palette", $"Replace the palette's {p.Family} swatches with these colors")))
                    {
                        Undo.RecordObject(palette, "Store in Palette");
                        palette.StoreFrom(p);
                        EditorUtility.SetDirty(palette);
                    }
                }
            }
            if (GUILayout.Button("Save as new palette…"))
                SaveAsNewPalette(target, p);
            return palette;
        }

        // Tall swatches are the ones Apply draws from for this sim; short ones belong to other sims.
        private static void DrawSwatchStrip(AgentColorPalette palette, AgentFamily family)
        {
            int n = palette.swatches.Count;
            if (n == 0) return;
            var rect = EditorGUI.IndentedRect(EditorGUILayout.GetControlRect(false, 18f));
            float w = rect.width / n;
            for (int i = 0; i < n; i++)
            {
                float h = PaletteAssign.IsCandidate(palette.swatches, family, i) ? rect.height : 6f;
                EditorGUI.DrawRect(new Rect(rect.x + i * w, rect.yMax - h, w - 1f, h), palette.swatches[i].color);
            }
        }

        private static void DrawTypeRow(Editor editor, IAgentColorParams p, int i, AgentColorPalette palette)
        {
            var target = editor.target;
            var (h, s, b) = p.GetHsb(i);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                var picked = EditorGUILayout.ColorField(new GUIContent($"Type {i}"), AgentColor.ToRgb(h, s, b),
                    showEyedropper: true, showAlpha: false, hdr: false);
                if (EditorGUI.EndChangeCheck())
                    Write(target, p, i, AgentColor.FromRgb(picked, h, s));

                using (new EditorGUI.DisabledScope(palette == null || palette.swatches.Count == 0))
                {
                    var content = new GUIContent("▾", "Pick a palette color for this type");
                    var rect = GUILayoutUtility.GetRect(content, EditorStyles.miniButton, GUILayout.Width(22f));
                    if (GUI.Button(rect, content, EditorStyles.miniButton))
                        PopupWindow.Show(rect, new SwatchPopup(palette, p.Family, color =>
                        {
                            Write(target, p, i, AgentColor.FromRgb(color, h, s));
                            editor.Repaint();
                        }));
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                float labelWidth = EditorGUIUtility.labelWidth;
                EditorGUIUtility.labelWidth = 14f;
                EditorGUI.BeginChangeCheck();
                float nh = EditorGUILayout.Slider("H", h, 0f, 1f);
                float ns = EditorGUILayout.Slider("S", s, 0f, 1f);
                float nb = EditorGUILayout.Slider("B", b, 0f, 1f);
                if (EditorGUI.EndChangeCheck())
                    Write(target, p, i, (nh, ns, nb));
                EditorGUIUtility.labelWidth = labelWidth;
            }
        }

        private static void Write(UnityEngine.Object target, IAgentColorParams p, int i, (float h, float s, float b) c)
        {
            Undo.RecordObject(target, "Edit Agent Color");
            p.SetHsb(i, c);
            EditorUtility.SetDirty(target);
        }

        private static void SaveAsNewPalette(UnityEngine.Object target, IAgentColorParams p)
        {
            string source = target.name.Replace("(Clone)", "").Trim();
            string path = EditorUtility.SaveFilePanelInProject("Save Agent Color Palette", $"{source} Palette", "asset",
                "Save these type colors as a palette", PaletteFolder);
            if (!string.IsNullOrEmpty(path))
            {
                var palette = ScriptableObject.CreateInstance<AgentColorPalette>();
                palette.notes = $"Saved from {source} on {DateTime.Now:yyyy-MM-dd}.";
                palette.StoreFrom(p);
                AssetDatabase.CreateAsset(palette, path);
                AssetDatabase.SaveAssets();
                SelectedPalette = palette;
            }
            GUIUtility.ExitGUI();   // the save panel ran a nested event loop; end this layout pass
        }

        private sealed class SwatchPopup : PopupWindowContent
        {
            private const float Cell = 26f, Pad = 4f;
            private const int Columns = 8;
            private readonly AgentColorPalette _palette;
            private readonly AgentFamily _family;
            private readonly Action<Color> _pick;

            public SwatchPopup(AgentColorPalette palette, AgentFamily family, Action<Color> pick)
            {
                _palette = palette; _family = family; _pick = pick;
            }

            public override Vector2 GetWindowSize()
            {
                int n = _palette.swatches.Count;
                return new Vector2(Mathf.Min(n, Columns) * Cell + 2f * Pad, ((n + Columns - 1) / Columns) * Cell + 2f * Pad);
            }

            public override void OnGUI(Rect rect)
            {
                var swatches = _palette.swatches;
                for (int i = 0; i < swatches.Count; i++)
                {
                    var r = new Rect(Pad + (i % Columns) * Cell, Pad + (i / Columns) * Cell, Cell - 2f, Cell - 2f);
                    EditorGUI.DrawRect(r, swatches[i].color);
                    if (!PaletteAssign.IsCandidate(swatches, _family, i))   // tagged for another sim
                        EditorGUI.DrawRect(new Rect(r.x, r.yMax - 4f, r.width, 4f), new Color(0f, 0f, 0f, 0.6f));
                    string tip = "#" + ColorUtility.ToHtmlStringRGB(swatches[i].color)
                               + (swatches[i].family == AgentFamily.Any ? "" : " · " + swatches[i].family);
                    if (GUI.Button(r, new GUIContent("", tip), GUIStyle.none))
                    {
                        _pick(swatches[i].color);
                        editorWindow.Close();
                    }
                }
            }
        }
    }
}
```

- [ ] **Step 3: `ParamsEditor.cs`.**
  - `DrawInspector(Editor editor, IParamSet p)` → `DrawInspector(Editor editor, IAgentColorParams p)`; the three `[CustomEditor]` classes cast `(IAgentColorParams)target`.
  - Right after the `typeCount` change-check block, before `PropertyField(types)`: `AgentColorsGUI.Draw(editor, p);`
  - Delete `DrawColorSwatches` and its call (the per-type color fields replace the fixed-0.85 preview).
  - Rename `ApplyPalette` → `RandomizeColorsWithSettings` (the word "palette" now means the asset):

```csharp
        private static void RandomizeColorsWithSettings(IParamSet p)
        {
            var colors = ColorPalette.GenerateHSB(p.TypeCount,
                PaletteEditorState.lightnessMin, PaletteEditorState.lightnessMax,
                PaletteEditorState.hueMin, PaletteEditorState.hueMax);
            for (int i = 0; i < p.TypeCount && i < colors.Count; i++)
                p.SetHsb(i, colors[i]);
        }
```
  - Foldout label `"Palette Generation Settings"` → `"Randomize Colors Settings"`; summary of `PaletteEditorState` → "Shared Randomize Colors (Colorgorical) settings for the param editors."
  - Class summary: "Draws typeCount (with type-list sync), the Colors section, the types/ranges arrays, and the edit-mode tool buttons."

- [ ] **Step 4: Verify compile + suite.** `$T/benchtest.sh` → `failed=0`; `grep -n "error CS" $T/benchtest.log` → nothing.

- [ ] **Step 5: Commit.**

```bash
git add -A "Assets/Workspace/11.0 Biomes/src/Editor" "Assets/Workspace/11.0 Biomes/src/params"
git commit -m "feat(colors): params inspector Colors section (exact color fields, HSB sliders, palette apply/pick/save); randomize sets brightness"
```

---

### Task 7: Live palette cycling (`AgentPaletteCycler`, manager, MFT, OSC)

**Files:**
- Create: `11.0/src/components/utils/AgentPaletteCycler.cs` (+ `.meta`)
- Modify: `11.0/src/components/core/SimulationManager.cs` (field after `simulations`; palette buttons after the Start/Stop block; `LateUpdate`; `ConfigureAndReset`)
- Modify: `11.0/src/components/network/MidiFighterTwister.cs` (enum + cases)
- Modify: `11.0/src/components/network/OSCMapping.cs` (addresses after the per-family resets)
- Modify: `11.0/MIDI_OSC.md` (OSC table, side-button actions)

**Interfaces:**
- Consumes: `PaletteAssign.StepCycle`, `AgentColorPalette.TryGetTarget`, `IAgentColorParams`, `GetHsb`/`SetHsb`, `ParameterInterpolator.LerpHue01`, `SimulationBase.LiveParamSet` / `PresetParamSet` / `runState`.
- Produces: `[Serializable] AgentPaletteCycler { List<AgentColorPalette> palettes; float fadeSeconds; int ActiveIndex; string ActiveName; void Select(int, IReadOnlyList<SimulationBase>); void Tick(float); void Reimpose(SimulationBase); }`;
  `SimulationManager.paletteCycle`, `NextPalette()`, `PreviousPalette()`, `SelectPalette(int)`;
  `SideButtonAction.NextPalette`, `SideButtonAction.PreviousPalette`.

- [ ] **Step 1: `AgentPaletteCycler.cs`** (+ `.meta`):

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Biomes
{
    /// <summary>
    /// Live palette cycling for the agent sims a SimulationManager runs. The cycle is Preset (each
    /// sim's authored colors) then <see cref="palettes"/>; a change fades every type's hue (shortest
    /// arc), saturation and brightness over <see cref="fadeSeconds"/>. The active palette outlives
    /// resets: the manager calls <see cref="Reimpose"/> on each freshly reset sim.
    /// </summary>
    [Serializable]
    public class AgentPaletteCycler
    {
        [Tooltip("Palettes in cycle order. Next/Previous wrap through Preset (the authored colors).")]
        public List<AgentColorPalette> palettes = new();
        [Tooltip("Seconds a palette change fades over (unscaled time). 0 = instant.")]
        [Min(0f)] public float fadeSeconds = 2f;

        [NonSerialized] private int _active = -1;        // −1 = Preset; every Play starts there
        [NonSerialized] private float _progress = 1f;    // 1 = settled
        // Each faded sim's per-type colors when the current change began.
        private readonly Dictionary<SimulationBase, (float h, float s, float b)[]> _from = new();

        public int ActiveIndex => _active;
        public string ActiveName => _active < 0 ? "Preset" : PaletteAt(_active) != null ? PaletteAt(_active).name : "(missing palette)";

        /// <summary>Starts a change to palette <paramref name="index"/> (−1 = Preset; clamped) on
        /// every started agent sim. Edit mode and fadeSeconds 0 apply at once.</summary>
        public void Select(int index, IReadOnlyList<SimulationBase> sims)
        {
            _active = Mathf.Clamp(index, -1, palettes.Count - 1);
            _from.Clear();
            foreach (var sim in sims)
            {
                if (sim == null || sim.runState == SimRunState.Stopped || sim.LiveParamSet is not IAgentColorParams p) continue;
                var from = new (float h, float s, float b)[p.TypeCount];
                for (int i = 0; i < from.Length; i++) from[i] = p.GetHsb(i);
                _from[sim] = from;
            }
            _progress = fadeSeconds > 0f && Application.isPlaying ? 0f : 1f;
            foreach (var sim in _from.Keys) Write(sim);
        }

        /// <summary>Advances the fade; call once per rendered frame.</summary>
        public void Tick(float deltaTime)
        {
            if (_progress >= 1f) return;
            _progress = fadeSeconds > 0f ? Mathf.Min(1f, _progress + deltaTime / fadeSeconds) : 1f;
            foreach (var sim in _from.Keys) Write(sim);
        }

        /// <summary>Puts the active palette (at the fade's current point) on a sim that was just
        /// reset or started, whose fresh params carry the preset's colors.</summary>
        public void Reimpose(SimulationBase sim)
        {
            if (_active < 0 && _progress >= 1f) return;   // settled on Preset: the reset already restored it
            Write(sim);
        }

        private AgentColorPalette PaletteAt(int i) => i >= 0 && i < palettes.Count ? palettes[i] : null;

        private void Write(SimulationBase sim)
        {
            if (sim == null || sim.LiveParamSet is not IAgentColorParams p) return;
            _from.TryGetValue(sim, out var from);
            float t = Mathf.SmoothStep(0f, 1f, _progress);
            for (int i = 0; i < p.TypeCount; i++)
            {
                // A sim that was not running when the change began has nothing to fade from: it jumps.
                bool fades = from != null && i < from.Length;
                var a = fades ? from[i] : p.GetHsb(i);
                if (TryTarget(sim, p, i, a, out var to))
                    p.SetHsb(i, fades ? Lerp(a, to, t) : to);
            }
        }

        private bool TryTarget(SimulationBase sim, IAgentColorParams p, int type, (float h, float s, float b) current,
            out (float h, float s, float b) target)
        {
            target = default;
            if (_active < 0)
            {
                if (sim.PresetParamSet is not IParamSet preset || type >= preset.TypeCount) return false;
                target = preset.GetHsb(type);
                return true;
            }
            var palette = PaletteAt(_active);
            return palette != null && palette.TryGetTarget(p.Family, type, current.h, current.s, out target);
        }

        private static (float h, float s, float b) Lerp((float h, float s, float b) a, (float h, float s, float b) b, float t) =>
            (ParameterInterpolator.LerpHue01(a.h, b.h, t), Mathf.Lerp(a.s, b.s, t), Mathf.Lerp(a.b, b.b, t));
    }
}
```

- [ ] **Step 2: `SimulationManager.cs`.**

After `public List<SimulationBase> simulations = new();`:
```csharp

        [Header("Agent Palettes")]
        [Tooltip("Live agent-color palettes: Next/Previous Palette (buttons, MFT side actions, OSC /palette_*) fade every running agent sim; the active palette survives resets.")]
        public AgentPaletteCycler paletteCycle = new();
```
`LateUpdate` (keep its comment):
```csharp
        void LateUpdate()
        {
            paletteCycle.Tick(Time.unscaledDeltaTime);
            Render();
        }
```
After the `StopSimsOfType<T>` method:
```csharp

        // ── Agent palettes ───────────────────────────────────────────────────────

        [Button("Next Palette")]     public void NextPalette()     => StepPalette(+1);
        [Button("Previous Palette")] public void PreviousPalette() => StepPalette(-1);

        /// <summary>Fade every running agent sim to palette <paramref name="index"/> of
        /// <see cref="paletteCycle"/> (−1 = Preset, the authored colors). Public for MIDI/OSC.</summary>
        public void SelectPalette(int index)
        {
            paletteCycle.Select(index, simulations);
            Debug.Log($"[SimulationManager] Palette → {paletteCycle.ActiveName}");
        }

        private void StepPalette(int delta) =>
            SelectPalette(PaletteAssign.StepCycle(paletteCycle.ActiveIndex, paletteCycle.palettes.Count, delta));
```
In `ConfigureAndReset`, replace the final `sim.Reset();` with:
```csharp
            sim.Reset();
            // The fresh params clone carries the preset's colors; put the live palette back.
            paletteCycle.Reimpose(sim);
```

- [ ] **Step 3: MFT.** Append to `SideButtonAction` after `SaveToCurrentParams,`:
```csharp
            NextPalette,
            PreviousPalette,
```
In the action switch, after the `RandomizeAll` case:
```csharp
                case SideButtonAction.NextPalette:
                    m_SimManager?.NextPalette();
                    break;

                case SideButtonAction.PreviousPalette:
                    m_SimManager?.PreviousPalette();
                    break;
```

- [ ] **Step 4: OSC.** In `OSCMapping.Start`, after the `/sim_resetTermites` registration:
```csharp

            // Agent palettes (SimulationManager.paletteCycle) — main thread: the fade ticks there.
            On(
                "/palette_next",
                (string address, OscDataHandle data) => {
                    m_MainThreadActions.Enqueue(() => m_SimulationManager.NextPalette());
                }
            );
            On(
                "/palette_prev",
                (string address, OscDataHandle data) => {
                    m_MainThreadActions.Enqueue(() => m_SimulationManager.PreviousPalette());
                }
            );
            // /palette <index>: select directly (−1 = Preset, the authored colors).
            On(
                "/palette",
                (string address, OscDataHandle data) => {
                    int index = data.GetElementAsInt(0);   // read now: the handle is only valid in this callback
                    m_MainThreadActions.Enqueue(() => m_SimulationManager.SelectPalette(index));
                }
            );
```
Update the comment at the top of `OSCMapping` ("Reset()/ResetSimsOnly() call Unity GPU…") only if it lists queued actions explicitly — it does not, leave it.

- [ ] **Step 5: `MIDI_OSC.md`.** After the reset-commands table add:

```markdown
Agent palettes (`SimulationManager` → **Agent Palettes**: fill `Palettes`, set `Fade Seconds`; marshalled to the main thread):

| Address | Args | Effect |
| ------- | ---- | ------ |
| `/palette_next` | — | fade to the next palette in the cycle (Preset → palettes → Preset …) |
| `/palette_prev` | — | fade to the previous one |
| `/palette` | 1 int | select directly; −1 = Preset (authored colors), out of range clamps |

The active palette survives every reset path; per-type colors stay live on `/<p>_hue_<i>`, `/<p>_saturation_<i>`, `/<p>_brightness_<i>`.
```
and in "Other assignable actions" append: "NextPalette, PreviousPalette (step the `SimulationManager` palette cycle; e.g. put NextPalette on R3 in place of RandomizeColors)."

- [ ] **Step 6: Verify.** `$T/benchtest.sh` → `failed=0`, no `error CS`. Behaviour is verified live in Task 8.

- [ ] **Step 7: Commit.**

```bash
git add "Assets/Workspace/11.0 Biomes/src/components" "Assets/Workspace/11.0 Biomes/MIDI_OSC.md"
git commit -m "feat(colors): live palette cycling with fades (SimulationManager, MFT side actions, OSC /palette_*), survives resets"
```

---

### Task 8: Live verification in the bench Editor + docs

**Files:**
- Scratch only: `$T/probe_palette_*.cs` (not committed)
- Modify: `README.md`, `docs/ARCHITECTURE.md`, `docs/ROADMAP.md`, `docs/INDEX.md`, spec status line
- Create: `docs/sessions/2026-10-04-agent-color-palettes.md`

- [ ] **Step 1: Launch the bench Editor** (headless, Pipeline package already in its manifest):

```bash
$T/sync.sh
/Applications/Unity/Hub/Editor/6000.3.10f1/Unity.app/Contents/MacOS/Unity -batchmode -projectPath "$T/bench/EoC-biomes-compute" -logFile "$T/bench/editor.log" > "$T/bench/editor.stdout" 2>&1 &
until ~/.unity/bin/unity status --no-banner 2>/dev/null | grep -q "bench/EoC-biomes-compute"; do sleep 5; done
```
Then the Metal gate: `$T/ue.sh $T/shader_gate.cs` → `errors 0 | warnings 0` for all 10 computes.

- [ ] **Step 2: Probe — enter Play in TestScene.** `$T/probe_palette_play.cs`:

```csharp
UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Workspace/11.0 Biomes/TestScene.unity");
UnityEditor.EditorApplication.EnterPlaymode();
return "entering";
```
Poll `$T/ue.sh $T/st.cs`-style status until `playing=True` and `SimStepCount > 60`.

- [ ] **Step 3: Probe — cycle, fade, reset, clamp.** `$T/probe_palette_check.cs` (run, then re-run its second half after waiting ≥ 2.5 s by calling it with a SessionState stage key):

```csharp
var m = UnityEngine.Object.FindFirstObjectByType<Biomes.SimulationManager>();
var phy = UnityEngine.Object.FindFirstObjectByType<Biomes.PhysarumSim>();
var stage = UnityEditor.SessionState.GetInt("palprobe", 0);
var sb = new System.Text.StringBuilder();
string Hsb(int i) { var c = Biomes.AgentColorParamsExtensions.GetHsb(phy.LiveParamSet, i); return $"({c.h:F3},{c.s:F3},{c.b:F3})"; }
if (stage == 0)
{
    var siggraph = UnityEditor.AssetDatabase.LoadAssetAtPath<Biomes.AgentColorPalette>("Assets/Workspace/11.0 Biomes/assets/Palettes/Shows/SIGGRAPH.asset");
    var mono = UnityEditor.AssetDatabase.LoadAssetAtPath<Biomes.AgentColorPalette>("Assets/Workspace/11.0 Biomes/assets/Palettes/Curated/Monochrome.asset");
    m.paletteCycle.palettes = new System.Collections.Generic.List<Biomes.AgentColorPalette> { siggraph, mono };
    m.paletteCycle.fadeSeconds = 2f;
    sb.Append("preset " + Hsb(0));
    m.NextPalette();                                   // → SIGGRAPH, fading
    sb.Append(" | t0 " + Hsb(0) + " active=" + m.paletteCycle.ActiveName);
    UnityEditor.SessionState.SetInt("palprobe", 1);
}
else if (stage == 1)
{
    sb.Append("settled " + Hsb(0));                   // expect SIGGRAPH physarum type 0: (0.000,0.810,0.800)
    m.ResetPhysarum();
    sb.Append(" | after reset " + Hsb(0));            // expect unchanged (re-imposed)
    m.SelectPalette(99);
    sb.Append(" | clamp→" + m.paletteCycle.ActiveName); // expect Monochrome
    m.paletteCycle.fadeSeconds = 0f;
    m.paletteCycle.Tick(0f);
    sb.Append(" zeroFade " + Hsb(0));                 // expect finite, s 0, hue kept
    m.paletteCycle.palettes.RemoveAt(1);
    sb.Append(" | shrunk→" + m.paletteCycle.ActiveName); // expect "(missing palette)", no exception
    m.paletteCycle.fadeSeconds = 2f;
    m.SelectPalette(-1);                               // fade from white back to the preset …
    m.ResetPhysarum();                                 // … reset at t = 0: the fade's value, not the preset
    sb.Append(" | reset-mid-fade " + Hsb(0));          // expect (0.000,0.000,1.000)
    UnityEditor.SessionState.SetInt("palprobe", 2);
}
else
{
    sb.Append("back to preset " + Hsb(0));            // expect the stage-0 preset value
    UnityEditor.SessionState.EraseInt("palprobe");
}
return sb.ToString();
```
Record the three outputs. Pass criteria: settled = SIGGRAPH type 0 `(0.000,0.810,0.800)` within 1e-3; after-reset equal to settled; clamp → `Monochrome`; zero-fade `(0.000,0.000,1.000)` (white keeps hue, all finite); shrunk → `(missing palette)` with no exception; reset-mid-fade `(0.000,0.000,1.000)` (the fade's start, not the preset); final = the stage-0 preset value. Any miss → fix in the owning task's file, re-run Task 7 Step 6 and this step; fixes are new commits.

- [ ] **Step 4: Stop the bench Editor.** `$T/ue.sh $T/exitplay.cs`, then `$T/ue.sh $T/quit.cs`.

- [ ] **Step 5: Docs** (invoke `eoc-docs` → `docs-log`):
  - `README.md` Concepts: new bullet **Agent colors (HSB + palettes)** — per-type brightness (legacy defaults keep looks), exact color fields in the params inspector, `AgentColorPalette` presets (`11.0 Biomes/assets/Palettes/`: Shows + Curated), live cycling from SimulationManager / MFT / OSC with fades that survive resets; spec link.
  - `docs/ARCHITECTURE.md`: params section — brightness + struct sizes; new palette subsystem paragraph (AgentColor, PaletteAssign, AgentColorPalette, AgentPaletteCycler, reset funnel hook); MFT bank 2 rows.
  - `docs/ROADMAP.md`: Shipped entry.
  - `docs/sessions/2026-10-04-agent-color-palettes.md` (frontmatter per docs-log): what shipped, measured/verified (test counts, probe outputs), decisions (HSB not HSL; append-only struct; palette on manager; keep-on-reset), open items (interpolator color toggles vs palettes; side-button mapping in show scenes).
  - `docs/INDEX.md`: session entry; add `([[superpowers/plans/2026-10-04-agent-color-palettes|plan]])` to the spec entry.
  - Spec `**Status:**` → `Implemented (branch feat/agent-color-palettes)`.

- [ ] **Step 6: Final suite + commit.** `$T/benchtest.sh` → `failed=0`. Then:

```bash
git add README.md docs
git commit -m "docs: agent color palettes session log + INDEX; README, ARCHITECTURE, ROADMAP"
```
