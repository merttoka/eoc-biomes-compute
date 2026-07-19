# Temporal Composer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Timeline-driven show sequencer for `Scene_SIGGRAPH.unity`: choreographs sim visibility, param snapshots, resets, network routing, 2–4 live biome cells (overlay/replace), and Anadol-style scattered patches of a StreamDiffusion return stream — all composited into a new `composerOutTex`.

**Architecture:** Unity Timeline (`PlayableDirector` + custom tracks) authors and executes the sequence. Track mixers push per-frame draw state into a new `CompositeSequencer` MonoBehaviour, which owns `composerOutTex` (stable RT, clear-in-place per ADR-0008) and dispatches `SequencerComposite.compute` after `SimulationManager.Render()`. Pure scheduling logic (patch generation, sweep, envelopes, sigmoid crossfade) lives in an engine-free `Biomes.Sequencer.Core` assembly so it is unit-testable without Unity playmode.

**Tech Stack:** Unity 6000.3.10f1, HDRP, compute shaders, `com.unity.timeline`, `com.unity.test-framework` (NUnit edit-mode), Klak Spout/Syphon/NDI (existing `ExternalTextureShare` wrappers), EasyButtons.

**Spec:** `docs/superpowers/specs/2026-07-19-temporal-composer-design.md`

## Global Constraints

- Unity editor: `6000.3.10f1` at `/Applications/Unity/Hub/Editor/6000.3.10f1` (mac dev; show machine is Windows/RTX 5080).
- All runtime code in namespace `Biomes` except the pure-logic assembly, which uses `Biomes.Sequencer`.
- Code style: PascalCase publics, camelCase privates with `m_`/`_` only where surrounding file already does; `[Button]` (EasyButtons) for inspector actions; XML doc comments on public API (match `SimulationManager.cs` idiom).
- `composerOutTex`: `ARGBHalf`, allocated once, cleared in place, reallocated ONLY on resolution change (ADR-0008 — senders keep native texture handle).
- Composer default rez = sim composite rez × `composerResScale` (default 1) so `ScreenLayout` pixel-crop rects stay valid.
- Zero per-frame GC alloc in `LateUpdate`/`ProcessFrame` hot paths: pooled `List`s, preallocated arrays, no LINQ, no closures.
- ≤4 cells, ≤512 patch events per clip, ≤128 active patch draws per frame.
- All sequencer runtime behavior play-mode-guarded.
- Commit after every task; concise messages, no attribution lines.
- The Unity CLI test command CANNOT run while the project is open in the editor. Prefer the Test Runner window when the editor is open (Window → General → Test Runner → EditMode → Run All).

**Deviations from spec (agreed rationale, noted for reviewers):**
1. Spec names two kernels `CellKernel`/`PatchKernel` with a 512-wide `StructuredBuffer` loop. Cells and patches are the same math (sample src sub-rect → blend into dst rect), so one `RectBlendKernel` dispatched per rect (≤4 cells + ≤128 active patches, each dispatch covers only its rect's pixels) replaces both. This is strictly cheaper than a per-pixel 512-iteration loop over the full composite and removes the buffer entirely.
2. Spec says rate caps (`targetFrameRate`, sim Hz) are `CompositeSequencer` fields. Those caps already exist on `SimulationManager` (`targetFPS`, `simRate`); duplicating them creates two sources of truth. `CompositeSequencer` exposes only `composerResScale`; cell Hz lives on each `BiomeCellRig`.

---

### Task 1: Packages + assemblies + test scaffold

**Files:**
- Modify: `Packages/manifest.json`
- Create: `Assets/Workspace/11.0 Biomes/src/sequencer_core/Biomes.Sequencer.Core.asmdef`
- Create: `Assets/Workspace/11.0 Biomes/src/sequencer_core/AssemblyInfo.cs`
- Create: `Assets/Tests/EditMode/Biomes.Sequencer.Tests.asmdef`
- Create: `Assets/Tests/EditMode/SmokeTest.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `Biomes.Sequencer.Core` assembly (engine-free — later tasks put `PatchEventScheduler` here) and `Biomes.Sequencer.Tests` edit-mode test assembly referencing it. The default `Assembly-CSharp` (all existing game code) automatically references `Biomes.Sequencer.Core` because predefined assemblies reference all asmdefs.

- [ ] **Step 1: Add timeline + test-framework packages**

In `Packages/manifest.json`, add to `"dependencies"` (alphabetical position, after `"com.unity.render-pipelines.high-definition"`):

```json
    "com.unity.test-framework": "1.4.5",
    "com.unity.timeline": "1.8.7",
```

- [ ] **Step 2: Create the pure-logic assembly**

`Assets/Workspace/11.0 Biomes/src/sequencer_core/Biomes.Sequencer.Core.asmdef`:

```json
{
    "name": "Biomes.Sequencer.Core",
    "rootNamespace": "Biomes.Sequencer",
    "references": [],
    "includePlatforms": [],
    "excludePlatforms": [],
    "allowUnsafeCode": false,
    "overrideReferences": false,
    "precompiledReferences": [],
    "autoReferenced": true,
    "defineConstraints": [],
    "versionDefines": [],
    "noEngineReferences": true
}
```

`Assets/Workspace/11.0 Biomes/src/sequencer_core/AssemblyInfo.cs`:

```csharp
// Marker file so the folder always compiles into Biomes.Sequencer.Core even before
// the first real class lands. noEngineReferences: this assembly must stay free of
// UnityEngine so its logic is unit-testable and deterministic.
```

- [ ] **Step 3: Create the edit-mode test assembly + smoke test**

`Assets/Tests/EditMode/Biomes.Sequencer.Tests.asmdef`:

```json
{
    "name": "Biomes.Sequencer.Tests",
    "rootNamespace": "Biomes.Sequencer.Tests",
    "references": [
        "UnityEngine.TestRunner",
        "UnityEditor.TestRunner",
        "Biomes.Sequencer.Core"
    ],
    "includePlatforms": [
        "Editor"
    ],
    "excludePlatforms": [],
    "allowUnsafeCode": false,
    "overrideReferences": true,
    "precompiledReferences": [
        "nunit.framework.dll"
    ],
    "autoReferenced": false,
    "defineConstraints": [
        "UNITY_INCLUDE_TESTS"
    ],
    "versionDefines": [],
    "noEngineReferences": false
}
```

`Assets/Tests/EditMode/SmokeTest.cs`:

```csharp
using NUnit.Framework;

namespace Biomes.Sequencer.Tests
{
    public class SmokeTest
    {
        [Test]
        public void TestAssemblyCompilesAndRuns() => Assert.Pass();
    }
}
```

- [ ] **Step 4: Verify — run the edit-mode tests**

With the Unity editor CLOSED:

```bash
"/Applications/Unity/Hub/Editor/6000.3.10f1/Unity.app/Contents/MacOS/Unity" \
  -batchmode -projectPath "/Users/toka/Developer/Graphics/EoC-biomes-compute" \
  -runTests -testPlatform EditMode \
  -testResults "/Users/toka/Developer/Graphics/EoC-biomes-compute/TestResults/editmode.xml" \
  -logFile -
```

Expected: exits 0; `TestResults/editmode.xml` contains `result="Passed"` for `TestAssemblyCompilesAndRuns`. (First run is slow — package import.) If the editor is open instead: Window → General → Test Runner → EditMode → Run All → 1 green test.

- [ ] **Step 5: Commit**

```bash
cd /Users/toka/Developer/Graphics/EoC-biomes-compute
git add Packages/manifest.json Packages/packages-lock.json "Assets/Workspace/11.0 Biomes/src/sequencer_core" Assets/Tests
git add -A  # pick up generated .meta files
git commit -m "sequencer: timeline+test-framework packages, core/tests asmdef scaffold"
```

---

### Task 2: PatchEventScheduler pure logic (TDD)

**Files:**
- Create: `Assets/Workspace/11.0 Biomes/src/sequencer_core/PatchEventScheduler.cs`
- Test: `Assets/Tests/EditMode/PatchEventSchedulerTests.cs`

**Interfaces:**
- Consumes: nothing (System only — assembly has `noEngineReferences`).
- Produces (used by Task 6's `PatchScatterBehaviour` and Task 7's mixer):
  - `struct PatchRect { float x, y, w, h; bool Overlaps(in PatchRect o); }` — normalized [0,1].
  - `struct PatchEvent { PatchRect dst; PatchRect src; int start; int holdEnd; int fadeEnd; float crossfadeRoll; float anchorT; }`
  - `class PatchScatterConfig { int seed; int count; float minSize; float maxSize; float aspect; int holdMinFrames; int holdMaxFrames; int fadeFrames; int leadFrames; int trailFrames; int staggerJitterFrames; float crossfadeCenter; float crossfadeWidth; int durationFrames; int maxRejects; }`
  - `static PatchEvent[] PatchEventScheduler.Generate(PatchScatterConfig cfg)`
  - `static int PatchEventScheduler.SizeToHoldFrames(float sizeNorm01, int holdMin, int holdMax)`
  - `static float PatchEventScheduler.Envelope(in PatchEvent e, int frame)` — 0..1 alpha.
  - `static float PatchEventScheduler.Sigmoid(float t, float center, float width)`
  - `class PatchSweep { PatchSweep(PatchEvent[] events); int Collect(int frame, PatchEvent[] outBuf); }` — sorted-cursor sweep, rewinds automatically on backward scrub.

- [ ] **Step 1: Write the failing tests**

`Assets/Tests/EditMode/PatchEventSchedulerTests.cs`:

```csharp
using System;
using NUnit.Framework;
using Biomes.Sequencer;

namespace Biomes.Sequencer.Tests
{
    public class PatchEventSchedulerTests
    {
        private static PatchScatterConfig Cfg(int seed = 42) => new PatchScatterConfig
        {
            seed = seed,
            count = 64,
            minSize = 0.03f,
            maxSize = 0.25f,
            aspect = 5f,
            holdMinFrames = 9,
            holdMaxFrames = 90,
            fadeFrames = 30,
            leadFrames = 150,
            trailFrames = 90,
            staggerJitterFrames = 12,
            crossfadeCenter = 0.5f,
            crossfadeWidth = 0.15f,
            durationFrames = 1800,
            maxRejects = 40,
        };

        [Test]
        public void Generate_SameSeed_IsDeterministic()
        {
            var a = PatchEventScheduler.Generate(Cfg());
            var b = PatchEventScheduler.Generate(Cfg());
            Assert.AreEqual(a.Length, b.Length);
            for (int i = 0; i < a.Length; i++)
            {
                Assert.AreEqual(a[i].start, b[i].start);
                Assert.AreEqual(a[i].dst.x, b[i].dst.x);
                Assert.AreEqual(a[i].crossfadeRoll, b[i].crossfadeRoll);
            }
        }

        [Test]
        public void Generate_DifferentSeed_Differs()
        {
            var a = PatchEventScheduler.Generate(Cfg(1));
            var b = PatchEventScheduler.Generate(Cfg(2));
            bool anyDiff = a.Length != b.Length;
            for (int i = 0; !anyDiff && i < a.Length; i++)
                anyDiff = a[i].start != b[i].start || a[i].dst.x != b[i].dst.x;
            Assert.IsTrue(anyDiff);
        }

        [Test]
        public void Generate_TimeCoactivePatches_NeverOverlapSpatially()
        {
            var events = PatchEventScheduler.Generate(Cfg());
            for (int i = 0; i < events.Length; i++)
                for (int j = i + 1; j < events.Length; j++)
                {
                    bool timeOverlap = events[i].start < events[j].fadeEnd &&
                                       events[j].start < events[i].fadeEnd;
                    if (timeOverlap)
                        Assert.IsFalse(events[i].dst.Overlaps(events[j].dst),
                            $"events {i} and {j} are co-active and overlap spatially");
                }
        }

        [Test]
        public void Generate_EventsWithinDurationAndUnitSquare()
        {
            var events = PatchEventScheduler.Generate(Cfg());
            Assert.Greater(events.Length, 0);
            foreach (var e in events)
            {
                Assert.GreaterOrEqual(e.start, 0);
                Assert.Greater(e.holdEnd, e.start);
                Assert.Greater(e.fadeEnd, e.holdEnd);
                Assert.GreaterOrEqual(e.dst.x, 0f);
                Assert.GreaterOrEqual(e.dst.y, 0f);
                Assert.LessOrEqual(e.dst.x + e.dst.w, 1f + 1e-4f);
                Assert.LessOrEqual(e.dst.y + e.dst.h, 1f + 1e-4f);
            }
        }

        [Test]
        public void SizeToHold_LargerPatch_ShorterHold()
        {
            int small = PatchEventScheduler.SizeToHoldFrames(0f, 9, 90);
            int mid = PatchEventScheduler.SizeToHoldFrames(0.5f, 9, 90);
            int large = PatchEventScheduler.SizeToHoldFrames(1f, 9, 90);
            Assert.AreEqual(90, small);
            Assert.AreEqual(9, large);
            Assert.Greater(small, mid);
            Assert.Greater(mid, large);
        }

        [Test]
        public void Envelope_HoldsAtOne_ThenFadesToZero()
        {
            var e = new PatchEvent
            {
                start = 100, holdEnd = 130, fadeEnd = 160,
                dst = new PatchRect(0, 0, 0.1f, 0.1f),
                src = new PatchRect(0, 0, 0.1f, 0.1f),
            };
            Assert.AreEqual(0f, PatchEventScheduler.Envelope(in e, 99));
            Assert.AreEqual(1f, PatchEventScheduler.Envelope(in e, 100));
            Assert.AreEqual(1f, PatchEventScheduler.Envelope(in e, 129));
            float mid = PatchEventScheduler.Envelope(in e, 145);
            Assert.Greater(mid, 0f);
            Assert.Less(mid, 1f);
            Assert.AreEqual(0f, PatchEventScheduler.Envelope(in e, 160));
            Assert.AreEqual(0f, PatchEventScheduler.Envelope(in e, 500));
        }

        [Test]
        public void Sigmoid_CenteredAndMonotonic()
        {
            Assert.AreEqual(0.5f, PatchEventScheduler.Sigmoid(0.5f, 0.5f, 0.15f), 1e-4f);
            float prev = -1f;
            for (float t = 0f; t <= 1f; t += 0.05f)
            {
                float v = PatchEventScheduler.Sigmoid(t, 0.5f, 0.15f);
                Assert.Greater(v, prev);
                prev = v;
            }
            Assert.Less(PatchEventScheduler.Sigmoid(0f, 0.5f, 0.15f), 0.05f);
            Assert.Greater(PatchEventScheduler.Sigmoid(1f, 0.5f, 0.15f), 0.95f);
        }

        [Test]
        public void Sweep_MatchesBruteForce_IncludingBackwardScrub()
        {
            var events = PatchEventScheduler.Generate(Cfg());
            var sweep = new PatchSweep(events);
            var buf = new PatchEvent[events.Length];
            // forward, backward, random jumps
            int[] frames = { 0, 200, 900, 901, 300, 1799, 50, 1800 };
            foreach (int frame in frames)
            {
                int n = sweep.Collect(frame, buf);
                int expected = 0;
                foreach (var e in events)
                    if (e.start <= frame && frame < e.fadeEnd) expected++;
                Assert.AreEqual(expected, n, $"frame {frame}");
                for (int i = 0; i < n; i++)
                    Assert.IsTrue(buf[i].start <= frame && frame < buf[i].fadeEnd);
            }
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Test Runner window → Run All (or the CLI command from Task 1 Step 4).
Expected: compile errors in the test assembly (`PatchScatterConfig` not found) — that is the failing state for a not-yet-written API. Do NOT stub the API to make compilation pass without behavior.

- [ ] **Step 3: Implement**

`Assets/Workspace/11.0 Biomes/src/sequencer_core/PatchEventScheduler.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace Biomes.Sequencer
{
    /// <summary>Axis-aligned rect in normalized [0,1] texture coords.</summary>
    public struct PatchRect
    {
        public float x, y, w, h;

        public PatchRect(float x, float y, float w, float h)
        {
            this.x = x; this.y = y; this.w = w; this.h = h;
        }

        public bool Overlaps(in PatchRect o) =>
            x < o.x + o.w && o.x < x + w && y < o.y + o.h && o.y < y + h;
    }

    /// <summary>One scheduled patch: where it lands (dst), what it samples (src),
    /// and its lifetime in clip-local frames. crossfadeRoll vs Sigmoid(anchorT)
    /// picks source A or B (per-patch stochastic dissolve).</summary>
    public struct PatchEvent
    {
        public PatchRect dst;
        public PatchRect src;
        public int start;
        public int holdEnd;
        public int fadeEnd;
        public float crossfadeRoll;
        public float anchorT;       // normalized position of this patch's anchor in the clip
    }

    /// <summary>Deterministic inputs for Generate(). One instance per PatchScatterClip.</summary>
    public class PatchScatterConfig
    {
        public int seed = 1234;
        public int count = 64;                 // requested events over the clip (rejections may yield fewer)
        public float minSize = 0.03f;          // dst height, normalized
        public float maxSize = 0.25f;
        public float aspect = 5f;              // composer W/H; dst width = height / aspect → square in pixels
        public int holdMinFrames = 9;          // large patches flash…
        public int holdMaxFrames = 90;         // …small patches linger (size→hold inversion)
        public int fadeFrames = 30;
        public int leadFrames = 150;           // patch may appear this many frames BEFORE its anchor
        public int trailFrames = 90;           // …or this many after (asymmetric spread)
        public int staggerJitterFrames = 12;
        public float crossfadeCenter = 0.5f;
        public float crossfadeWidth = 0.15f;
        public int durationFrames = 1800;
        public int maxRejects = 40;            // rejection-sampling tries per patch
    }

    /// <summary>Anadol-grammar patch scheduling, ported from SimAesthetics
    /// render_overlay_video.py. Pure C# — deterministic from (config.seed), no engine refs.</summary>
    public static class PatchEventScheduler
    {
        /// <summary>Reversed size→duration mapping: sizeNorm01=1 (largest) → holdMin
        /// (flash and vanish), sizeNorm01=0 (smallest) → holdMax (linger).</summary>
        public static int SizeToHoldFrames(float sizeNorm01, int holdMin, int holdMax)
        {
            if (sizeNorm01 < 0f) sizeNorm01 = 0f;
            if (sizeNorm01 > 1f) sizeNorm01 = 1f;
            return holdMax - (int)Math.Round((holdMax - holdMin) * (double)sizeNorm01);
        }

        /// <summary>Alpha envelope: 1 during [start, holdEnd), linear fade to 0 over
        /// [holdEnd, fadeEnd), 0 outside.</summary>
        public static float Envelope(in PatchEvent e, int frame)
        {
            if (frame < e.start || frame >= e.fadeEnd) return 0f;
            if (frame < e.holdEnd) return 1f;
            float denom = e.fadeEnd - e.holdEnd;
            return denom <= 0f ? 0f : 1f - (frame - e.holdEnd) / denom;
        }

        /// <summary>Logistic curve over normalized clip time; compare a patch's
        /// crossfadeRoll against this to pick source A (roll ≥ sigmoid) or B (roll &lt; sigmoid).</summary>
        public static float Sigmoid(float t, float center, float width)
        {
            if (width < 1e-5f) width = 1e-5f;
            return 1f / (1f + (float)Math.Exp(-(t - center) / width));
        }

        public static PatchEvent[] Generate(PatchScatterConfig cfg)
        {
            var rng = new Random(cfg.seed);
            var events = new List<PatchEvent>(cfg.count);

            for (int i = 0; i < cfg.count; i++)
            {
                // Anchor spreads patches uniformly across the clip; the actual start is
                // offset asymmetrically (lead/trail) + jitter so appearances cascade.
                float anchorT = (i + 0.5f) / cfg.count;
                int anchor = (int)(anchorT * cfg.durationFrames);

                float sizeH = Lerp(cfg.minSize, cfg.maxSize, (float)rng.NextDouble());
                float sizeNorm01 = cfg.maxSize > cfg.minSize
                    ? (sizeH - cfg.minSize) / (cfg.maxSize - cfg.minSize) : 0f;
                float sizeW = sizeH / Math.Max(0.01f, cfg.aspect);

                int hold = SizeToHoldFrames(sizeNorm01, cfg.holdMinFrames, cfg.holdMaxFrames);
                int spread = (int)(Lerp(-cfg.leadFrames, cfg.trailFrames, (float)rng.NextDouble()));
                int jitter = rng.Next(-cfg.staggerJitterFrames, cfg.staggerJitterFrames + 1);
                int start = Clamp(anchor + spread + jitter, 0, Math.Max(0, cfg.durationFrames - 1));
                int holdEnd = start + Math.Max(1, hold);
                int fadeEnd = holdEnd + Math.Max(1, cfg.fadeFrames);

                // Rejection sampling: dst must not overlap any event it will be
                // on screen with. Give up after maxRejects (skip the patch).
                bool placed = false;
                for (int attempt = 0; attempt < cfg.maxRejects && !placed; attempt++)
                {
                    var dst = new PatchRect(
                        (float)rng.NextDouble() * (1f - sizeW),
                        (float)rng.NextDouble() * (1f - sizeH),
                        sizeW, sizeH);

                    bool collides = false;
                    for (int k = 0; k < events.Count; k++)
                    {
                        var other = events[k];
                        bool timeOverlap = start < other.fadeEnd && other.start < fadeEnd;
                        if (timeOverlap && dst.Overlaps(other.dst)) { collides = true; break; }
                    }
                    if (collides) continue;

                    // src: random sub-rect of the source texture, same normalized size.
                    var src = new PatchRect(
                        (float)rng.NextDouble() * (1f - sizeW),
                        (float)rng.NextDouble() * (1f - sizeH),
                        sizeW, sizeH);

                    events.Add(new PatchEvent
                    {
                        dst = dst,
                        src = src,
                        start = start,
                        holdEnd = holdEnd,
                        fadeEnd = fadeEnd,
                        crossfadeRoll = (float)rng.NextDouble(),
                        anchorT = anchorT,
                    });
                    placed = true;
                }
            }

            events.Sort((a, b) => a.start.CompareTo(b.start));
            return events.ToArray();
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    }

    /// <summary>Sorted-cursor sweep over PatchEvents: O(active + newly-started) per
    /// forward frame. A backward frame (scrub) rewinds and replays — still deterministic.</summary>
    public class PatchSweep
    {
        private readonly PatchEvent[] _sorted;   // by start ascending
        private readonly List<int> _active = new List<int>(128);
        private int _cursor;
        private int _lastFrame = int.MinValue;

        public PatchSweep(PatchEvent[] events)
        {
            _sorted = (PatchEvent[])events.Clone();
            Array.Sort(_sorted, (a, b) => a.start.CompareTo(b.start));
        }

        /// <summary>Copies events active at <paramref name="frame"/> into
        /// <paramref name="outBuf"/>; returns the count. outBuf must be at least
        /// as long as the event array.</summary>
        public int Collect(int frame, PatchEvent[] outBuf)
        {
            if (frame < _lastFrame) { _cursor = 0; _active.Clear(); }  // backward scrub → rewind
            _lastFrame = frame;

            while (_cursor < _sorted.Length && _sorted[_cursor].start <= frame)
            {
                _active.Add(_cursor);
                _cursor++;
            }

            int n = 0;
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                var e = _sorted[_active[i]];
                if (frame >= e.fadeEnd) { _active.RemoveAt(i); continue; }
                if (frame >= e.start) outBuf[n++] = e;
            }
            return n;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Test Runner window → Run All (or CLI). Expected: all 8 `PatchEventSchedulerTests` + smoke test PASS.

- [ ] **Step 5: Commit**

```bash
cd /Users/toka/Developer/Graphics/EoC-biomes-compute
git add -A "Assets/Workspace/11.0 Biomes/src/sequencer_core" Assets/Tests
git commit -m "sequencer: patch event scheduler (scatter, size->hold, sigmoid crossfade, sweep) + tests"
```

---

### Task 3: SequencerComposite.compute + CompositeSequencer base pass

**Files:**
- Create: `Assets/Workspace/11.0 Biomes/src/computes/SequencerComposite.compute`
- Create: `Assets/Workspace/11.0 Biomes/src/components/sequencer/CompositeSequencer.cs`

**Interfaces:**
- Consumes: `SimulationManager.CompositeOutputTexture` (RenderTexture, null until Reset), `SimulationManager.rezX/rezY`, `GPUResourceManager` (`CreateTexture2D(w, h, FilterMode, RenderTextureFormat, string)`, `ReleaseAll()`).
- Produces (used by Tasks 4–8):
  - `CompositeSequencer.ComposerOutputTexture` (RenderTexture, ARGBHalf).
  - `void SetBaseWeight(float w)` — 0 lets a Replace cell own the frame.
  - `void PushCell(Texture src, Rect dstNorm, float weight, CellBlendMode mode)` (≤4/frame).
  - `void PushPatch(Texture src, Rect dstNorm, Rect srcNorm, float alpha)` (≤128/frame).
  - `enum CellBlendMode { Overlay = 0, Replace = 1 }`.
  - Draw-state lists are cleared at the end of every `LateUpdate` — mixers must push every frame.

- [ ] **Step 1: Write the compute shader**

`Assets/Workspace/11.0 Biomes/src/computes/SequencerComposite.compute`:

```hlsl
// Temporal Composer: base copy of the sim composite + per-rect blended draws
// (biome cells and scattered patches share RectBlendKernel — same math, one
// small dispatch per rect covering only that rect's pixels).

#pragma kernel BaseKernel
#pragma kernel RectBlendKernel
#pragma kernel DebugRectKernel

uint composerRezX;
uint composerRezY;

SamplerState sampler_linear_clamp;

RWTexture2D<float4> composerOut;

// ── BaseKernel ──
Texture2D<float4> baseTex;
float baseWeight;   // 0 = black base (a Replace cell owns the frame)

[numthreads(8, 8, 1)]
void BaseKernel(uint3 id : SV_DISPATCHTHREADID)
{
    if (id.x >= composerRezX || id.y >= composerRezY) return;
    float2 uv = (float2(id.xy) + 0.5) / float2((float)composerRezX, (float)composerRezY);
    float4 c = baseTex.SampleLevel(sampler_linear_clamp, uv, 0);
    composerOut[id.xy] = float4(c.rgb * baseWeight, 1.0);
}

// ── RectBlendKernel ── one dispatch per cell/patch; thread space = dst rect pixels
Texture2D<float4> rectSrc;
float4 dstRect;     // normalized x, y, w, h on the composer
float4 srcRect;     // normalized sub-rect of rectSrc to sample
float rectWeight;   // cell clip weight, or patch envelope alpha
int blendMode;      // 0 = Overlay (additive), 1 = Replace (lerp)

[numthreads(8, 8, 1)]
void RectBlendKernel(uint3 id : SV_DISPATCHTHREADID)
{
    uint2 rectSize = uint2(max(1.0, dstRect.z * composerRezX), max(1.0, dstRect.w * composerRezY));
    if (id.x >= rectSize.x || id.y >= rectSize.y) return;

    uint2 p = uint2(dstRect.xy * float2((float)composerRezX, (float)composerRezY)) + id.xy;
    if (p.x >= composerRezX || p.y >= composerRezY) return;

    float2 localUV = (float2(id.xy) + 0.5) / float2(rectSize);
    float2 uv = srcRect.xy + localUV * srcRect.zw;
    float4 c = rectSrc.SampleLevel(sampler_linear_clamp, uv, 0);

    float4 baseC = composerOut[p];
    float3 outRGB = blendMode == 1
        ? lerp(baseC.rgb, c.rgb, rectWeight)     // Replace / patch alpha
        : baseC.rgb + c.rgb * rectWeight;        // Overlay additive
    composerOut[p] = float4(saturate(outRGB), 1.0);
}

// ── DebugRectKernel ── thin outline around a rect (annotation overlay, off for show)
float4 debugColor;

[numthreads(8, 8, 1)]
void DebugRectKernel(uint3 id : SV_DISPATCHTHREADID)
{
    uint2 rectSize = uint2(max(1.0, dstRect.z * composerRezX), max(1.0, dstRect.w * composerRezY));
    if (id.x >= rectSize.x || id.y >= rectSize.y) return;
    bool edge = id.x < 2 || id.y < 2 || id.x >= rectSize.x - 2 || id.y >= rectSize.y - 2;
    if (!edge) return;
    uint2 p = uint2(dstRect.xy * float2((float)composerRezX, (float)composerRezY)) + id.xy;
    if (p.x >= composerRezX || p.y >= composerRezY) return;
    composerOut[p] = float4(debugColor.rgb, 1.0);
}
```

- [ ] **Step 2: Write CompositeSequencer**

`Assets/Workspace/11.0 Biomes/src/components/sequencer/CompositeSequencer.cs`:

```csharp
using System.Collections.Generic;
using UnityEngine;

namespace Biomes
{
    public enum CellBlendMode { Overlay = 0, Replace = 1 }

    /// <summary>
    /// Owns the show output texture (composerOutTex) and composites, every rendered
    /// frame AFTER SimulationManager.Render(): base = sim composite, then biome cells,
    /// then scattered patches, then optional debug outlines. Timeline track mixers push
    /// per-frame draw state via SetBaseWeight/PushCell/PushPatch; state clears each frame.
    /// Clear-in-place rule (ADR-0008): composerOutTex is allocated once and reused so the
    /// ExternalTextureSender's native server never tears down; realloc only on rez change.
    /// </summary>
    [DefaultExecutionOrder(1000)]   // after SimulationManager.LateUpdate → Render()
    public class CompositeSequencer : MonoBehaviour
    {
        private struct RectDraw
        {
            public Texture src;
            public Rect dst;        // normalized
            public Rect srcRect;    // normalized
            public float weight;
            public int mode;        // CellBlendMode, or 1 for patches (alpha lerp)
        }

        [Header("References")]
        public SimulationManager simManager;
        public ComputeShader sequencerCS;
        [Tooltip("Display material re-pointed at composerOutTex (HDRP Unlit _UnlitColorMap).")]
        public Material composerOutMat;
        [Tooltip("Receiver #2: the StreamDiffusion return stream (Spout from TouchDesigner).")]
        public ExternalTextureReceiver diffusionReturn;

        [Header("Composer")]
        [Tooltip("Composer rez = sim composite rez × this. 1 keeps ScreenLayout pixel rects valid.")]
        [Range(0.25f, 1f)] public float composerResScale = 1f;

        [Header("Debug overlay (annotation layer — OFF for show)")]
        public bool debugOutlines = false;
        public Color debugCellColor = new(0f, 1f, 0.6f, 1f);
        public Color debugPatchColor = new(1f, 0.4f, 0f, 1f);

        public const int MaxCells = 4;
        public const int MaxPatchDraws = 128;

        private readonly List<RectDraw> _cells = new(MaxCells);
        private readonly List<RectDraw> _patches = new(MaxPatchDraws);
        private float _baseWeight = 1f;

        private GPUResourceManager gpu;
        private RenderTexture _composerTex;
        private int _baseKernel = -1, _rectKernel = -1, _debugKernel = -1;
        private int _rezX, _rezY;
        private int _allocRezX = -1, _allocRezY = -1;

        private static readonly int s_ComposerRezXID = Shader.PropertyToID("composerRezX");
        private static readonly int s_ComposerRezYID = Shader.PropertyToID("composerRezY");
        private static readonly int s_ComposerOutID = Shader.PropertyToID("composerOut");
        private static readonly int s_BaseTexID = Shader.PropertyToID("baseTex");
        private static readonly int s_BaseWeightID = Shader.PropertyToID("baseWeight");
        private static readonly int s_RectSrcID = Shader.PropertyToID("rectSrc");
        private static readonly int s_DstRectID = Shader.PropertyToID("dstRect");
        private static readonly int s_SrcRectID = Shader.PropertyToID("srcRect");
        private static readonly int s_RectWeightID = Shader.PropertyToID("rectWeight");
        private static readonly int s_BlendModeID = Shader.PropertyToID("blendMode");
        private static readonly int s_DebugColorID = Shader.PropertyToID("debugColor");

        /// <summary>Show output. Null until first play-mode LateUpdate.</summary>
        public RenderTexture ComposerOutputTexture => _composerTex;

        // ── Per-frame state (pushed by Timeline mixers, cleared after render) ──

        /// <summary>0 lets a Replace cell own the frame; default 1 restores each frame.</summary>
        public void SetBaseWeight(float w) => _baseWeight = Mathf.Clamp01(w);

        public void PushCell(Texture src, Rect dstNorm, float weight, CellBlendMode mode)
        {
            if (src == null || weight <= 0f || _cells.Count >= MaxCells) return;
            _cells.Add(new RectDraw
            {
                src = src, dst = dstNorm, srcRect = new Rect(0, 0, 1, 1),
                weight = Mathf.Clamp01(weight), mode = (int)mode,
            });
        }

        public void PushPatch(Texture src, Rect dstNorm, Rect srcNorm, float alpha)
        {
            if (src == null || alpha <= 0f || _patches.Count >= MaxPatchDraws) return;
            _patches.Add(new RectDraw
            {
                src = src, dst = dstNorm, srcRect = srcNorm,
                weight = Mathf.Clamp01(alpha), mode = 1,   // patches always alpha-lerp
            });
        }

        // ── Render ──

        void LateUpdate()
        {
            if (!Application.isPlaying || simManager == null || sequencerCS == null)
            {
                ClearFrameState();
                return;
            }

            var baseTex = simManager.CompositeOutputTexture;
            if (baseTex == null) { ClearFrameState(); return; }   // pre-Reset

            EnsureAllocated();

            sequencerCS.SetInt(s_ComposerRezXID, _rezX);
            sequencerCS.SetInt(s_ComposerRezYID, _rezY);

            // 1. base
            sequencerCS.SetTexture(_baseKernel, s_BaseTexID, baseTex);
            sequencerCS.SetFloat(s_BaseWeightID, _baseWeight);
            sequencerCS.SetTexture(_baseKernel, s_ComposerOutID, _composerTex);
            DispatchFull(_baseKernel);

            // 2. cells, then 3. patches — same kernel, one small dispatch per rect
            for (int i = 0; i < _cells.Count; i++) DispatchRect(_rectKernel, _cells[i]);
            for (int i = 0; i < _patches.Count; i++) DispatchRect(_rectKernel, _patches[i]);

            // 4. optional annotation outlines
            if (debugOutlines)
            {
                for (int i = 0; i < _cells.Count; i++) DispatchDebug(_cells[i].dst, debugCellColor);
                for (int i = 0; i < _patches.Count; i++) DispatchDebug(_patches[i].dst, debugPatchColor);
            }

            if (composerOutMat != null)
                composerOutMat.SetTexture("_UnlitColorMap", _composerTex);

            ClearFrameState();
        }

        private void DispatchFull(int kernel)
        {
            sequencerCS.GetKernelThreadGroupSizes(kernel, out uint wx, out uint wy, out _);
            sequencerCS.Dispatch(kernel,
                Mathf.CeilToInt((float)_rezX / wx), Mathf.CeilToInt((float)_rezY / wy), 1);
        }

        private void DispatchRect(int kernel, in RectDraw d)
        {
            sequencerCS.SetTexture(kernel, s_RectSrcID, d.src);
            sequencerCS.SetTexture(kernel, s_ComposerOutID, _composerTex);
            sequencerCS.SetVector(s_DstRectID, new Vector4(d.dst.x, d.dst.y, d.dst.width, d.dst.height));
            sequencerCS.SetVector(s_SrcRectID, new Vector4(d.srcRect.x, d.srcRect.y, d.srcRect.width, d.srcRect.height));
            sequencerCS.SetFloat(s_RectWeightID, d.weight);
            sequencerCS.SetInt(s_BlendModeID, d.mode);
            int px = Mathf.Max(1, Mathf.CeilToInt(d.dst.width * _rezX));
            int py = Mathf.Max(1, Mathf.CeilToInt(d.dst.height * _rezY));
            sequencerCS.GetKernelThreadGroupSizes(kernel, out uint wx, out uint wy, out _);
            sequencerCS.Dispatch(kernel, Mathf.CeilToInt((float)px / wx), Mathf.CeilToInt((float)py / wy), 1);
        }

        private void DispatchDebug(Rect dst, Color color)
        {
            sequencerCS.SetTexture(_debugKernel, s_ComposerOutID, _composerTex);
            sequencerCS.SetVector(s_DstRectID, new Vector4(dst.x, dst.y, dst.width, dst.height));
            sequencerCS.SetVector(s_DebugColorID, color);
            int px = Mathf.Max(1, Mathf.CeilToInt(dst.width * _rezX));
            int py = Mathf.Max(1, Mathf.CeilToInt(dst.height * _rezY));
            sequencerCS.GetKernelThreadGroupSizes(_debugKernel, out uint wx, out uint wy, out _);
            sequencerCS.Dispatch(_debugKernel, Mathf.CeilToInt((float)px / wx), Mathf.CeilToInt((float)py / wy), 1);
        }

        private void ClearFrameState()
        {
            _cells.Clear();
            _patches.Clear();
            _baseWeight = 1f;
        }

        private void EnsureAllocated()
        {
            _rezX = Mathf.Max(8, Mathf.RoundToInt(simManager.rezX * composerResScale));
            _rezY = Mathf.Max(8, Mathf.RoundToInt(simManager.rezY * composerResScale));
            if (gpu != null && _rezX == _allocRezX && _rezY == _allocRezY) return;

            Release();
            gpu = new GPUResourceManager();
            _composerTex = gpu.CreateTexture2D(_rezX, _rezY, FilterMode.Trilinear,
                RenderTextureFormat.ARGBHalf, name: "composer_out");
            _baseKernel = sequencerCS.FindKernel("BaseKernel");
            _rectKernel = sequencerCS.FindKernel("RectBlendKernel");
            _debugKernel = sequencerCS.FindKernel("DebugRectKernel");
            _allocRezX = _rezX; _allocRezY = _rezY;
        }

        public void Release()
        {
            gpu?.ReleaseAll();
            gpu = null;
            _composerTex = null;
            _allocRezX = _allocRezY = -1;
        }

        void OnDestroy() => Release();
        void OnDisable() => Release();
    }
}
```

- [ ] **Step 3: Manual verification in Unity**

1. Open the project in Unity 6000.3.10f1; let it compile (0 errors in Console).
2. Open `Assets/Workspace/11.2 SIGGRAPH Scene/Scene_SIGGRAPH.unity`.
3. Create empty GameObject `TemporalComposer`; add `CompositeSequencer`; assign: `simManager` = the scene's SimulationManager, `sequencerCS` = `SequencerComposite.compute`, `composerOutMat` = `Assets/Workspace/11.2 SIGGRAPH Scene/materials/m_composite.mat`.
4. Enter Play mode. Expected: the composite quad shows exactly what it showed before (base pass copies the sim composite 1:1); Console has no errors; frame rate unchanged (±1–2 fps).
5. In the inspector, toggle `debugOutlines` on → nothing changes (no cells/patches pushed yet) and no errors.

- [ ] **Step 4: Commit**

```bash
cd /Users/toka/Developer/Graphics/EoC-biomes-compute
git add -A "Assets/Workspace/11.0 Biomes/src/computes/SequencerComposite.compute" "Assets/Workspace/11.0 Biomes/src/components/sequencer" "Assets/Workspace/11.2 SIGGRAPH Scene"
git commit -m "sequencer: composer RT + base/rect-blend/debug kernels, scene wiring"
```

---

### Task 4: BiomeCellRig + SimulationManager seams

**Files:**
- Modify: `Assets/Workspace/11.0 Biomes/src/components/core/SimulationManager.cs:133-147` (Awake) and `:252-261` (Step influence assignment)
- Create: `Assets/Workspace/11.0 Biomes/src/components/sequencer/BiomeCellRig.cs`

**Interfaces:**
- Consumes: `SimulationManager.Step()` (public), `SimulationManager.CompositeOutputTexture`, `SimulationManager.stepsPerTick`.
- Produces (used by Task 5's `BiomeCellMixer` and Task 8's routing):
  - `SimulationManager.ownsGlobalTiming` (bool, default true) — cell-rig managers set false so they don't fight over `Time.fixedDeltaTime`/`Application.targetFrameRate`.
  - `SimulationManager.influenceOverride` (Texture, default null) — when non-null, replaces `externalInput.OutputTexture` as the sims' `externalInfluenceTex`.
  - `BiomeCellRig.OutputTexture` (RenderTexture), `BiomeCellRig.Running` (bool, set by mixer), `BiomeCellRig.cellRate` (float Hz).

- [ ] **Step 1: Add the two SimulationManager seams**

In `SimulationManager.cs`, add fields after `public int targetFPS = 60;` (line 32):

```csharp
        [Tooltip("Untick on cell-rig (nested) managers: they must not write the global " +
                 "Time.fixedDeltaTime / targetFrameRate settings the main manager owns.")]
        public bool ownsGlobalTiming = true;

        [Tooltip("When set (by the Timeline RoutingTrack), overrides the external receiver " +
                 "as the sims' influence texture. Null = normal externalInput path.")]
        [System.NonSerialized] public Texture influenceOverride;
```

Change `Awake()` to guard the global writes:

```csharp
        void Awake()
        {
            // Fixed-timestep sim: Step() runs in FixedUpdate at simRate steps/sec,
            // independent of render FPS. maxAllowedTimestep is Unity's spiral-of-death
            // guard (see field tooltips). Both are global Time settings owned by the MAIN
            // manager only — cell rigs (ownsGlobalTiming = false) skip them.
            if (!ownsGlobalTiming) return;

            ApplySimRate();
            Time.maximumDeltaTime = maxAllowedTimestep;

            if (limitFPS)
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = targetFPS;
            }
        }
```

In `Step()`, change the influence assignment (currently `Texture influenceTex = externalInput != null ? externalInput.OutputTexture : null;`):

```csharp
            Texture influenceTex = influenceOverride != null
                ? influenceOverride
                : (externalInput != null ? externalInput.OutputTexture : null);
```

- [ ] **Step 2: Write BiomeCellRig**

`Assets/Workspace/11.0 Biomes/src/components/sequencer/BiomeCellRig.cs`:

```csharp
using UnityEngine;

namespace Biomes
{
    /// <summary>
    /// One live biome cell: wraps a nested SimulationManager (own Biome + sims at reduced
    /// rez, own preset assets) and steps it at its own rate, decoupled from the main sim's
    /// fixed clock. The wrapped manager must have ownsGlobalTiming = false and
    /// stepsPerTick = 0 (so its FixedUpdate does nothing; this rig is the only stepper).
    /// The Timeline BiomeCellMixer sets Running while a cell clip is active.
    /// </summary>
    public class BiomeCellRig : MonoBehaviour
    {
        [Tooltip("Nested manager: ownsGlobalTiming OFF, stepsPerTick 0, rez ~1024.")]
        public SimulationManager manager;

        [Range(1f, 60f)] public float cellRate = 20f;

        [Tooltip("Set by the Timeline mixer while a cell clip is active. Manual for testing.")]
        public bool Running;

        private float _accum;

        /// <summary>Cell source texture for the composer (null until manager Reset).</summary>
        public RenderTexture OutputTexture => manager != null ? manager.CompositeOutputTexture : null;

        void Update()
        {
            if (!Running || manager == null || !Application.isPlaying) return;
            _accum += Time.deltaTime;
            float dt = 1f / Mathf.Max(1f, cellRate);
            int guard = 0;                      // spiral-of-death guard, mirrors main sim
            while (_accum >= dt && guard++ < 4)
            {
                manager.Step();
                _accum -= dt;
            }
            if (_accum >= dt) _accum = 0f;      // dropped time: cell slows, never bursts
        }
    }
}
```

- [ ] **Step 3: Build one cell rig in the scene (manual)**

1. In `Scene_SIGGRAPH.unity`: duplicate the existing `SimulationManager` GameObject hierarchy (manager + `Biome` + one or two sims — e.g. keep Physarum, delete the Boid/Termite children to keep the rig light). Rename root `CellRig_A`.
2. On `CellRig_A`'s SimulationManager: `rezX = rezY = 1024`, `ownsGlobalTiming = OFF`, `stepsPerTick = 0`, `limitFPS = OFF`; assign a DIFFERENT param preset asset (e.g. `assets/Snapshots/Physarum_20260711_201424`) to its sim than the main scene uses; clear its `compositeOutMat`/`compositeOutputQuad`/`recordingCamera` references (the rig renders only to its own composite texture, not to screen).
3. Add `BiomeCellRig` component to `CellRig_A`; assign `manager`.
4. Save scene. Enter Play mode; tick `Running` on the rig. Expected: no console errors; in the inspector debugger the rig manager's `CompositeOutputTexture` is non-null and animating (select its SimulationManager, use the texture preview via the material or frame debugger). Main output unchanged.
5. Save as prefab: drag `CellRig_A` into `Assets/Workspace/11.2 SIGGRAPH Scene/assets/` → `BiomeCellRig.prefab`. Keep 1 instance in scene for now (Task 10 adds more).

- [ ] **Step 4: Commit**

```bash
cd /Users/toka/Developer/Graphics/EoC-biomes-compute
git add -A "Assets/Workspace/11.0 Biomes/src/components" "Assets/Workspace/11.2 SIGGRAPH Scene"
git commit -m "sequencer: BiomeCellRig (self-paced nested manager) + timing/influence seams"
```

---

### Task 5: BiomeCellTrack (Timeline) + reset signals

**Files:**
- Create: `Assets/Workspace/11.0 Biomes/src/components/sequencer/tracks/BiomeCellTrack.cs`
- Create: `Assets/Workspace/11.0 Biomes/src/components/sequencer/tracks/BiomeCellClip.cs`
- Create: `Assets/Workspace/11.0 Biomes/src/components/sequencer/tracks/BiomeCellMixer.cs`
- Create (editor assets, manual): `Assets/Workspace/11.2 SIGGRAPH Scene/assets/signals/` — 4 `SignalAsset`s

**Interfaces:**
- Consumes: `CompositeSequencer.PushCell(Texture, Rect, float, CellBlendMode)`, `SetBaseWeight(float)`, `BiomeCellRig.{OutputTexture, Running}`, `SimulationManager.CompositeOutputTexture`, `ExternalTextureReceiver.OutputTexture`.
- Produces:
  - `enum CellSource { Rig = 0, MainComposite = 1, InputReceiver = 2, DiffusionReturn = 3 }` (in `BiomeCellClip.cs`; also used by Task 6's patch clip).
  - `Texture CompositeSequencer.ResolveSource(CellSource kind, BiomeCellRig rig)` — add this method to `CompositeSequencer` in this task (code below).
  - Track binding: `BiomeCellTrack` binds to `CompositeSequencer`.

- [ ] **Step 1: Add ResolveSource to CompositeSequencer**

Append inside `CompositeSequencer` (after `PushPatch`), plus a receiver reference:

```csharp
        [Tooltip("Receiver #1: the general external input (same one SimulationManager uses).")]
        public ExternalTextureReceiver inputReceiver;

        /// <summary>Resolves a clip's source kind to a live texture; null = draw skipped
        /// (graceful degradation, never black).</summary>
        public Texture ResolveSource(CellSource kind, BiomeCellRig rig)
        {
            switch (kind)
            {
                case CellSource.Rig: return rig != null ? rig.OutputTexture : null;
                case CellSource.MainComposite: return simManager != null ? simManager.CompositeOutputTexture : null;
                case CellSource.InputReceiver: return inputReceiver != null ? inputReceiver.OutputTexture : null;
                case CellSource.DiffusionReturn: return diffusionReturn != null ? diffusionReturn.OutputTexture : null;
                default: return null;
            }
        }
```

- [ ] **Step 2: Write the clip asset**

`Assets/Workspace/11.0 Biomes/src/components/sequencer/tracks/BiomeCellClip.cs`:

```csharp
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace Biomes
{
    public enum CellSource { Rig = 0, MainComposite = 1, InputReceiver = 2, DiffusionReturn = 3 }

    /// <summary>One biome cell on the timeline: which texture, where on the composer,
    /// overlay or replace. Clip ease-in/out curves drive the blend weight.</summary>
    public class BiomeCellClip : PlayableAsset, ITimelineClipAsset
    {
        public CellSource source = CellSource.Rig;
        public ExposedReference<BiomeCellRig> rig;
        [Tooltip("Normalized composer rect: x, y, width, height in 0..1.")]
        public Rect dstRect = new(0.25f, 0.25f, 0.5f, 0.5f);
        public CellBlendMode mode = CellBlendMode.Overlay;
        [Tooltip("Replace only: also duck the base sim composite to 1-weight while active.")]
        public bool duckBase = false;

        public ClipCaps clipCaps => ClipCaps.Blending;

        public override Playable CreatePlayable(PlayableGraph graph, GameObject owner)
        {
            var playable = ScriptPlayable<BiomeCellBehaviour>.Create(graph);
            var b = playable.GetBehaviour();
            b.clip = this;
            b.rig = rig.Resolve(graph.GetResolver());
            return playable;
        }
    }

    public class BiomeCellBehaviour : PlayableBehaviour
    {
        public BiomeCellClip clip;
        public BiomeCellRig rig;
    }
}
```

- [ ] **Step 3: Write track + mixer**

`Assets/Workspace/11.0 Biomes/src/components/sequencer/tracks/BiomeCellTrack.cs`:

```csharp
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace Biomes
{
    [TrackColor(0.2f, 0.8f, 0.5f)]
    [TrackClipType(typeof(BiomeCellClip))]
    [TrackBindingType(typeof(CompositeSequencer))]
    public class BiomeCellTrack : TrackAsset
    {
        public override Playable CreateTrackMixer(PlayableGraph graph, GameObject go, int inputCount)
            => ScriptPlayable<BiomeCellMixer>.Create(graph, inputCount);
    }
}
```

`Assets/Workspace/11.0 Biomes/src/components/sequencer/tracks/BiomeCellMixer.cs`:

```csharp
using UnityEngine;
using UnityEngine.Playables;

namespace Biomes
{
    /// <summary>Pushes every active cell clip into the CompositeSequencer each frame,
    /// weight = Timeline input weight (clip ease curves). Rigs run while their clip has
    /// any weight (they keep evolving through the blend).</summary>
    public class BiomeCellMixer : PlayableBehaviour
    {
        public override void ProcessFrame(Playable playable, FrameData info, object playerData)
        {
            var seq = playerData as CompositeSequencer;
            if (seq == null) return;

            int n = playable.GetInputCount();
            for (int i = 0; i < n; i++)
            {
                float w = playable.GetInputWeight(i);
                var input = (ScriptPlayable<BiomeCellBehaviour>)playable.GetInput(i);
                var b = input.GetBehaviour();
                if (b.clip == null) continue;

                if (b.rig != null) b.rig.Running = w > 0f;
                if (w <= 0f) continue;

                Texture src = seq.ResolveSource(b.clip.source, b.rig);
                if (src == null) continue;   // rig not reset yet / receiver silent → skip

                seq.PushCell(src, b.clip.dstRect, w, b.clip.mode);
                if (b.clip.mode == CellBlendMode.Replace && b.clip.duckBase)
                    seq.SetBaseWeight(1f - w);
            }
        }
    }
}
```

- [ ] **Step 4: Wire timeline + reset signals in the scene (manual)**

1. In `Scene_SIGGRAPH.unity`, on `TemporalComposer`: Add Component → `PlayableDirector`. Project window → `Assets/Workspace/11.2 SIGGRAPH Scene/assets/` → right-click → Create → Timeline → name `ShowSequence`. Assign to the director.
2. Open Window → Sequencing → Timeline with `TemporalComposer` selected. Add track → `Biomes` → `Biome Cell Track`; bind it to the `CompositeSequencer` component.
3. Right-click the track → Add Biome Cell Clip; in the clip inspector set `source = Rig`, drag `CellRig_A` into `rig`, `dstRect = (0.05, 0.1, 0.25, 0.8)`, `mode = Overlay`. Give the clip 10 s with 2 s ease-in/ease-out (drag clip edges).
4. Reset signals: create folder `assets/signals/`; Create → Signal (×4): `Sig_ResetSims`, `Sig_ResetPhysarum`, `Sig_ResetBoids`, `Sig_ResetTermites`. On the main `SimulationManager` GameObject add a `SignalReceiver`; add 4 reactions mapping each SignalAsset to the matching public method (`ResetSimsOnly`, `ResetPhysarum`, `ResetBoids`, `ResetTermites`). In Timeline add a Signal Track bound to that SimulationManager GameObject and drop a `Sig_ResetPhysarum` emitter at ~5 s.
5. Enter Play mode, press Play on the director (Timeline window transport). Expected: cell A's physarum fades in over the left band of the output, holds, fades out; at ~5 s the main physarum respawns (reset signal); Console clean. Toggle `debugOutlines` → green outline around the cell rect.

- [ ] **Step 5: Commit**

```bash
cd /Users/toka/Developer/Graphics/EoC-biomes-compute
git add -A "Assets/Workspace/11.0 Biomes/src/components/sequencer" "Assets/Workspace/11.2 SIGGRAPH Scene"
git commit -m "sequencer: BiomeCellTrack/clip/mixer, reset signal wiring, ShowSequence asset"
```

---

### Task 6: PatchScatterTrack

**Files:**
- Create: `Assets/Workspace/11.0 Biomes/src/components/sequencer/tracks/PatchScatterClip.cs`
- Create: `Assets/Workspace/11.0 Biomes/src/components/sequencer/tracks/PatchScatterTrack.cs`
- Create: `Assets/Workspace/11.0 Biomes/src/components/sequencer/tracks/PatchScatterMixer.cs`

**Interfaces:**
- Consumes: `PatchEventScheduler.Generate/Envelope/Sigmoid`, `PatchSweep.Collect(int, PatchEvent[])`, `PatchScatterConfig`, `CompositeSequencer.{PushPatch, ResolveSource, MaxPatchDraws}`, `CellSource`.
- Produces: `PatchScatterClip` (serialized scatter params + `CellSource sourceA/sourceB`), `PatchScatterBehaviour` (lazily builds events + sweep; exposes them to the mixer).
- Frame clock: clip-local frames at a fixed `frameRate = 60f` reference (independent of render fps, so the schedule is scrub-deterministic).

- [ ] **Step 1: Write the clip**

`Assets/Workspace/11.0 Biomes/src/components/sequencer/tracks/PatchScatterClip.cs`:

```csharp
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;
using Biomes.Sequencer;

namespace Biomes
{
    /// <summary>Anadol-grammar patch scatter over the composer: deterministic from
    /// (params, seed). SourceB + crossfade gives the per-patch stochastic dissolve
    /// (e.g. A = main composite, B = StreamDiffusion return).</summary>
    public class PatchScatterClip : PlayableAsset, ITimelineClipAsset
    {
        public const float FrameRate = 60f;   // schedule clock, independent of render fps

        [Header("Sources")]
        public CellSource sourceA = CellSource.MainComposite;
        public CellSource sourceB = CellSource.DiffusionReturn;

        [Header("Scatter (deterministic per seed)")]
        public int seed = 1234;
        [Range(1, 512)] public int count = 64;
        [Range(0.01f, 0.5f)] public float minSize = 0.03f;
        [Range(0.02f, 0.9f)] public float maxSize = 0.25f;

        [Header("Timing (frames @ 60)")]
        [Range(1, 60)] public int holdMinFrames = 9;
        [Range(10, 300)] public int holdMaxFrames = 90;
        [Range(1, 120)] public int fadeFrames = 30;
        [Range(0, 600)] public int leadFrames = 150;
        [Range(0, 600)] public int trailFrames = 90;
        [Range(0, 60)] public int staggerJitterFrames = 12;

        [Header("A→B crossfade")]
        [Range(0f, 1f)] public float crossfadeCenter = 0.5f;
        [Range(0.01f, 0.5f)] public float crossfadeWidth = 0.15f;

        public ClipCaps clipCaps => ClipCaps.Blending;

        public override Playable CreatePlayable(PlayableGraph graph, GameObject owner)
        {
            var playable = ScriptPlayable<PatchScatterBehaviour>.Create(graph);
            playable.GetBehaviour().clip = this;
            return playable;
        }

        public PatchScatterConfig BuildConfig(double clipDuration, float composerAspect) => new()
        {
            seed = seed,
            count = count,
            minSize = minSize,
            maxSize = maxSize,
            aspect = composerAspect,
            holdMinFrames = holdMinFrames,
            holdMaxFrames = holdMaxFrames,
            fadeFrames = fadeFrames,
            leadFrames = leadFrames,
            trailFrames = trailFrames,
            staggerJitterFrames = staggerJitterFrames,
            crossfadeCenter = crossfadeCenter,
            crossfadeWidth = crossfadeWidth,
            durationFrames = Mathf.Max(1, (int)(clipDuration * FrameRate)),
            maxRejects = 40,
        };
    }

    public class PatchScatterBehaviour : PlayableBehaviour
    {
        public PatchScatterClip clip;

        // Built lazily on first ProcessFrame (needs clip duration + composer aspect).
        public PatchEvent[] events;
        public PatchSweep sweep;
        public PatchEvent[] activeBuf;   // preallocated, no per-frame alloc

        public void EnsureBuilt(double duration, float aspect)
        {
            if (events != null) return;
            events = PatchEventScheduler.Generate(clip.BuildConfig(duration, aspect));
            sweep = new PatchSweep(events);
            activeBuf = new PatchEvent[events.Length];
        }
    }
}
```

- [ ] **Step 2: Write track + mixer**

`Assets/Workspace/11.0 Biomes/src/components/sequencer/tracks/PatchScatterTrack.cs`:

```csharp
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace Biomes
{
    [TrackColor(1f, 0.5f, 0.1f)]
    [TrackClipType(typeof(PatchScatterClip))]
    [TrackBindingType(typeof(CompositeSequencer))]
    public class PatchScatterTrack : TrackAsset
    {
        public override Playable CreateTrackMixer(PlayableGraph graph, GameObject go, int inputCount)
            => ScriptPlayable<PatchScatterMixer>.Create(graph, inputCount);
    }
}
```

`Assets/Workspace/11.0 Biomes/src/components/sequencer/tracks/PatchScatterMixer.cs`:

```csharp
using UnityEngine;
using UnityEngine.Playables;
using Biomes.Sequencer;

namespace Biomes
{
    /// <summary>Each frame: sweep the clip's deterministic events at the clip-local
    /// frame, compute envelope alpha × clip weight, pick source A/B via the sigmoid
    /// stochastic crossfade, and push draws to the CompositeSequencer.</summary>
    public class PatchScatterMixer : PlayableBehaviour
    {
        public override void ProcessFrame(Playable playable, FrameData info, object playerData)
        {
            var seq = playerData as CompositeSequencer;
            if (seq == null) return;

            int n = playable.GetInputCount();
            for (int i = 0; i < n; i++)
            {
                float clipWeight = playable.GetInputWeight(i);
                if (clipWeight <= 0f) continue;

                var input = (ScriptPlayable<PatchScatterBehaviour>)playable.GetInput(i);
                var b = input.GetBehaviour();
                if (b.clip == null) continue;

                var composer = seq.ComposerOutputTexture;
                float aspect = composer != null ? (float)composer.width / composer.height : 5f;
                b.EnsureBuilt(input.GetDuration(), aspect);

                Texture texA = seq.ResolveSource(b.clip.sourceA, null);
                Texture texB = seq.ResolveSource(b.clip.sourceB, null);
                if (texB == null) texB = texA;   // diffusion stream down → degrade to A
                if (texA == null) texA = texB;   // (never black)
                if (texA == null) continue;

                int frame = (int)(input.GetTime() * PatchScatterClip.FrameRate);
                int active = b.sweep.Collect(frame, b.activeBuf);

                for (int p = 0; p < active && p < CompositeSequencer.MaxPatchDraws; p++)
                {
                    ref var e = ref b.activeBuf[p];
                    float alpha = PatchEventScheduler.Envelope(in e, frame) * clipWeight;
                    if (alpha <= 0f) continue;
                    float sig = PatchEventScheduler.Sigmoid(e.anchorT, b.clip.crossfadeCenter, b.clip.crossfadeWidth);
                    Texture src = e.crossfadeRoll < sig ? texB : texA;
                    seq.PushPatch(src,
                        new Rect(e.dst.x, e.dst.y, e.dst.w, e.dst.h),
                        new Rect(e.src.x, e.src.y, e.src.w, e.src.h),
                        alpha);
                }
            }
        }
    }
}
```

- [ ] **Step 3: Manual verification**

1. Timeline window → add `Patch Scatter Track`, bind to `CompositeSequencer`. Add a 30 s `PatchScatterClip`: `sourceA = MainComposite`, `sourceB = MainComposite` (no diffusion stream yet), defaults otherwise.
2. Play mode → play the director. Expected: small square patches of the live composite pop over the output, big ones flashing briefly, small ones lingering ~1.5 s, cascading (not simultaneous), fading out individually. `debugOutlines` on → orange outlines.
3. Scrub the director back and forth in the Timeline window. Expected: patch layout at a given time is identical every pass (deterministic), no errors, no GC spikes in the Profiler (Profiler → Memory → GC Alloc ≈ 0 B/frame in steady state after the first clip frame).
4. Stop. Change `seed` → different scatter. Same seed → same scatter.

- [ ] **Step 4: Commit**

```bash
cd /Users/toka/Developer/Graphics/EoC-biomes-compute
git add -A "Assets/Workspace/11.0 Biomes/src/components/sequencer" "Assets/Workspace/11.2 SIGGRAPH Scene"
git commit -m "sequencer: PatchScatterTrack — deterministic Anadol patch layer on timeline"
```

---

### Task 7: ParamSnapshotTrack

**Files:**
- Create: `Assets/Workspace/11.0 Biomes/src/components/sequencer/tracks/ParamSnapshotClip.cs`
- Create: `Assets/Workspace/11.0 Biomes/src/components/sequencer/tracks/ParamSnapshotTrack.cs`
- Create: `Assets/Workspace/11.0 Biomes/src/components/sequencer/tracks/ParamSnapshotMixer.cs`

**Interfaces:**
- Consumes: `SimulationBase.{LiveParamSet, ModulatableParams}` (`IParamSet`: `TypeCount`, `GetValue(string, int)`, `SetValue(string, int, float)`), `SimulationManager.simulations`, `ParameterInterpolator.LerpHue01(float, float, float)` (existing public static), Timeline clip weight for easing.
- Produces: `ParamSnapshotClip { ScriptableObject snapshot; int simIndex; }` bound via `ParamSnapshotTrack` to `SimulationManager`. Interpolates current→snapshot by the clip's eased weight; snapshot of "from" taken once when the clip first gains weight.

- [ ] **Step 1: Write clip, track, mixer**

`Assets/Workspace/11.0 Biomes/src/components/sequencer/tracks/ParamSnapshotClip.cs`:

```csharp
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace Biomes
{
    /// <summary>Timeline waypoint: ease one sim's live params from their state at clip
    /// start toward a snapshot preset asset. Clip ease-in curve = interpolation curve;
    /// hold the clip at weight 1 to hold the look.</summary>
    public class ParamSnapshotClip : PlayableAsset, ITimelineClipAsset
    {
        [Tooltip("A params preset/snapshot asset implementing IParamSet (PhysarumParams, BoidParams, TermiteParams instance).")]
        public ScriptableObject snapshot;
        [Tooltip("Index into SimulationManager.simulations.")]
        public int simIndex = 0;

        public ClipCaps clipCaps => ClipCaps.Blending;

        public override Playable CreatePlayable(PlayableGraph graph, GameObject owner)
        {
            var playable = ScriptPlayable<ParamSnapshotBehaviour>.Create(graph);
            playable.GetBehaviour().clip = this;
            return playable;
        }
    }

    public class ParamSnapshotBehaviour : PlayableBehaviour
    {
        public ParamSnapshotClip clip;

        // "from" values captured the first frame the clip has weight; name → per-type values.
        public System.Collections.Generic.Dictionary<string, float[]> from;
        public bool warned;
    }
}
```

`Assets/Workspace/11.0 Biomes/src/components/sequencer/tracks/ParamSnapshotTrack.cs`:

```csharp
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace Biomes
{
    [TrackColor(0.55f, 0.35f, 0.9f)]
    [TrackClipType(typeof(ParamSnapshotClip))]
    [TrackBindingType(typeof(SimulationManager))]
    public class ParamSnapshotTrack : TrackAsset
    {
        public override Playable CreateTrackMixer(PlayableGraph graph, GameObject go, int inputCount)
            => ScriptPlayable<ParamSnapshotMixer>.Create(graph, inputCount);
    }
}
```

`Assets/Workspace/11.0 Biomes/src/components/sequencer/tracks/ParamSnapshotMixer.cs`:

```csharp
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Playables;

namespace Biomes
{
    /// <summary>Applies snapshot clips: live = lerp(from, snapshot, clipWeight) for every
    /// modulatable param (hue via shortest-arc). "from" is captured when the clip first
    /// gains weight, so easing starts from whatever the show drifted to.</summary>
    public class ParamSnapshotMixer : PlayableBehaviour
    {
        public override void ProcessFrame(Playable playable, FrameData info, object playerData)
        {
            var manager = playerData as SimulationManager;
            if (manager == null || !Application.isPlaying) return;

            int n = playable.GetInputCount();
            for (int i = 0; i < n; i++)
            {
                float w = playable.GetInputWeight(i);
                var input = (ScriptPlayable<ParamSnapshotBehaviour>)playable.GetInput(i);
                var b = input.GetBehaviour();
                if (b.clip == null) continue;

                if (w <= 0f) { b.from = null; continue; }   // re-capture on next entry

                var target = b.clip.snapshot as IParamSet;
                if (target == null)
                {
                    if (!b.warned && b.clip.snapshot != null)
                    {
                        Debug.LogWarning($"ParamSnapshotClip: '{b.clip.snapshot.name}' is not an IParamSet preset; clip is a no-op");
                        b.warned = true;
                    }
                    continue;
                }

                var sim = (b.clip.simIndex >= 0 && b.clip.simIndex < manager.simulations.Count)
                    ? manager.simulations[b.clip.simIndex] : null;
                if (sim == null || sim.LiveParamSet == null) continue;
                var live = sim.LiveParamSet;

                if (b.from == null)   // first weighted frame → capture "from"
                {
                    b.from = new Dictionary<string, float[]>();
                    foreach (var name in sim.ModulatableParams)
                    {
                        var arr = new float[live.TypeCount];
                        for (int t = 0; t < live.TypeCount; t++)
                            arr[t] = live.GetValue(name, t);
                        b.from[name] = arr;
                    }
                }

                int typeCount = Mathf.Min(live.TypeCount, target.TypeCount);
                foreach (var kv in b.from)
                {
                    float[] fromArr = kv.Value;
                    for (int t = 0; t < typeCount && t < fromArr.Length; t++)
                    {
                        float to = target.GetValue(kv.Key, t);
                        float v = kv.Key == "hue"
                            ? ParameterInterpolator.LerpHue01(fromArr[t], to, w)
                            : Mathf.Lerp(fromArr[t], to, w);
                        live.SetValue(kv.Key, t, v);
                    }
                }
            }
        }
    }
}
```

- [ ] **Step 2: Manual verification**

1. Timeline: add `Param Snapshot Track`, bind to the MAIN `SimulationManager`. Add a 15 s clip: `snapshot = assets/Snapshots/Physarum_20260711_195316`, `simIndex` = index of PhysarumSim in the manager's simulations list (check inspector), 5 s ease-in.
2. Play → the physarum look morphs into the snapshot over 5 s and holds. Scrub before the clip → params snap back only when the clip re-enters (captures a new "from" — expected behavior, sims are forward-only).
3. Set `snapshot` to a non-IParamSet asset (e.g. a Material) → single console warning, no errors.

- [ ] **Step 3: Commit**

```bash
cd /Users/toka/Developer/Graphics/EoC-biomes-compute
git add -A "Assets/Workspace/11.0 Biomes/src/components/sequencer"
git commit -m "sequencer: ParamSnapshotTrack — eased live-param morph to snapshot assets"
```

---

### Task 8: RoutingTrack + diffusion return receiver + composer send

**Files:**
- Create: `Assets/Workspace/11.0 Biomes/src/components/sequencer/tracks/RoutingClip.cs`
- Create: `Assets/Workspace/11.0 Biomes/src/components/sequencer/tracks/RoutingTrack.cs`
- Create: `Assets/Workspace/11.0 Biomes/src/components/sequencer/tracks/RoutingMixer.cs`
- Modify: `Assets/Workspace/11.0 Biomes/src/components/network/ExternalTextureSender.cs:8` (enum) and `:102-125` (ResolveSource)

**Interfaces:**
- Consumes: `SimulationManager.influenceOverride` (Task 4), `CompositeSequencer.{ResolveSource, ComposerOutputTexture, diffusionReturn, inputReceiver}`, `CellSource`.
- Produces:
  - `SendSource.ComposerOutput` enum member (appended — keeps existing serialized values stable) + `ExternalTextureSender.sequencer` field.
  - `RoutingClip { CellSource influenceSource; }` on `RoutingTrack` (binds `CompositeSequencer`): while active, sims' `externalInfluenceTex` = that source; no active clip → normal receiver path.

- [ ] **Step 1: Extend the sender**

In `ExternalTextureSender.cs` line 8:

```csharp
    public enum SendSource { CompositeOutput, SimOutput, BiomeLayer, ComposerOutput }
```

Add field under `public SimulationManager simManager;`:

```csharp
        [Tooltip("For SendSource.ComposerOutput — the Temporal Composer's output.")]
        public CompositeSequencer sequencer;
```

Add a case in `ResolveSource` before `default:`:

```csharp
                case SendSource.ComposerOutput:
                    return sequencer != null ? sequencer.ComposerOutputTexture : null;
```

And in `DefaultName`, add before `_ =>`:

```csharp
            SendSource.ComposerOutput => "EoC/Composer",
```

- [ ] **Step 2: Write routing track/clip/mixer**

`Assets/Workspace/11.0 Biomes/src/components/sequencer/tracks/RoutingClip.cs`:

```csharp
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace Biomes
{
    /// <summary>While active, feeds the chosen texture into every sim's
    /// externalInfluenceTex (via SimulationManager.influenceOverride) so e.g. the
    /// StreamDiffusion return perturbs sim behavior for a timed section.</summary>
    public class RoutingClip : PlayableAsset, ITimelineClipAsset
    {
        public CellSource influenceSource = CellSource.DiffusionReturn;

        public ClipCaps clipCaps => ClipCaps.None;

        public override Playable CreatePlayable(PlayableGraph graph, GameObject owner)
        {
            var playable = ScriptPlayable<RoutingBehaviour>.Create(graph);
            playable.GetBehaviour().clip = this;
            return playable;
        }
    }

    public class RoutingBehaviour : PlayableBehaviour
    {
        public RoutingClip clip;
    }
}
```

`Assets/Workspace/11.0 Biomes/src/components/sequencer/tracks/RoutingTrack.cs`:

```csharp
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace Biomes
{
    [TrackColor(0.9f, 0.8f, 0.2f)]
    [TrackClipType(typeof(RoutingClip))]
    [TrackBindingType(typeof(CompositeSequencer))]
    public class RoutingTrack : TrackAsset
    {
        public override Playable CreateTrackMixer(PlayableGraph graph, GameObject go, int inputCount)
            => ScriptPlayable<RoutingMixer>.Create(graph, inputCount);
    }
}
```

`Assets/Workspace/11.0 Biomes/src/components/sequencer/tracks/RoutingMixer.cs`:

```csharp
using UnityEngine;
using UnityEngine.Playables;

namespace Biomes
{
    /// <summary>Sets/clears SimulationManager.influenceOverride from the active routing
    /// clip. Cleared every frame first, so no clip = normal externalInput path.</summary>
    public class RoutingMixer : PlayableBehaviour
    {
        public override void ProcessFrame(Playable playable, FrameData info, object playerData)
        {
            var seq = playerData as CompositeSequencer;
            if (seq == null || seq.simManager == null) return;

            seq.simManager.influenceOverride = null;

            int n = playable.GetInputCount();
            for (int i = 0; i < n; i++)
            {
                if (playable.GetInputWeight(i) <= 0f) continue;
                var b = ((ScriptPlayable<RoutingBehaviour>)playable.GetInput(i)).GetBehaviour();
                if (b.clip == null) continue;
                seq.simManager.influenceOverride = seq.ResolveSource(b.clip.influenceSource, null);
                break;   // first active clip wins
            }
        }
    }
}
```

- [ ] **Step 3: Scene wiring (manual)**

1. On the `NetworkIO`/`TextureIO` GameObject: add a SECOND `ExternalTextureReceiver` component → `enableReceive = ON`, `protocol = Syphon` (mac dev) / `Spout` (show machine), `streamName = "TD_Diffusion"`, `selfDrive = ON`. Assign it to `CompositeSequencer.diffusionReturn`. Assign the existing first receiver to `CompositeSequencer.inputReceiver`.
2. On `ExternalTextureSender`: add a stream — `source = ComposerOutput`, `protocol` = Syphon (mac) / Spout (show), name left blank (`EoC/Composer`); assign the `sequencer` field.
3. Timeline: add `Routing Track` bound to `CompositeSequencer`; add a 10 s `RoutingClip` with `influenceSource = DiffusionReturn`.
4. Verify without TD: Play → no errors; while the routing clip is active and the diffusion receiver has no stream, `influenceOverride` stays null-texture → sims run normally (receiver OutputTexture null → ResolveSource returns null → override null). Patches with `sourceB = DiffusionReturn` degrade to sourceA (Task 6 fallback).
5. Verify loop (mac, optional): in TD (or any Syphon test sender) publish a stream named `TD_Diffusion` → the cell/patch clips using `DiffusionReturn` now show it, and during the routing clip the sims visibly react to it. Confirm `EoC/Composer` appears as a Syphon source in TD.

- [ ] **Step 4: Commit**

```bash
cd /Users/toka/Developer/Graphics/EoC-biomes-compute
git add -A "Assets/Workspace/11.0 Biomes/src/components" "Assets/Workspace/11.2 SIGGRAPH Scene"
git commit -m "sequencer: RoutingTrack (influence override), diffusion-return receiver, composer send stream"
```

---

### Task 9: Biome Palette window + thumbnail cache

**Files:**
- Create: `Assets/Workspace/11.0 Biomes/src/Editor/sequencer/SnapshotThumbnailCache.cs`
- Create: `Assets/Workspace/11.0 Biomes/src/Editor/sequencer/BiomePaletteWindow.cs`

**Interfaces:**
- Consumes: `IParamSet` (to filter assets), `CompositeSequencer.ComposerOutputTexture`, `ParamSnapshotTrack`/`ParamSnapshotClip` (to insert clips), `UnityEditor.Timeline.TimelineEditor.inspectedDirector`.
- Produces: `Biomes → Biome Palette` menu window; thumbnails saved as `<AssetName>_thumb.png` next to each asset.

- [ ] **Step 1: Write the thumbnail cache**

`Assets/Workspace/11.0 Biomes/src/Editor/sequencer/SnapshotThumbnailCache.cs`:

```csharp
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Biomes.EditorTools
{
    /// <summary>Thumbnails for param snapshot assets: a PNG named "&lt;asset&gt;_thumb.png"
    /// sitting next to the asset. Captured from the live composer output on demand.</summary>
    public static class SnapshotThumbnailCache
    {
        private static readonly Dictionary<string, Texture2D> s_Cache = new();

        public static string ThumbPath(Object asset)
        {
            string assetPath = AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(assetPath)) return null;
            return Path.Combine(Path.GetDirectoryName(assetPath),
                Path.GetFileNameWithoutExtension(assetPath) + "_thumb.png");
        }

        public static Texture2D Get(Object asset)
        {
            string path = ThumbPath(asset);
            if (path == null) return null;
            if (s_Cache.TryGetValue(path, out var tex) && tex != null) return tex;
            tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            s_Cache[path] = tex;
            return tex;
        }

        /// <summary>Downscale + save the current composer output as the asset's thumb.</summary>
        public static void Capture(Object asset, RenderTexture composer, int thumbHeight = 128)
        {
            string path = ThumbPath(asset);
            if (path == null || composer == null) return;

            int w = Mathf.Max(32, thumbHeight * composer.width / composer.height);
            var scaled = RenderTexture.GetTemporary(w, thumbHeight, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(composer, scaled);

            var prev = RenderTexture.active;
            RenderTexture.active = scaled;
            var tex = new Texture2D(w, thumbHeight, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, w, thumbHeight), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(scaled);

            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path);
            s_Cache.Remove(path);
        }
    }
}
```

- [ ] **Step 2: Write the palette window**

`Assets/Workspace/11.0 Biomes/src/Editor/sequencer/BiomePaletteWindow.cs`:

```csharp
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Timeline;

namespace Biomes.EditorTools
{
    /// <summary>Grid of all IParamSet preset/snapshot assets with cached thumbnails.
    /// "Capture" saves a thumb from the live composer; "Insert" drops a ParamSnapshotClip
    /// at the inspected Timeline's playhead. Assets can also be dragged from here.</summary>
    public class BiomePaletteWindow : EditorWindow
    {
        private const int ThumbH = 96;
        private Vector2 _scroll;
        private readonly List<ScriptableObject> _assets = new();
        private int _simIndex;

        [MenuItem("Biomes/Biome Palette")]
        public static void Open() => GetWindow<BiomePaletteWindow>("Biome Palette");

        void OnEnable() => Refresh();

        private void Refresh()
        {
            _assets.Clear();
            foreach (string guid in AssetDatabase.FindAssets("t:ScriptableObject",
                new[] { "Assets/Workspace/11.2 SIGGRAPH Scene/assets",
                        "Assets/Workspace/11.1 CURRENTS Scene/assets" }))
            {
                var so = AssetDatabase.LoadAssetAtPath<ScriptableObject>(
                    AssetDatabase.GUIDToAssetPath(guid));
                if (so is IParamSet) _assets.Add(so);
            }
        }

        void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button("Refresh", EditorStyles.toolbarButton)) Refresh();
                GUILayout.FlexibleSpace();
                GUILayout.Label("simIndex");
                _simIndex = EditorGUILayout.IntField(_simIndex, GUILayout.Width(32));
            }

            var seq = FindFirstObjectByType<CompositeSequencer>();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            int cols = Mathf.Max(1, (int)(position.width / (ThumbH * 2f)));
            int col = 0;
            EditorGUILayout.BeginHorizontal();
            foreach (var asset in _assets)
            {
                if (col++ >= cols) { EditorGUILayout.EndHorizontal(); EditorGUILayout.BeginHorizontal(); col = 1; }
                DrawTile(asset, seq);
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndScrollView();
        }

        private void DrawTile(ScriptableObject asset, CompositeSequencer seq)
        {
            using (new EditorGUILayout.VerticalScope(GUI.skin.box, GUILayout.Width(ThumbH * 2f - 12)))
            {
                var thumb = SnapshotThumbnailCache.Get(asset);
                var rect = GUILayoutUtility.GetRect(ThumbH * 2f - 20, ThumbH * 0.6f);
                if (thumb != null) GUI.DrawTexture(rect, thumb, ScaleMode.ScaleAndCrop);
                else EditorGUI.DrawRect(rect, new Color(0.15f, 0.15f, 0.15f));

                // Drag support: start a normal object drag from the tile.
                var e = Event.current;
                if (e.type == EventType.MouseDrag && rect.Contains(e.mousePosition))
                {
                    DragAndDrop.PrepareStartDrag();
                    DragAndDrop.objectReferences = new Object[] { asset };
                    DragAndDrop.StartDrag(asset.name);
                    e.Use();
                }

                GUILayout.Label(asset.name, EditorStyles.miniLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(seq == null || seq.ComposerOutputTexture == null))
                        if (GUILayout.Button("Capture", EditorStyles.miniButton))
                            SnapshotThumbnailCache.Capture(asset, seq.ComposerOutputTexture);
                    using (new EditorGUI.DisabledScope(TimelineEditor.inspectedDirector == null))
                        if (GUILayout.Button("Insert", EditorStyles.miniButton))
                            InsertClip(asset);
                }
            }
        }

        private void InsertClip(ScriptableObject asset)
        {
            var director = TimelineEditor.inspectedDirector;
            var timeline = director.playableAsset as TimelineAsset;
            if (timeline == null) return;

            ParamSnapshotTrack track = null;
            foreach (var t in timeline.GetOutputTracks())
                if (t is ParamSnapshotTrack pst) { track = pst; break; }
            if (track == null)
            {
                track = timeline.CreateTrack<ParamSnapshotTrack>(null, "Param Snapshots");
                director.SetGenericBinding(track,
                    FindFirstObjectByType<SimulationManager>());
            }

            var clip = track.CreateClip<ParamSnapshotClip>();
            clip.start = director.time;
            clip.duration = 10;
            clip.displayName = asset.name;
            var payload = (ParamSnapshotClip)clip.asset;
            payload.snapshot = asset;
            payload.simIndex = _simIndex;

            EditorUtility.SetDirty(timeline);
            TimelineEditor.Refresh(RefreshReason.ContentsAddedOrRemoved);
        }
    }
}
```

- [ ] **Step 3: Manual verification**

1. `Biomes → Biome Palette` opens; grid lists the 11.2 + 11.1 preset/snapshot assets (dark placeholder tiles — no thumbs yet).
2. Play mode with composer running → select a tile → `Capture` → a `<name>_thumb.png` appears next to the asset and the tile now shows it.
3. With the Timeline window open on `ShowSequence`: `Insert` → a 10 s ParamSnapshotClip appears at the playhead with the asset assigned. Play across it → params morph.
4. Drag a tile onto the Project window — the drag carries the asset (standard object drag).

- [ ] **Step 4: Commit**

```bash
cd /Users/toka/Developer/Graphics/EoC-biomes-compute
git add -A "Assets/Workspace/11.0 Biomes/src/Editor/sequencer" "Assets/Workspace/11.2 SIGGRAPH Scene" "Assets/Workspace/11.1 CURRENTS Scene"
git commit -m "sequencer: Biome Palette window — thumbnail grid, capture, insert-at-playhead"
```

---

### Task 10: Show assembly — cells ×2–4, perf validation, README/ARCHITECTURE

**Files:**
- Modify: `Assets/Workspace/11.2 SIGGRAPH Scene/Scene_SIGGRAPH.unity` (scene wiring)
- Modify: `README.md`, `docs/ARCHITECTURE.md`

**Interfaces:**
- Consumes: everything above.
- Produces: a playable `ShowSequence` demonstrating every track type; docs updated (project rule: README before push, ARCHITECTURE on main merges).

- [ ] **Step 1: Build out the show scene**

1. Duplicate `CellRig_A` → `CellRig_B` (different snapshot preset, e.g. a Termite or Boid variant). Optionally C/D later — spec caps at 4.
2. In `ShowSequence`, author a ~90 s demonstration pass:
   - 0–20 s: main composite only; ParamSnapshotClip morphs physarum to `Physarum_20260711_195316` (10 s ease).
   - 15–45 s: BiomeCellClip A (Overlay, left band) and B (Overlay, right band) fade in 3 s, hold, fade out.
   - 40–70 s: PatchScatterClip (`sourceA = MainComposite`, `sourceB = DiffusionReturn`, crossfadeCenter 0.5) — patches dissolve from raw sim to diffusion over the clip.
   - 55–65 s: RoutingClip (`DiffusionReturn` feeds sims) + `Sig_ResetTermites` emitter at 60 s.
   - 70–90 s: BiomeCellClip A `mode = Replace`, `duckBase = ON`, full-frame `dstRect (0,0,1,1)` — cell takes over the output, then fades back.
3. Play the full pass in the editor. Expected: every transition lands; Console clean.

- [ ] **Step 2: Perf validation (mac dev numbers; re-check on show machine)**

1. Window → Analysis → Profiler. Play the 15–45 s section (2 live cells + main sim).
2. Record: GPU ms/frame (or CPU `Gfx.WaitForPresent` as proxy), `CompositeSequencer.LateUpdate` CPU ms, GC Alloc/frame.
3. Acceptance: GC Alloc ≈ 0 B/frame in steady state (first frame of each clip may allocate — events build lazily); `CompositeSequencer.LateUpdate` < 0.5 ms CPU; overall fps ≥ `targetFPS` × 0.9 with 2 cells running.
4. If cells are too heavy: drop rig `rezX/rezY` to 768 or `cellRate` to 12 — note chosen values in the scene.

- [ ] **Step 3: Update docs**

- `README.md`: add a "Temporal Composer" bullet block under the 11.2 SIGGRAPH section — what it is (Timeline-driven show sequencer), the 5 track types, the composer output texture + `EoC/Composer` send stream, the `TD_Diffusion` return stream name, and the Biome Palette menu path.
- `docs/ARCHITECTURE.md`: add a section describing the data flow (spec's diagram), the stable-RT rule applying to `composerOutTex`, the pure-logic `Biomes.Sequencer.Core` assembly and its test suite, and the deviations noted in this plan's Global Constraints.

- [ ] **Step 4: Full test suite + commit**

Run all edit-mode tests once more (Test Runner → Run All). Expected: all green.

```bash
cd /Users/toka/Developer/Graphics/EoC-biomes-compute
git add -A
git commit -m "sequencer: SIGGRAPH show sequence (cells, patches, routing, resets), perf-validated; docs"
```

---

## Self-Review (completed)

- **Spec coverage:** sims visibility fade/switch → cell clips w/ MainComposite source + base duck + existing `compositeWeight` (untouched); param snapshots → Task 7; resets → Task 5 signals; routing → Task 8; 2–4 live cells overlay/replace → Tasks 4/5/10; scattered diffusion patches → Tasks 2/6; composer own RT + rez scale → Task 3; ScreenLayout/sender re-point → Task 3 (`composerOutMat`) + Task 8 (`SendSource.ComposerOutput`); palette + thumbnails → Task 9; failure handling (null textures skip, non-IParamSet warns, missing rig skips) → Tasks 5–8; scrub semantics → `PatchSweep` rewind + snapshot "from" recapture; testing → Tasks 1/2 + manual steps; perf budget → Task 10.
- **Placeholder scan:** no TBDs; every code step carries full code; manual steps give exact clicks and expected results.
- **Type consistency:** `CellSource`/`CellBlendMode` defined once (Tasks 5/3) and consumed by Tasks 6/8; `PushCell/PushPatch/ResolveSource/SetBaseWeight` signatures match across Tasks 3→5→6→8; `PatchEvent`/`PatchSweep.Collect` match Tasks 2→6; `influenceOverride`/`ownsGlobalTiming` match Tasks 4→8.
