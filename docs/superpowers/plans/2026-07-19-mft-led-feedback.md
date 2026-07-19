# MFT LED Feedback (Type Gradients + Bank Flash) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Per-type hue gradients, bank-switch flash indicator, and full-brightness LEDs on the Midi Fighter Twister so the device is readable at a glance.

**Architecture:** All runtime changes in `Assets/Workspace/11.0 Biomes/src/components/network/MidiFighterTwister.cs`. New serialized hue-range fields surface via existing `DrawDefaultInspector()` — no editor changes. Flash is timestamp-based state checked in `Update()`, no coroutines.

**Tech Stack:** Unity C# (Assembly-CSharp), RtMidi out, Minis MIDI in.

**Spec:** `docs/superpowers/specs/2026-07-19-mft-led-feedback-design.md`

## Global Constraints

- No Unity test assembly exists — verification is `dotnet build Assembly-CSharp.csproj` (0 errors) + `LogBindingTable` output + on-device pass (deferred, hue endpoints serialized precisely so tuning is live).
- csproj + `Library/` are gitignored → in a worktree, symlink `Library/` and copy `*.csproj` from the main checkout before building (Task 0).
- Default hue CC values verbatim from spec: physarum 20→40, boid 78→98, termite 57→70; flash 0.7 s.
- Do not touch bindings, ordering, OSC, soft-takeover, or push actions.
- Commit messages: concise, no attribution (user rule).

---

### Task 0: Worktree + build harness

**Files:** none (environment)

- [ ] **Step 1:** Create worktree branch `mft-led-feedback` (use `superpowers:using-git-worktrees` / EnterWorktree).
- [ ] **Step 2:** In worktree root: `ln -s /Users/toka/Developer/Graphics/EoC-biomes-compute/Library Library && cp /Users/toka/Developer/Graphics/EoC-biomes-compute/*.csproj .`
- [ ] **Step 3:** Baseline: `dotnet build Assembly-CSharp.csproj --nologo -v q` → Expected: `0 Error(s)`.

### Task 1: Fix anim constants + brightness bump

**Files:**
- Modify: `Assets/Workspace/11.0 Biomes/src/components/network/MidiFighterTwister.cs:92-96` (constants), `:942-976` (SendAllLEDs)

**Interfaces:**
- Produces: `ANIM_RGB_BRIGHT_MAX` const (int, value 47) used by Task 3.

- [ ] **Step 1:** Confirm `ANIM_STROBE/PULSE/RAINBOW` unused: `grep -n "ANIM_" <file>` → only definitions + `ANIM_NONE` uses.
- [ ] **Step 2:** Replace constants block:

```csharp
// MFT LED animation values (sent as CC value on Ch 2)
// Per DJTT manual: 1-8 strobe, 9-16 pulse, 17-47 RGB brightness (47 = 100%), 127 rainbow
private const int ANIM_NONE           = 0;
private const int ANIM_RGB_BRIGHT_MAX = 47;
```

- [ ] **Step 3:** In `SendAllLEDs()`, after `SendCC(CH_RGB, cc, color);` add:

```csharp
SendCC(CH_ANIM, cc, ANIM_RGB_BRIGHT_MAX);
```

- [ ] **Step 4:** Build → 0 errors. Commit: `fix(mft): correct anim constants per DJTT manual, full LED brightness on bound encoders`

### Task 2: Per-family hue gradients

**Files:**
- Modify: same file — fields after `[Header("LED Feedback")]` block (`:56-58`), color switch in `SendAllLEDs()` (`:959-969`), new method near `GetNormalizedValue`, `LogBindingTable` (`:1126-1164`)

**Interfaces:**
- Consumes: `GetTypeCount(SimulationBase)` (exists, `:493`).
- Produces: `private int GetSimParamColor(EncoderBinding b)` — returns hue-wheel CC int; used by Task 3's restore path (via `SendAllLEDs`).

- [ ] **Step 1:** Add serialized fields after `ledUpdateInterval`:

```csharp
[Header("LED Colors")]
[Tooltip("MFT hue-wheel CC values (1-125). Column color = Lerp(start, end, typeIndex/(typeCount-1)).")]
[Range(1, 125)] public int physarumHueStart = 20;
[Range(1, 125)] public int physarumHueEnd   = 40;
[Range(1, 125)] public int boidHueStart     = 78;
[Range(1, 125)] public int boidHueEnd       = 98;
[Range(1, 125)] public int termiteHueStart  = 57;
[Range(1, 125)] public int termiteHueEnd    = 70;
```

- [ ] **Step 2:** Add method (LED Feedback region):

```csharp
/// <summary>Hue-wheel CC for a SimParam binding: family range interpolated by type index.
/// Single-type sims land on the range midpoint (≈ legacy family anchor).</summary>
private int GetSimParamColor(EncoderBinding b)
{
    if (b.simIndex < 0 || b.simIndex >= m_Simulations.Count) return RGB_OFF;
    var sim = m_Simulations[b.simIndex];
    (int start, int end) = sim switch
    {
        PhysarumSim => (physarumHueStart, physarumHueEnd),
        TermiteSim  => (termiteHueStart, termiteHueEnd),
        _           => (boidHueStart, boidHueEnd),
    };
    int typeCount = Mathf.Max(1, GetTypeCount(sim));
    float t = typeCount <= 1 ? 0.5f : (float)b.typeIndex / (typeCount - 1);
    return Mathf.RoundToInt(Mathf.Lerp(start, end, t));
}
```

- [ ] **Step 3:** Replace color switch in `SendAllLEDs()`:

```csharp
int color = b.target switch
{
    BindingTarget.SimParam        => GetSimParamColor(b),
    BindingTarget.BiomeCrossField => RGB_GREEN,
    BindingTarget.Umwelt          => RGB_CYAN,
    BindingTarget.Global          => RGB_PURPLE,
    _ => RGB_OFF,
};
```

(The old `when b.simIndex >= 0 …` guard moves inside `GetSimParamColor`.)

- [ ] **Step 4:** In `LogBindingTable`, SimParam case, prefix color for no-device verification:

```csharp
int c = GetSimParamColor(b);
cell = $"{c,3}|{sn[0]}{b.typeIndex}.{b.paramName}";
```

- [ ] **Step 5:** Build → 0 errors. Commit: `feat(mft): per-type hue gradients (tunable per-family ranges)`

### Task 3: Bank flash on switch

**Files:**
- Modify: same file — field after `_lastLEDUpdate` (`:114`), `bankFlashDuration` field in LED Colors header block, `Update()` (`:206-213`), `SetSoftBank`/`SetHwBank` (`:910-932`), `SendAllLEDs`/`SendEncoderRingPositions` guards

**Interfaces:**
- Consumes: `_bankColors` (built per soft bank, `:241/255/269/310`), `ANIM_RGB_BRIGHT_MAX` (Task 1), `SendAllLEDs()`.

- [ ] **Step 1:** Add serialized field at end of LED Colors block:

```csharp
[Range(0f, 2f)] public float bankFlashDuration = 0.7f;
```

and private state after `_lastLEDUpdate`:

```csharp
// Bank-switch flash overlay: >0 while active; Update() restores LEDs on expiry.
private float _flashUntil = -1f;
private bool FlashActive => _flashUntil > 0f && Time.unscaledTime < _flashUntil;
```

- [ ] **Step 2:** Add method in LED Feedback region:

```csharp
/// <summary>Flash bank identity: top row = softBank+1 knobs in bank color,
/// bottom row = hwBank+1 knobs in white. Update() restores after bankFlashDuration.</summary>
private void StartBankFlash()
{
    if (!sendLEDFeedback || !_midiOutReady || _bankColors == null || bankFlashDuration <= 0f)
    {
        SendAllLEDs();
        return;
    }
    _flashUntil = Time.unscaledTime + bankFlashDuration;
    int ccBase = _hwBank * ENCODERS_PER_HW_BANK;
    for (int i = 0; i < ENCODERS_PER_HW_BANK; i++)
    {
        int row = i / 4, col = i % 4;
        int color = RGB_OFF;
        if (row == 0 && col <= _softBank) color = _bankColors[_softBank];
        else if (row == 3 && col <= _hwBank) color = RGB_WHITE;
        SendCC(CH_RGB, ccBase + i, color);
        SendCC(CH_ANIM, ccBase + i, color == RGB_OFF ? ANIM_NONE : ANIM_RGB_BRIGHT_MAX);
        SendCC(CH_ENCODER, ccBase + i, 0); // ring off during flash
    }
}
```

- [ ] **Step 3:** Replace `Update()`:

```csharp
void Update()
{
    if (_flashUntil > 0f && !FlashActive)
    {
        _flashUntil = -1f;
        SendAllLEDs();
    }
    if (FlashActive) return;
    if (sendLEDFeedback && Time.realtimeSinceStartup - _lastLEDUpdate > ledUpdateInterval)
    {
        _lastLEDUpdate = Time.realtimeSinceStartup;
        SendEncoderRingPositions();
    }
}
```

- [ ] **Step 4:** In `SetSoftBank` and `SetHwBank`, replace `SendAllLEDs();` with `StartBankFlash();`.
- [ ] **Step 5:** Add early-out at top of `SendEncoderRingPositions()`:

```csharp
if (FlashActive) return;
```

- [ ] **Step 6:** Build → 0 errors. Commit: `feat(mft): bank-switch LED flash (soft bank top row, hw bank bottom row)`

### Task 4: Docs

**Files:**
- Modify: `Assets/Workspace/11.0 Biomes/MIDI_OSC.md` (Inspector/LED sections: gradient legend, flash behavior, tunable fields), `README.md` (one-line mention under MIDI control)

- [ ] **Step 1:** MIDI_OSC.md — replace flat color legend with: per-family gradient ranges (defaults + inspector fields), bank-flash description (top row soft / bottom row white HW, 0.7 s), brightness note.
- [ ] **Step 2:** README.md — update MIDI bullet.
- [ ] **Step 3:** Commit: `docs: MFT LED gradients + bank flash`

### Task 5: Review loop + merge

- [ ] **Step 1:** Run code review (code-reviewer) on the branch diff; fix all confirmed findings; rebuild; commit fixes.
- [ ] **Step 2:** Repeat until no findings.
- [ ] **Step 3:** Remove `Library` symlink + copied csproj (untracked — verify `git status` clean of them via .gitignore).
- [ ] **Step 4:** Merge `mft-led-feedback` → `main`, remove worktree. Session log per eoc-docs.

## Self-Review

- Spec coverage: gradients→T2, flash→T3, brightness+constants→T1, log color→T2.4, docs/testing→T4/T5. Device hue tuning explicitly deferred (serialized fields). ✓
- No placeholders; all code shown. ✓
- Types: `GetSimParamColor(EncoderBinding)→int` consistent T2/T3; `ANIM_RGB_BRIGHT_MAX` defined T1, used T2.3(no)/T3.2. ✓
- Edit-mode caveat: `[ExecuteInEditMode]` `Update()` ticks irregularly in edit mode → flash restore may lag until next editor tick; play mode unaffected. Accepted.
