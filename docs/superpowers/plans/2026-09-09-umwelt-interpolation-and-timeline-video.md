# Umwelt Interpolation + SimTimeline Debug-Video Cues — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `ParameterInterpolator` crossfades each sim's live `UmweltMapping` between waypoint umwelt assets (union-key reads/writes), and `SimTimeline` can start / pause / stop the `ExternalTextureReceiver` debug clip at authored sim-time seconds so it influences the composition only after that mark.

**Architecture:** Pure key/crossfade math goes into `Biomes.Core` (`core_math/`, NUnit-tested). `SimulationBase` gains a runtime umwelt clone (`liveUmwelt`) that every runtime reader/writer uses instead of the on-disk asset. `ParameterInterpolator` grows a second, index-paired waypoint list and a second `_from` snapshot. `ExternalTextureReceiver` exposes a small video transport; `SimTimeline` calls it on Play/Stop and from three new cue actions.

**Tech Stack:** Unity 6000.3.10f1, C#, Unity Test Framework 1.4.5 (EditMode, NUnit), EasyButtons. Runtime code is in the predefined `Assembly-CSharp` (no asmdef) except `core_math/` = `Biomes.Core` and `sequencer_core/` = `Biomes.Sequencer.Core`.

**Spec:** `docs/superpowers/specs/2026-09-09-umwelt-interpolation-and-timeline-video-design.md`

## Global Constraints

- All paths below are relative to `Assets/Workspace/11.0 Biomes/src/` unless they start with `Assets/` or `docs/`.
- Tests live in `Assets/Tests/EditMode/` (assembly `Biomes.Sequencer.Tests`, references `Biomes.Core` + `Biomes.Sequencer.Core` only — it **cannot** reference `Assembly-CSharp`, so anything you want unit-tested must live in `core_math/`).
- Run tests headless (editor must be closed) with:
  ```bash
  cd /Users/toka/Developer/Graphics/EoC-biomes-compute
  "/Applications/Unity/Hub/Editor/6000.3.10f1/Unity.app/Contents/MacOS/Unity" \
    -batchmode -projectPath "$PWD" -runTests -testPlatform EditMode \
    -testResults "$PWD/TestResults/editmode.xml" -logFile "$PWD/TestResults/editmode.log"
  grep -c 'result="Passed"' TestResults/editmode.xml; grep -c 'result="Failed"' TestResults/editmode.xml
  ```
  Exit code non-zero or any `Failed` = red. If Unity is open, use Window → General → Test Runner → EditMode → Run All instead. Compile errors show in `TestResults/editmode.log` as `error CS`.
- Unity writes a `.meta` next to every new file on import; **commit the `.meta` too**.
- Never write into an on-disk asset at runtime; the interpolator and MFT only touch clones.
- Commit messages: concise, no attributions except the trailer `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.
- Branch: `feat/umwelt-interp-timeline-video` (already created; spec is committed there).
- Key strings (exact):
  - scalars: `permMin`, `permMax`, `metabolicHeat`, `oxygenConsumption`, `deathO2`, `deathPerm`, `corpseWaste`, `corpseDecay`
  - read entry: `read:<channel>:<effect>.weight` (effect = `(int)UmweltEffect`)
  - write entry: `write:<channel>.amount`
  - toggle names in the interpolator: `umwelt.<scalar>`, `umwelt.reads`, `umwelt.writes`

---

## File map

| File | Responsibility |
|---|---|
| `core_math/UmweltKeys.cs` (new) | Key string format/parse; scalar list. Pure. |
| `core_math/KeyedCrossfade.cs` (new) | Union-key lerp over two `IReadOnlyDictionary<string,float>`; keys-to-remove. Pure. |
| `Assets/Tests/EditMode/UmweltKeysTests.cs` (new) | NUnit for `UmweltKeys`. |
| `Assets/Tests/EditMode/KeyedCrossfadeTests.cs` (new) | NUnit for `KeyedCrossfade`. |
| `components/core/UmweltMapping.cs` | `Keys`, `TryGetValue`, `GetValue`, `SetValue`, `RemoveEntry`, `Snapshot`. |
| `components/core/SimulationBase.cs` | `liveUmwelt` clone, `LiveUmwelt`, save-back. |
| `components/core/SimulationManager.cs` | Read `LiveUmwelt`. |
| `components/network/MidiFighterTwister.cs` | Bank 3 bindings resolve `LiveUmwelt` at call time. |
| `components/utils/ParameterInterpolator.cs` | `umweltWaypoints`, `_fromUmwelt`/`_toUmwelt`, umwelt leg, toggles. |
| `components/network/ExternalTextureReceiver.cs` | `debugVideoAutoPlay`, `RestartDebugVideo`, `PauseDebugVideo`, `StopDebugVideo`, RT clear. |
| `components/utils/SimTimeline.cs` | `externalReceiver`, `startDebugVideoOnPlay`, 3 cue actions. |
| `docs/ARCHITECTURE.md` | §3.5 live clone; interpolator + SimTimeline + receiver bullets. |

---

### Task 1: `UmweltKeys` (pure key format) — TDD

**Files:**
- Create: `core_math/UmweltKeys.cs`
- Test: `Assets/Tests/EditMode/UmweltKeysTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  namespace Biomes {
    public static class UmweltKeys {
      public const string PermMin = "permMin", PermMax = "permMax",
        MetabolicHeat = "metabolicHeat", OxygenConsumption = "oxygenConsumption",
        DeathO2 = "deathO2", DeathPerm = "deathPerm",
        CorpseWaste = "corpseWaste", CorpseDecay = "corpseDecay";
      public static readonly string[] Scalars; // the 8 above, in that order
      public static string Read(int channel, int effect);   // "read:{channel}:{effect}.weight"
      public static string Write(int channel);              // "write:{channel}.amount"
      public static bool IsRead(string key);                // starts with "read:"
      public static bool IsWrite(string key);               // starts with "write:"
      public static bool IsScalar(string key);
      public static bool TryParseRead(string key, out int channel, out int effect);
      public static bool TryParseWrite(string key, out int channel);
    }
  }
  ```

- [ ] **Step 1: Write the failing tests**

`Assets/Tests/EditMode/UmweltKeysTests.cs`:
```csharp
using NUnit.Framework;
using Biomes;

public class UmweltKeysTests
{
    [Test]
    public void Read_FormatsChannelAndEffect()
    {
        Assert.That(UmweltKeys.Read(10, 2), Is.EqualTo("read:10:2.weight"));
    }

    [Test]
    public void Write_FormatsChannel()
    {
        Assert.That(UmweltKeys.Write(2), Is.EqualTo("write:2.amount"));
    }

    [Test]
    public void TryParseRead_RoundTrips()
    {
        Assert.That(UmweltKeys.TryParseRead("read:13:3.weight", out int ch, out int fx), Is.True);
        Assert.That(ch, Is.EqualTo(13));
        Assert.That(fx, Is.EqualTo(3));
    }

    [Test]
    public void TryParseWrite_RoundTrips()
    {
        Assert.That(UmweltKeys.TryParseWrite("write:7.amount", out int ch), Is.True);
        Assert.That(ch, Is.EqualTo(7));
    }

    [Test]
    public void TryParse_RejectsWrongShape()
    {
        Assert.That(UmweltKeys.TryParseRead("write:7.amount", out _, out _), Is.False);
        Assert.That(UmweltKeys.TryParseRead("read:x:1.weight", out _, out _), Is.False);
        Assert.That(UmweltKeys.TryParseWrite("read:1:1.weight", out _), Is.False);
        Assert.That(UmweltKeys.TryParseWrite("metabolicHeat", out _), Is.False);
    }

    [Test]
    public void Classifiers_AreExclusive()
    {
        Assert.That(UmweltKeys.IsRead("read:1:0.weight"), Is.True);
        Assert.That(UmweltKeys.IsWrite("read:1:0.weight"), Is.False);
        Assert.That(UmweltKeys.IsScalar("read:1:0.weight"), Is.False);
        Assert.That(UmweltKeys.IsScalar("metabolicHeat"), Is.True);
        Assert.That(UmweltKeys.IsScalar("bogus"), Is.False);
    }

    [Test]
    public void Scalars_HasEightStableNames()
    {
        Assert.That(UmweltKeys.Scalars, Is.EqualTo(new[] {
            "permMin", "permMax", "metabolicHeat", "oxygenConsumption",
            "deathO2", "deathPerm", "corpseWaste", "corpseDecay" }));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run the headless test command from Global Constraints.
Expected: `editmode.log` contains `error CS0103: The name 'UmweltKeys' does not exist` (compile failure = red).

- [ ] **Step 3: Implement `UmweltKeys`**

`core_math/UmweltKeys.cs`:
```csharp
using System;
using System.Globalization;

namespace Biomes
{
    /// <summary>
    /// Flat string keys addressing every interpolatable field of an UmweltMapping, so a
    /// mapping can be snapshotted to a name→float dictionary and crossfaded key-by-key.
    /// Pure (no Unity types) so it is unit-testable from Biomes.Core.
    /// </summary>
    public static class UmweltKeys
    {
        public const string PermMin           = "permMin";
        public const string PermMax           = "permMax";
        public const string MetabolicHeat     = "metabolicHeat";
        public const string OxygenConsumption = "oxygenConsumption";
        public const string DeathO2           = "deathO2";
        public const string DeathPerm         = "deathPerm";
        public const string CorpseWaste       = "corpseWaste";
        public const string CorpseDecay       = "corpseDecay";

        public static readonly string[] Scalars =
        {
            PermMin, PermMax, MetabolicHeat, OxygenConsumption,
            DeathO2, DeathPerm, CorpseWaste, CorpseDecay,
        };

        private const string ReadPrefix   = "read:";
        private const string ReadSuffix   = ".weight";
        private const string WritePrefix  = "write:";
        private const string WriteSuffix  = ".amount";

        public static string Read(int channel, int effect) =>
            ReadPrefix + channel.ToString(CultureInfo.InvariantCulture) + ":" +
            effect.ToString(CultureInfo.InvariantCulture) + ReadSuffix;

        public static string Write(int channel) =>
            WritePrefix + channel.ToString(CultureInfo.InvariantCulture) + WriteSuffix;

        public static bool IsRead(string key)  => key != null && key.StartsWith(ReadPrefix, StringComparison.Ordinal);
        public static bool IsWrite(string key) => key != null && key.StartsWith(WritePrefix, StringComparison.Ordinal);

        public static bool IsScalar(string key)
        {
            if (key == null) return false;
            foreach (var s in Scalars) if (s == key) return true;
            return false;
        }

        public static bool TryParseRead(string key, out int channel, out int effect)
        {
            channel = 0; effect = 0;
            if (!IsRead(key) || !key.EndsWith(ReadSuffix, StringComparison.Ordinal)) return false;
            string body = key.Substring(ReadPrefix.Length, key.Length - ReadPrefix.Length - ReadSuffix.Length);
            int colon = body.IndexOf(':');
            if (colon <= 0 || colon == body.Length - 1) return false;
            return int.TryParse(body.Substring(0, colon), NumberStyles.Integer, CultureInfo.InvariantCulture, out channel)
                && int.TryParse(body.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out effect);
        }

        public static bool TryParseWrite(string key, out int channel)
        {
            channel = 0;
            if (!IsWrite(key) || !key.EndsWith(WriteSuffix, StringComparison.Ordinal)) return false;
            string body = key.Substring(WritePrefix.Length, key.Length - WritePrefix.Length - WriteSuffix.Length);
            return int.TryParse(body, NumberStyles.Integer, CultureInfo.InvariantCulture, out channel);
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run the headless test command. Expected: exit 0, `Failed` count 0, `UmweltKeysTests` all `Passed` in `TestResults/editmode.xml`.

- [ ] **Step 5: Commit**

```bash
cd /Users/toka/Developer/Graphics/EoC-biomes-compute
git add "Assets/Workspace/11.0 Biomes/src/core_math/UmweltKeys.cs" "Assets/Workspace/11.0 Biomes/src/core_math/UmweltKeys.cs.meta" \
        Assets/Tests/EditMode/UmweltKeysTests.cs Assets/Tests/EditMode/UmweltKeysTests.cs.meta
git commit -m "feat(core): UmweltKeys — flat key format/parse for umwelt fields

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 2: `KeyedCrossfade` (pure union-key lerp) — TDD

**Files:**
- Create: `core_math/KeyedCrossfade.cs`
- Test: `Assets/Tests/EditMode/KeyedCrossfadeTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  namespace Biomes {
    public static class KeyedCrossfade {
      // All keys present in either dictionary, each once, deterministic order (from's keys first, then to-only keys).
      public static List<string> UnionKeys(IReadOnlyDictionary<string,float> from, IReadOnlyDictionary<string,float> to);
      // Lerp for one key; a side missing the key counts as 0.
      public static float Lerp(IReadOnlyDictionary<string,float> from, IReadOnlyDictionary<string,float> to, string key, float t);
      // Keys in `from` but not in `to` — the ones to delete once the leg completes.
      public static List<string> KeysToRemove(IReadOnlyDictionary<string,float> from, IReadOnlyDictionary<string,float> to);
    }
  }
  ```

- [ ] **Step 1: Write the failing tests**

`Assets/Tests/EditMode/KeyedCrossfadeTests.cs`:
```csharp
using System.Collections.Generic;
using NUnit.Framework;
using Biomes;

public class KeyedCrossfadeTests
{
    private const float Eps = 1e-5f;

    private static Dictionary<string, float> D(params (string k, float v)[] kv)
    {
        var d = new Dictionary<string, float>();
        foreach (var (k, v) in kv) d[k] = v;
        return d;
    }

    [Test]
    public void UnionKeys_FromFirst_ThenToOnly_NoDuplicates()
    {
        var from = D(("a", 1f), ("b", 2f));
        var to   = D(("b", 5f), ("c", 3f));
        Assert.That(KeyedCrossfade.UnionKeys(from, to), Is.EqualTo(new[] { "a", "b", "c" }));
    }

    [Test]
    public void Lerp_BothSides_IsPlainLerp()
    {
        var from = D(("k", 2f));
        var to   = D(("k", 4f));
        Assert.That(KeyedCrossfade.Lerp(from, to, "k", 0.5f), Is.EqualTo(3f).Within(Eps));
        Assert.That(KeyedCrossfade.Lerp(from, to, "k", 0f),   Is.EqualTo(2f).Within(Eps));
        Assert.That(KeyedCrossfade.Lerp(from, to, "k", 1f),   Is.EqualTo(4f).Within(Eps));
    }

    [Test]
    public void Lerp_OnlyInTarget_FadesInFromZero()
    {
        var from = D();
        var to   = D(("k", 2f));
        Assert.That(KeyedCrossfade.Lerp(from, to, "k", 0.25f), Is.EqualTo(0.5f).Within(Eps));
    }

    [Test]
    public void Lerp_OnlyInFrom_FadesOutToZero()
    {
        var from = D(("k", -1f));
        var to   = D();
        Assert.That(KeyedCrossfade.Lerp(from, to, "k", 0.75f), Is.EqualTo(-0.25f).Within(Eps));
        Assert.That(KeyedCrossfade.Lerp(from, to, "k", 1f),    Is.EqualTo(0f).Within(Eps));
    }

    [Test]
    public void Lerp_ClampsT()
    {
        var from = D(("k", 0f));
        var to   = D(("k", 1f));
        Assert.That(KeyedCrossfade.Lerp(from, to, "k", 1.5f),  Is.EqualTo(1f).Within(Eps));
        Assert.That(KeyedCrossfade.Lerp(from, to, "k", -0.5f), Is.EqualTo(0f).Within(Eps));
    }

    [Test]
    public void KeysToRemove_IsFromMinusTo()
    {
        var from = D(("a", 1f), ("b", 2f));
        var to   = D(("b", 5f), ("c", 3f));
        Assert.That(KeyedCrossfade.KeysToRemove(from, to), Is.EqualTo(new[] { "a" }));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run the headless test command. Expected: `error CS0103: The name 'KeyedCrossfade' does not exist`.

- [ ] **Step 3: Implement `KeyedCrossfade`**

`core_math/KeyedCrossfade.cs`:
```csharp
using System.Collections.Generic;

namespace Biomes
{
    /// <summary>
    /// Crossfade between two name→float snapshots whose key sets may differ (umwelt
    /// read/write entries come and go between waypoints). A key missing on one side counts
    /// as 0, so new entries fade in from 0 and dropped entries fade out to 0.
    /// Pure — unit-tested in Biomes.Core.
    /// </summary>
    public static class KeyedCrossfade
    {
        public static List<string> UnionKeys(IReadOnlyDictionary<string, float> from,
                                             IReadOnlyDictionary<string, float> to)
        {
            var keys = new List<string>(from.Count + to.Count);
            foreach (var k in from.Keys) keys.Add(k);
            foreach (var k in to.Keys) if (!from.ContainsKey(k)) keys.Add(k);
            return keys;
        }

        public static float Lerp(IReadOnlyDictionary<string, float> from,
                                 IReadOnlyDictionary<string, float> to,
                                 string key, float t)
        {
            if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
            float a = from.TryGetValue(key, out float fa) ? fa : 0f;
            float b = to.TryGetValue(key, out float tb) ? tb : 0f;
            return a + (b - a) * t;
        }

        public static List<string> KeysToRemove(IReadOnlyDictionary<string, float> from,
                                                IReadOnlyDictionary<string, float> to)
        {
            var gone = new List<string>();
            foreach (var k in from.Keys) if (!to.ContainsKey(k)) gone.Add(k);
            return gone;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run the headless test command. Expected: exit 0, `Failed` count 0, all `KeyedCrossfadeTests` `Passed`.

- [ ] **Step 5: Commit**

```bash
cd /Users/toka/Developer/Graphics/EoC-biomes-compute
git add "Assets/Workspace/11.0 Biomes/src/core_math/KeyedCrossfade.cs" "Assets/Workspace/11.0 Biomes/src/core_math/KeyedCrossfade.cs.meta" \
        Assets/Tests/EditMode/KeyedCrossfadeTests.cs Assets/Tests/EditMode/KeyedCrossfadeTests.cs.meta
git commit -m "feat(core): KeyedCrossfade — union-key lerp, missing side = 0

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 3: `UmweltMapping` key access

**Files:**
- Modify: `components/core/UmweltMapping.cs` (whole file is 54 lines; add members inside the `UmweltMapping` class after `corpseDecayRate`)

**Interfaces:**
- Consumes: `UmweltKeys` (Task 1).
- Produces (on `UmweltMapping`):
  ```csharp
  public IEnumerable<string> Keys { get; }                 // 8 scalars + one key per read entry + one per write entry
  public bool TryGetValue(string key, out float value);
  public float GetValue(string key);                       // 0 when missing
  public void SetValue(string key, float value);           // adds a read/write entry when the key is missing
  public bool RemoveEntry(string key);                     // read/write keys only
  public void Snapshot(Dictionary<string, float> into);    // clears `into`, fills from Keys
  ```

- [ ] **Step 1: Add the members**

Append inside the class body (after the Lifecycle fields):
```csharp
        // ─────────── Flat key access (ParameterInterpolator) ───────────
        // Keys per UmweltKeys: 8 scalars, "read:<ch>:<effect>.weight", "write:<ch>.amount".

        public IEnumerable<string> Keys
        {
            get
            {
                foreach (var s in UmweltKeys.Scalars) yield return s;
                foreach (var r in reads)  yield return UmweltKeys.Read(r.channel, (int)r.effect);
                foreach (var w in writes) yield return UmweltKeys.Write(w.channel);
            }
        }

        public bool TryGetValue(string key, out float value)
        {
            value = 0f;
            switch (key)
            {
                case UmweltKeys.PermMin:           value = preferredPermeabilityMin;  return true;
                case UmweltKeys.PermMax:           value = preferredPermeabilityMax;  return true;
                case UmweltKeys.MetabolicHeat:     value = metabolicHeat;             return true;
                case UmweltKeys.OxygenConsumption: value = oxygenConsumption;         return true;
                case UmweltKeys.DeathO2:           value = deathThresholdOxygen;      return true;
                case UmweltKeys.DeathPerm:         value = deathThresholdPermeability; return true;
                case UmweltKeys.CorpseWaste:       value = corpseWasteAmount;         return true;
                case UmweltKeys.CorpseDecay:       value = corpseDecayRate;           return true;
            }
            if (UmweltKeys.TryParseRead(key, out int rch, out int rfx))
            {
                var r = FindRead(rch, rfx);
                if (r == null) return false;
                value = r.weight; return true;
            }
            if (UmweltKeys.TryParseWrite(key, out int wch))
            {
                var w = FindWrite(wch);
                if (w == null) return false;
                value = w.amount; return true;
            }
            return false;
        }

        public float GetValue(string key) => TryGetValue(key, out float v) ? v : 0f;

        public void SetValue(string key, float value)
        {
            switch (key)
            {
                case UmweltKeys.PermMin:           preferredPermeabilityMin   = value; return;
                case UmweltKeys.PermMax:           preferredPermeabilityMax   = value; return;
                case UmweltKeys.MetabolicHeat:     metabolicHeat              = value; return;
                case UmweltKeys.OxygenConsumption: oxygenConsumption          = value; return;
                case UmweltKeys.DeathO2:           deathThresholdOxygen       = value; return;
                case UmweltKeys.DeathPerm:         deathThresholdPermeability = value; return;
                case UmweltKeys.CorpseWaste:       corpseWasteAmount          = value; return;
                case UmweltKeys.CorpseDecay:       corpseDecayRate            = value; return;
            }
            if (UmweltKeys.TryParseRead(key, out int rch, out int rfx))
            {
                var r = FindRead(rch, rfx);
                if (r == null)
                {
                    r = new UmweltReadEntry { channel = rch, effect = (UmweltEffect)rfx };
                    reads.Add(r);
                }
                r.weight = value;
                return;
            }
            if (UmweltKeys.TryParseWrite(key, out int wch))
            {
                var w = FindWrite(wch);
                if (w == null)
                {
                    w = new UmweltWriteEntry { channel = wch };
                    writes.Add(w);
                }
                w.amount = value;
            }
        }

        public bool RemoveEntry(string key)
        {
            if (UmweltKeys.TryParseRead(key, out int rch, out int rfx))
                return reads.RemoveAll(r => r.channel == rch && (int)r.effect == rfx) > 0;
            if (UmweltKeys.TryParseWrite(key, out int wch))
                return writes.RemoveAll(w => w.channel == wch) > 0;
            return false;
        }

        public void Snapshot(Dictionary<string, float> into)
        {
            into.Clear();
            foreach (var k in Keys) into[k] = GetValue(k);
        }

        private UmweltReadEntry FindRead(int channel, int effect)
        {
            foreach (var r in reads) if (r.channel == channel && (int)r.effect == effect) return r;
            return null;
        }

        private UmweltWriteEntry FindWrite(int channel)
        {
            foreach (var w in writes) if (w.channel == channel) return w;
            return null;
        }
```

- [ ] **Step 2: Compile check**

Run the headless test command (it compiles `Assembly-CSharp` too). Expected: exit 0, no `error CS` in `TestResults/editmode.log`, all prior tests still `Passed`.

- [ ] **Step 3: Commit**

```bash
cd /Users/toka/Developer/Graphics/EoC-biomes-compute
git add "Assets/Workspace/11.0 Biomes/src/components/core/UmweltMapping.cs"
git commit -m "feat(umwelt): flat key access — Keys/TryGetValue/SetValue/RemoveEntry/Snapshot

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 4: Live umwelt clone on `SimulationBase` + all runtime readers

**Files:**
- Modify: `components/core/SimulationBase.cs:94-95` (field), `:225-245` (`SaveLiveParamsToPreset`), `:299-312` (`Reset`)
- Modify: `components/core/SimulationManager.cs:319-322`, `:361`, `:366-390`
- Modify: `components/network/MidiFighterTwister.cs:330-345`

**Interfaces:**
- Produces (on `SimulationBase`):
  ```csharp
  [NonSerialized] public UmweltMapping liveUmwelt;                 // runtime clone, re-made on Reset
  public UmweltMapping LiveUmwelt => liveUmwelt != null ? liveUmwelt : umwelt;
  ```

- [ ] **Step 1: `SimulationBase` — field, clone, save-back**

Replace lines 94-95:
```csharp
        [Header("Biome Integration")]
        [Tooltip("Assigned umwelt preset. Runtime reads/writes go to liveUmwelt (a clone made on Reset), never this asset.")]
        public UmweltMapping umwelt;

        /// <summary>Runtime clone of <see cref="umwelt"/>, re-created on every Reset (like agentParams
        /// from paramsSO). MFT knobs and ParameterInterpolator write here so the asset stays pristine.</summary>
        [NonSerialized] public UmweltMapping liveUmwelt;

        /// <summary>What the runtime should read: the clone when present, else the asset (pre-Reset).</summary>
        public UmweltMapping LiveUmwelt => liveUmwelt != null ? liveUmwelt : umwelt;
```

In `Reset()` (line ~299), insert as the first statements after `_simStep = 0;`:
```csharp
            // Runtime umwelt clone — mirrors agentParams: re-cloned from the pristine asset each
            // Reset so interpolation / MFT edits never touch disk and never compound. (Not
            // Destroy()ed — Reset is also a [Button] in edit mode, and agentParams sets the precedent.)
            liveUmwelt = umwelt != null ? Instantiate(umwelt) : null;
            if (liveUmwelt != null) liveUmwelt.name = umwelt.name + " (live)";
```

In `SaveLiveParamsToPreset()` inside the `#if UNITY_EDITOR` block, replace the body so umwelt is saved too:
```csharp
#if UNITY_EDITOR
            bool wrote = false;
            var live = LiveParamSet as ScriptableObject;
            var preset = PresetParamSet;
            if (live != null && preset != null)
            {
                string presetName = preset.name;                        // CopySerialized would stamp "(Clone)";
                UnityEditor.EditorUtility.CopySerialized(live, preset);  // copy all tuned fields into the asset
                preset.name = presetName;                               // restore the asset's name
                UnityEditor.EditorUtility.SetDirty(preset);
                wrote = true;
            }
            if (liveUmwelt != null && umwelt != null)
            {
                string umweltName = umwelt.name;
                UnityEditor.EditorUtility.CopySerialized(liveUmwelt, umwelt);
                umwelt.name = umweltName;
                UnityEditor.EditorUtility.SetDirty(umwelt);
                wrote = true;
            }
            return wrote;
#else
            return false;
#endif
```

- [ ] **Step 2: `SimulationManager` — read the clone**

Lines 319-322 become:
```csharp
                    var u = sim != null ? sim.LiveUmwelt : null;
                    if (sim == null || sim.runState != SimRunState.Running
                        || u == null || sim.perceptionTex == null) continue;
                    biome.BuildPerceptionTex(sim.perceptionTex, u,
                        sim.perceptionTex.width, sim.perceptionTex.height);
```
Line 361 becomes:
```csharp
                    var u = sim != null ? sim.LiveUmwelt : null;
                    if (sim == null || sim.runState != SimRunState.Running || u == null) continue;
```
Then in lines 366-390 replace every `sim.umwelt.` with `u.` (six occurrences: `writes` ×2, `metabolicHeat` ×2 pairs, `oxygenConsumption` ×2 pairs). Verify with:
```bash
grep -n 'sim\.umwelt' "Assets/Workspace/11.0 Biomes/src/components/core/SimulationManager.cs"
```
Expected: no output.

- [ ] **Step 3: `MidiFighterTwister` — resolve at call time**

Lines 333-345: replace `var u = sim.umwelt;` and the four bindings with:
```csharp
                if (sim == null || sim.umwelt == null) continue;
                var s = sim; // resolve the live clone at call time — Reset swaps it
                int globalCol = col < 3 ? col : col + 1; // skip col 3 (globals)

                bindings[ColRowToEncoderIdx(globalCol, 0)] = MakeUmweltBinding($"{sim.SimName[0]}.metHeat",
                    v => s.LiveUmwelt.metabolicHeat = v, () => s.LiveUmwelt.metabolicHeat, 0f, 0.1f);
                bindings[ColRowToEncoderIdx(globalCol, 1)] = MakeUmweltBinding($"{sim.SimName[0]}.O2cons",
                    v => s.LiveUmwelt.oxygenConsumption = v, () => s.LiveUmwelt.oxygenConsumption, 0f, 0.1f);
                bindings[ColRowToEncoderIdx(globalCol, 2)] = MakeUmweltBinding($"{sim.SimName[0]}.permMin",
                    v => s.LiveUmwelt.preferredPermeabilityMin = v, () => s.LiveUmwelt.preferredPermeabilityMin, 0f, 1f);
                bindings[ColRowToEncoderIdx(globalCol, 3)] = MakeUmweltBinding($"{sim.SimName[0]}.permMax",
                    v => s.LiveUmwelt.preferredPermeabilityMax = v, () => s.LiveUmwelt.preferredPermeabilityMax, 0f, 1f);
```
(`LiveUmwelt` falls back to the asset before the first Reset, so the getter never null-derefs while `sim.umwelt != null`.)

- [ ] **Step 4: Compile + grep for stragglers**

```bash
cd /Users/toka/Developer/Graphics/EoC-biomes-compute
grep -rn '\.umwelt\b' "Assets/Workspace/11.0 Biomes/src" --include='*.cs' | grep -v 'sim\.umwelt == null\|LiveUmwelt\|public UmweltMapping umwelt\|umwelt != null\|Instantiate(umwelt)\|umwelt\.name\|liveUmwelt, umwelt'
```
Expected: no runtime reader left (comments only). Then run the headless test command → exit 0, no `error CS`.

- [ ] **Step 5: In-editor check**

Open `Assets/Workspace/11.2 SIGGRAPH Scene/assets/DAC_params/0-minimal-test/_DAC Empty Test.unity`, Play. Select a sim: in Debug inspector `liveUmwelt` is `UmweltBoid_0 (live)`. Turn an MFT Bank 3 knob (or edit `liveUmwelt.metabolicHeat` in the inspector) → stop Play → `git status` shows `UmweltBoid_0.asset` unchanged. Sims still perceive/deposit (composite unchanged vs before).

- [ ] **Step 6: Commit**

```bash
cd /Users/toka/Developer/Graphics/EoC-biomes-compute
git add "Assets/Workspace/11.0 Biomes/src/components/core/SimulationBase.cs" \
        "Assets/Workspace/11.0 Biomes/src/components/core/SimulationManager.cs" \
        "Assets/Workspace/11.0 Biomes/src/components/network/MidiFighterTwister.cs"
git commit -m "feat(sim): runtime umwelt clone (liveUmwelt) — manager/MFT read it; save-to-preset copies it back

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 5: `ParameterInterpolator` umwelt leg

**Files:**
- Modify: `components/utils/ParameterInterpolator.cs` — header fields (`:27-29`), `RefreshParamList` (`:66-82`), `IsEnabled` (`:84-89`), `Update` (`:165-190`), `ApplyLeg` (`:192-219`), `SnapshotFrom` (`:236-249`), private state (`:46-51`)

**Interfaces:**
- Consumes: `UmweltMapping.Snapshot/SetValue/RemoveEntry/enableDeath` (Task 3), `SimulationBase.LiveUmwelt` (Task 4), `KeyedCrossfade`, `UmweltKeys` (Tasks 1-2).
- Produces: `public List<UmweltMapping> umweltWaypoints` (index-paired with `waypoints`).

- [ ] **Step 1: Fields**

After the `waypoints` field (line 29) add:
```csharp
        [Tooltip("Optional umwelt targets, index-paired with waypoints (leg i → umweltWaypoints[i]). " +
                 "Empty slot = umwelt untouched that leg. Reads/writes crossfade by channel key: entries " +
                 "only in the target fade in from 0, entries only in the source fade out and are removed at leg end.")]
        public List<UmweltMapping> umweltWaypoints = new();
```
Next to `_from` (line 47) add:
```csharp
        // umwelt snapshots: flat key -> value (UmweltKeys), taken at each leg start
        private readonly Dictionary<string, float> _fromUmwelt = new();
        private readonly Dictionary<string, float> _toUmwelt = new();
        private bool _umweltLegActive;   // this leg has an umwelt target
        private bool _umweltLegFinished; // end-of-leg cleanup done (remove faded entries, snap bools)
```
Add helpers after `StepNow()`:
```csharp
        private UmweltMapping UmweltTargetFor(int i) =>
            (umweltWaypoints != null && i >= 0 && i < umweltWaypoints.Count) ? umweltWaypoints[i] : null;

        private static string UmweltToggleName(string key) =>
            UmweltKeys.IsRead(key)  ? "umwelt.reads" :
            UmweltKeys.IsWrite(key) ? "umwelt.writes" :
            "umwelt." + key;
```

- [ ] **Step 2: Toggles**

In `RefreshParamList`, after the `foreach (var name in sim.ModulatableParams)` loop, add:
```csharp
            foreach (var s in UmweltKeys.Scalars) AddToggle("umwelt." + s);
            AddToggle("umwelt.reads");
            AddToggle("umwelt.writes");

            void AddToggle(string name) => paramToggles.Add(new ParamToggle
            {
                name = name,
                enabled = prev.TryGetValue(name, out bool e) ? e : true,
            });
```
(`IsEnabled` is unchanged — unlisted names default on.)

- [ ] **Step 3: Snapshot**

Replace `SnapshotFrom()` with:
```csharp
        private void SnapshotFrom()
        {
            _from.Clear();
            var sim = Sim;
            var live = sim.LiveParamSet;
            if (live != null)
            {
                int typeCount = live.TypeCount;
                foreach (var name in sim.ModulatableParams)
                {
                    var arr = new float[typeCount];
                    for (int i = 0; i < typeCount; i++)
                        arr[i] = live.GetValue(name, i);
                    _from[name] = arr;
                }
            }

            // Umwelt leg: snapshot both ends now (target asset is read once per leg).
            _fromUmwelt.Clear();
            _toUmwelt.Clear();
            _umweltLegFinished = false;
            var liveU = sim.LiveUmwelt;
            var targetU = UmweltTargetFor(currentWaypoint);
            _umweltLegActive = liveU != null && targetU != null;
            if (_umweltLegActive)
            {
                liveU.Snapshot(_fromUmwelt);
                targetU.Snapshot(_toUmwelt);
            }
        }
```

- [ ] **Step 4: Apply**

Replace `ApplyLeg` with:
```csharp
        private void ApplyLeg(IParamSet live, float te)
        {
            ApplyParamLeg(live, te);
            ApplyUmweltLeg(te);
        }

        private void ApplyParamLeg(IParamSet live, float te)
        {
            var target = waypoints != null && currentWaypoint < waypoints.Count
                ? waypoints[currentWaypoint] as IParamSet : null;
            if (target == null)
            {
                // Umwelt-only leg is legitimate; warn only when the leg has nothing to do.
                if (!_umweltLegActive && !_warnedWrongType)
                {
                    Debug.LogWarning($"ParameterInterpolator: waypoint {currentWaypoint} is neither an IParamSet preset nor paired with an umwelt waypoint; skipping leg");
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

        private void ApplyUmweltLeg(float te)
        {
            if (!_umweltLegActive) return;
            var liveU = Sim?.LiveUmwelt;
            if (liveU == null) return;
            foreach (var key in KeyedCrossfade.UnionKeys(_fromUmwelt, _toUmwelt))
            {
                if (!IsEnabled(UmweltToggleName(key))) continue;
                liveU.SetValue(key, KeyedCrossfade.Lerp(_fromUmwelt, _toUmwelt, key, te));
            }
        }

        /// <summary>Leg reached t = 1 naturally: drop entries that faded to 0 and snap the
        /// non-lerpable bool. Not called on Skip — a skipped leg re-snapshots from wherever it was.</summary>
        private void FinishUmweltLeg()
        {
            if (!_umweltLegActive || _umweltLegFinished) return;
            _umweltLegFinished = true;
            var liveU = Sim?.LiveUmwelt;
            var targetU = UmweltTargetFor(currentWaypoint);
            if (liveU == null || targetU == null) return;
            foreach (var key in KeyedCrossfade.KeysToRemove(_fromUmwelt, _toUmwelt))
                if (IsEnabled(UmweltToggleName(key))) liveU.RemoveEntry(key);
            liveU.enableDeath = targetU.enableDeath;
        }
```

- [ ] **Step 5: Hook leg completion in `Update`**

In `Update()`, inside `if (phase == Phase.Interpolating)`, change:
```csharp
                if (t >= 1f)
                {
                    if (holdSteps > 0) phase = Phase.Holding;
                    else Advance();
                }
```
to:
```csharp
                if (t >= 1f)
                {
                    FinishUmweltLeg();
                    if (holdSteps > 0) phase = Phase.Holding;
                    else Advance();
                }
```

- [ ] **Step 6: Compile**

Run the headless test command → exit 0, no `error CS`.

- [ ] **Step 7: In-editor verification (spec Part A §Verification)**

1. In `_DAC Empty Test.unity`, duplicate `UmweltBoid_0.asset` → `UmweltBoid_1.asset`; in the copy remove one read entry, add a new read on a different channel, set `metabolicHeat` 0.08.
2. On the Boid `ParameterInterpolator`: `waypoints = [Boid_0]`, `umweltWaypoints = [UmweltBoid_1]`, `durationSteps = 120`, `holdSteps = 0`. Click Refresh Param List → toggles include `umwelt.metabolicHeat`, `umwelt.reads`, `umwelt.writes`.
3. Play mode → Reset sims → Play on the interpolator. Watch `liveUmwelt` in the Debug inspector: `metabolicHeat` sweeps to 0.08; the new read appears immediately at weight 0 and rises; the removed read falls to 0 and is gone when `phase == Done`.
4. `git status` → no umwelt `.asset` modified.
5. Untick `umwelt.metabolicHeat`, Play again → it stays put while the reads move.
6. With a `ParameterInterpolatorGroup` + MFT: turn a Bank 3 knob mid-leg → status shows override; after cooldown it resumes toward the same target from the knob value with no jump.

- [ ] **Step 8: Commit**

```bash
cd /Users/toka/Developer/Graphics/EoC-biomes-compute
git add "Assets/Workspace/11.0 Biomes/src/components/utils/ParameterInterpolator.cs"
git commit -m "feat(interp): umweltWaypoints — union-key crossfade of live umwelt, entries fade in/out, enableDeath snaps at leg end

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 6: `ExternalTextureReceiver` debug-video transport

**Files:**
- Modify: `components/network/ExternalTextureReceiver.cs` — fields (`:20-28`), `InitializeDebugVideoIfNeeded` (`:157-208`), `Release` (`:257-268`), new public methods

**Interfaces:**
- Produces:
  ```csharp
  public bool debugVideoAutoPlay = true;       // serialized; when false the clip waits for RestartDebugVideo()
  public bool DebugUseVideoInput { get; }
  public bool IsDebugVideoPlaying { get; }
  public void RestartDebugVideo();             // from frame 0, playing
  public void PauseDebugVideo();               // holds last frame in OutputTexture
  public void StopDebugVideo();                // stops; debug RT + OutputTexture cleared to (0,0,0,0)
  ```

- [ ] **Step 1: Field + accessors**

After `m_DebugPlaybackSpeed` (line 24) add:
```csharp
        [Tooltip("Start the clip on the first sim step (legacy). Untick when SimTimeline cues it: the clip then " +
                 "waits, transparent, until RestartDebugVideo() / a StartDebugVideo cue.")]
        public bool debugVideoAutoPlay = true;
```
After `public RenderTexture OutputTexture => _outputTexture;` add:
```csharp
        public bool DebugUseVideoInput => m_DebugUseVideoInput;
        public bool IsDebugVideoPlaying => m_DebugVideoPlayer != null && m_DebugVideoPlayer.isPlaying;
```

- [ ] **Step 2: Init respects autoplay + clears the fresh RT**

In `InitializeDebugVideoIfNeeded`, after `m_DebugVideoTexture.Create(); gpu.Track(m_DebugVideoTexture);` add:
```csharp
                    ClearToTransparent(m_DebugVideoTexture); // fresh RT contents are undefined
```
Replace the tail
```csharp
                if (!m_DebugVideoPlayer.isPlaying)
                    m_DebugVideoPlayer.Play();
```
with
```csharp
                if (debugVideoAutoPlay)
                {
                    if (!m_DebugVideoPlayer.isPlaying) m_DebugVideoPlayer.Play();
                }
                else if (!m_DebugVideoPlayer.isPrepared && !m_DebugVideoPlayer.isPlaying)
                {
                    m_DebugVideoPlayer.Prepare(); // ready to start on cue with minimal latency
                }
```

- [ ] **Step 3: Transport methods + clear helper**

Add after `UpdateInput()`:
```csharp
        // ─────────── Debug-clip transport (SimTimeline cues) ───────────

        /// <summary>Play the debug clip from frame 0. No-op (with a warning) if debug video input is off.</summary>
        public void RestartDebugVideo()
        {
            if (!m_DebugUseVideoInput) { Debug.LogWarning($"[{name}] RestartDebugVideo: debug video input is off."); return; }
            if (gpu == null) Initialize();
            InitializeDebugVideoIfNeeded();
            if (m_DebugVideoPlayer == null || m_DebugVideoClip == null) return;
            m_DebugVideoPlayer.Stop();  // Stop() rewinds; Play() re-prepares and starts at frame 0
            m_DebugVideoPlayer.Play();
        }

        /// <summary>Pause the clip. OutputTexture keeps showing the last frame.</summary>
        public void PauseDebugVideo()
        {
            if (m_DebugVideoPlayer != null && m_DebugVideoPlayer.isPlaying) m_DebugVideoPlayer.Pause();
        }

        /// <summary>Stop the clip and clear both the clip RT and OutputTexture to transparent black,
        /// so the composite overlay (lerp by alpha) and TextureChannelSeeder see nothing.</summary>
        public void StopDebugVideo()
        {
            if (m_DebugVideoPlayer != null) m_DebugVideoPlayer.Stop();
            ClearToTransparent(m_DebugVideoTexture);
            ClearToTransparent(_outputTexture);
        }

        private static void ClearToTransparent(RenderTexture rt)
        {
            if (rt == null || !rt.IsCreated()) return;
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = prev;
        }
```

- [ ] **Step 4: Compile**

Run the headless test command → exit 0, no `error CS`.

- [ ] **Step 5: In-editor check**

DAC scene receiver: tick `selfDrive`, untick `debugVideoAutoPlay`, Play mode. Inspector preview stays transparent/black. Call `RestartDebugVideo` (add a temporary EasyButtons `[Button]` attribute to the three methods if you want inspector buttons — keep them, they are harmless) → clip plays from frame 0. `PauseDebugVideo` → frame holds. `StopDebugVideo` → preview clears. Re-tick `debugVideoAutoPlay`, re-enter Play → clip auto-plays as before.

- [ ] **Step 6: Commit**

```bash
cd /Users/toka/Developer/Graphics/EoC-biomes-compute
git add "Assets/Workspace/11.0 Biomes/src/components/network/ExternalTextureReceiver.cs"
git commit -m "feat(receiver): debug-clip transport — debugVideoAutoPlay, Restart/Pause/StopDebugVideo, stop clears RT to transparent

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 7: `SimTimeline` video cues

**Files:**
- Modify: `components/utils/SimTimeline.cs` — `TimelineAction` enum (`:24-28`), header fields after `firingPlayback` (`:49`), `Play` (`:85-101`), `Stop` (`:104-111`), `Fire` (`:137-153`)

**Interfaces:**
- Consumes: `ExternalTextureReceiver.debugVideoAutoPlay / DebugUseVideoInput / RestartDebugVideo / PauseDebugVideo / StopDebugVideo` (Task 6).
- Produces: `TimelineAction.StartDebugVideo`, `PauseDebugVideo`, `StopDebugVideo`; fields `externalReceiver`, `startDebugVideoOnPlay`.

- [ ] **Step 1: Enum**

```csharp
        public enum TimelineAction
        {
            StartTermites, StartBoids, StartPhysarum, StartCellular,
            StopTermites, StopBoids, StopPhysarum, StopCellular,
            ResetAll, ResetSimsOnly,
            StartDebugVideo, PauseDebugVideo, StopDebugVideo,
        }
```
(Appending keeps existing serialized cue indices valid.)

- [ ] **Step 2: Fields**

After the `firingPlayback` field add:
```csharp
        [Header("External input (optional)")]
        [Tooltip("Receiver whose DEBUG clip the timeline cues. On Play the clip is rewound and held transparent " +
                 "(autoplay disabled at runtime); StartDebugVideo / PauseDebugVideo / StopDebugVideo cues drive it; " +
                 "Stop clears it. The live Syphon/NDI path is untouched.")]
        public ExternalTextureReceiver externalReceiver;
        [Tooltip("Start the debug clip at 0 s on Play (otherwise wait for a StartDebugVideo cue).")]
        public bool startDebugVideoOnPlay = false;
```

- [ ] **Step 3: Play / Stop hooks**

In `Play()`, after `if (firingPlayback != null) firingPlayback.Restart();` add:
```csharp
            if (externalReceiver != null)
            {
                externalReceiver.debugVideoAutoPlay = false; // runtime-only; the timeline owns the clip now
                externalReceiver.StopDebugVideo();           // frame 0, transparent
                if (startDebugVideoOnPlay) externalReceiver.RestartDebugVideo();
            }
```
In `Stop()`, after `if (firingPlayback != null) firingPlayback.Stop();` add:
```csharp
            if (externalReceiver != null) externalReceiver.StopDebugVideo();
```

- [ ] **Step 4: Cues**

In `Fire`, add cases:
```csharp
                case TimelineAction.StartDebugVideo: VideoCue(r => r.RestartDebugVideo()); break;
                case TimelineAction.PauseDebugVideo: VideoCue(r => r.PauseDebugVideo()); break;
                case TimelineAction.StopDebugVideo:  VideoCue(r => r.StopDebugVideo()); break;
```
and a helper after `StopAll<T>`:
```csharp
        private void VideoCue(Action<ExternalTextureReceiver> act)
        {
            if (externalReceiver == null)
            {
                Debug.LogWarning("[SimTimeline] video cue fired but no externalReceiver is assigned.");
                return;
            }
            if (!externalReceiver.DebugUseVideoInput)
            {
                Debug.LogWarning("[SimTimeline] video cue fired but the receiver's debug video input is off.");
                return;
            }
            act(externalReceiver);
        }
```
(`using System;` is already present for `[Serializable]`.)

- [ ] **Step 5: Compile**

Run the headless test command → exit 0, no `error CS`.

- [ ] **Step 6: In-editor verification (spec Part B §Verification, the 72 s goal)**

DAC scene: assign the receiver to `SimTimeline.externalReceiver`, `startDebugVideoOnPlay` off, add cue `72 → StartDebugVideo`, `100 → PauseDebugVideo`. Set `simManager.stepsPerTick` high or `endAtSeconds = 110` for a quick run. Play:
1. Before 72 s the composite is identical to a run with `m_DebugOverlayVideoOnOutput` off (no darkening); `TextureChannelSeeder` seeds nothing visible.
2. At 72 s the clip appears from frame 0 (console: `[SimTimeline] 72.0s → StartDebugVideo`).
3. At 100 s the frame freezes and stays composited.
4. At `endAtSeconds` the timeline stops and the overlay disappears.
5. `startDebugVideoOnPlay` on → clip starts at 0 s each Play from frame 0.
6. Remove the receiver from the timeline, `debugVideoAutoPlay` on → legacy behaviour (clip from first step).

- [ ] **Step 7: Commit**

```bash
cd /Users/toka/Developer/Graphics/EoC-biomes-compute
git add "Assets/Workspace/11.0 Biomes/src/components/utils/SimTimeline.cs"
git commit -m "feat(timeline): externalReceiver + Start/Pause/StopDebugVideo cues; Play rewinds clip transparent, Stop clears

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 8: Docs

**Files:**
- Modify: `docs/ARCHITECTURE.md` — §3.5 (`:263-275`), `ParameterInterpolator` bullet (`:346-349`), `NeuronFiringPlayback`/`SimTimeline` bullet (`:341-343`), `ExternalTextureReceiver` bullet (`:360-364`)
- Modify: `README.md` (per repo rule: update before pushing) — one line in the changelog / features list if such a section exists; otherwise skip.

- [ ] **Step 1: §3.5 — live clone**

After the `metabolicHeat / oxygenConsumption` bullet add:
```markdown
- **Runtime clone** — `SimulationBase.umwelt` is the assigned asset; `Reset()` clones it into
  `liveUmwelt` and everything at runtime (`SimulationManager` perception build + writeback,
  MFT Bank 3, `ParameterInterpolator`) goes through `LiveUmwelt`. The asset is never mutated
  in Play mode; `SaveLiveParamsToPreset` copies `liveUmwelt` back on request.
- **Flat keys** — `UmweltKeys` (`Biomes.Core`) names every field: 8 scalars plus
  `read:<ch>:<effect>.weight` / `write:<ch>.amount`; `UmweltMapping.Keys/GetValue/SetValue/
  RemoveEntry/Snapshot` implement them (SetValue adds a missing entry).
```

- [ ] **Step 2: Interpolator bullet**

Append to the `ParameterInterpolator` bullet:
```markdown
  Also crossfades the sim's `LiveUmwelt` toward `umweltWaypoints[i]` (index-paired with
  `waypoints`, null = untouched): `KeyedCrossfade` over the union of keys, so reads/writes only
  in the target fade in from 0 and ones only in the source fade to 0 and are removed at leg
  end; `enableDeath` snaps at leg end. Toggles: `umwelt.<scalar>`, `umwelt.reads`,
  `umwelt.writes`. Spec: [[superpowers/specs/2026-09-09-umwelt-interpolation-and-timeline-video-design]].
```

- [ ] **Step 3: SimTimeline + receiver bullets**

After the sentence about `SimTimeline.firingPlayback` add:
```markdown
  `SimTimeline.externalReceiver` likewise owns the receiver's *debug clip*: Play rewinds it
  transparent (autoplay off at runtime), `StartDebugVideo` / `PauseDebugVideo` /
  `StopDebugVideo` cues drive it, Stop clears it — so a video coda can start at an authored
  sim second and influence the composite only from there. The clip runs on wall time, not
  the sim clock.
```
In the `ExternalTextureReceiver` bullet, after "or a debug video clip" add:
```markdown
  (`debugVideoAutoPlay`; `RestartDebugVideo`/`PauseDebugVideo`/`StopDebugVideo` — Stop clears
  the RT to transparent black because the composite overlay is `lerp(color, overlay, strength*overlay.a)`)
```

- [ ] **Step 4: Commit**

```bash
cd /Users/toka/Developer/Graphics/EoC-biomes-compute
git add docs/ARCHITECTURE.md README.md
git commit -m "docs: live umwelt clone + key access, interpolator umwelt legs, SimTimeline debug-video cues

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

## Self-review

**Spec coverage**
- Part A: clone (T4), save-back (T4), flat keys (T1, T3), union crossfade (T2, T5), `enableDeath` snap (T5), waypoint pairing incl. umwelt-only legs (T5), toggles (T5), override pause/resume via re-snapshot (T5 `SnapshotFrom`), MFT call-time resolution (T4), docs (T8). ✔
- Part B: `debugVideoAutoPlay` + transparent clear on init (T6), Restart/Pause/Stop semantics (T6), timeline fields + Play/Stop hooks + 3 cues + null/off warnings (T7), scope guard (only debug path touched) ✔, clock caveat documented (T8). ✔

**Placeholder scan** — none.

**Type consistency** — `UmweltKeys.Read/Write/IsRead/IsWrite/IsScalar/TryParseRead/TryParseWrite/Scalars` used identically in T3/T5; `KeyedCrossfade.UnionKeys/Lerp/KeysToRemove` signatures match T2↔T5; `LiveUmwelt` T4↔T5; receiver API names match T6↔T7 (`RestartDebugVideo`, `PauseDebugVideo`, `StopDebugVideo`, `DebugUseVideoInput`, `debugVideoAutoPlay`). ✔
