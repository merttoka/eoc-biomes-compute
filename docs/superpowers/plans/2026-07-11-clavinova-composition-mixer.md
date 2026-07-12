# Clavinova MIDI Composition Mixer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Play a Yamaha Clavinova (USB MIDI) as a live composition mixer — velocity on each sim's key sets that sim's on-screen presence (`compositeWeight`), chords crossfade — plus pedal-armed reset and pause, all native in Unity alongside the Midi Fighter Twister.

**Architecture:** Extend the existing native Minis MIDI path. `MIDIMapping.cs` gains public `NoteOn`/`NoteOff` events and a `SustainHeld` flag. A new `MidiPianoMixer` MonoBehaviour subscribes to those, maps white keys to `SimulationManager.simulations` in order via a pure `PianoMixerLayout` helper, and eases each `sim.compositeWeight` toward the last-played level. A custom Editor draws the key→sim map.

**Tech Stack:** Unity (default `Assembly-CSharp`), C#, Minis 1.3.2 (`jp.keijiro.minis`) for MIDI over the Input System, EasyButtons (`[Button]`) for in-editor self-tests, `SimulationManager` composite (`simWeights` ← `compositeWeight`).

> **Post-implementation note (2026-07-11):** built **self-contained**. `MidiPianoMixer` opens
> its own Minis connection and tracks CC64 itself (mirroring `MidiFighterTwister`) instead of
> consuming `MIDIMapping` — the scene's live MIDI hub turned out to be `MidiFighterTwister`, and
> `MIDIMapping` wasn't in the scene. Task 1's `MIDIMapping` edits were made, then **reverted as
> unused**; Task 3's `MidiPianoMixer` absorbed the device-connection + sustain logic. Also: the
> pause field is `stepsPerTick` (renamed from `stepsPerFrame`). Tasks 2, 4, 5 stand as written.

## Global Constraints

- Target namespace: `Biomes`. All new files live under `Assets/Workspace/11.0 Biomes/src/components/network/` (editor code under `network/Editor/`).
- No Unity Test Framework in this project — do **not** add it. Pure logic is verified by an EasyButtons `[Button]` self-test that logs `N passed, M failed` (the project's established in-editor idiom). MIDI-live and visual behavior is verified manually in Play mode.
- `SimulationBase.compositeWeight` is `public float [Range(0f, 4f)]`, default 1. Clamp all weight writes to `[0, 4]`.
- The piano owns `compositeWeight` exclusively. Do **not** touch the Twister's write set (sim params / biome / umwelt / global via `MidiFighterTwister`). Reset is shared (both may call `SimulationManager.Reset()`); that is fine.
- Pause = `SimulationManager.stepsPerFrame` toggled between `>0` and `0` (`0 = paused`), mirroring `MidiFighterTwister` — `stepsPerFrame` is public.
- `MIDIMapping` is `sealed`; extend the class in place (no subclassing).
- Commit messages: concise, no attribution trailers (repo convention).

---

## File Structure

- **Modify** `.../network/MIDIMapping.cs` — add `NoteOn`/`NoteOff` events + `SustainHeld` (CC64). Device layer only.
- **Create** `.../network/PianoMixerLayout.cs` — pure static mapping logic (white-key test, sim→key auto-assign, note classification, velocity→weight). No runtime state; self-testable.
- **Create** `.../network/MidiPianoMixer.cs` — MonoBehaviour: config + self-test button (Task 2), then runtime event handling + weight smoothing (Task 3). Owns all `compositeWeight` writes.
- **Create** `.../network/Editor/MidiPianoMixerEditor.cs` — custom Inspector; draws the key→sim map with weight bars (Twister-style readout).

---

### Task 1: MIDIMapping — expose note + sustain events

**Files:**
- Modify: `Assets/Workspace/11.0 Biomes/src/components/network/MIDIMapping.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: on `MIDIMapping` — `public event Action<int,int,float> NoteOn` (channel, noteNumber, velocity01); `public event Action<int,int> NoteOff` (channel, noteNumber); `public bool SustainHeld { get; private set; }`.

- [ ] **Step 1: Add the event/flag fields.** After the `m_MIDIMappings` field declaration (currently around line 26), insert:

```csharp
        // ── Piano mixer bridge: raise note + sustain so MidiPianoMixer can subscribe ──
        public event Action<int, int, float> NoteOn;  // channel, noteNumber, velocity01
        public event Action<int, int> NoteOff;        // channel, noteNumber
        public bool SustainHeld { get; private set; }
        private const int CC_SUSTAIN = 64;
```

- [ ] **Step 2: Raise `NoteOn`/`NoteOff` from the existing callbacks.** Replace the two expression-bodied handlers:

```csharp
        void OnWillNoteOn(Minis.MidiNoteControl note, float velocity)
          => Debug.Log($"[MIDI] Ch.{note.channel,-2} {note.shortDisplayName,3} ({note.noteNumber:000}) NoteOn {velocity * 100,3:0}%");

        void OnWillNoteOff(Minis.MidiNoteControl note)
          => Debug.Log($"[MIDI] Ch.{note.channel,-2} {note.shortDisplayName,3} ({note.noteNumber:000}) NoteOff");
```

with block bodies that also invoke the events:

```csharp
        void OnWillNoteOn(Minis.MidiNoteControl note, float velocity)
        {
            Debug.Log($"[MIDI] Ch.{note.channel,-2} {note.shortDisplayName,3} ({note.noteNumber:000}) NoteOn {velocity * 100,3:0}%");
            NoteOn?.Invoke(note.channel, note.noteNumber, velocity);
        }

        void OnWillNoteOff(Minis.MidiNoteControl note)
        {
            Debug.Log($"[MIDI] Ch.{note.channel,-2} {note.shortDisplayName,3} ({note.noteNumber:000}) NoteOff");
            NoteOff?.Invoke(note.channel, note.noteNumber);
        }
```

- [ ] **Step 3: Track the sustain pedal in `OnWillControlChange`.** Immediately after the line `m_lastMidiValues[controlKey] = value;` inside `OnWillControlChange`, insert:

```csharp
            if (cc.controlNumber == CC_SUSTAIN)
            {
                bool held = value >= 0.5f;
                if (held != SustainHeld)
                {
                    SustainHeld = held;
                    Debug.Log($"[MIDI] Sustain {(held ? "DOWN" : "UP")}");
                }
            }
```

- [ ] **Step 4: Verify it compiles and fires.** In Unity, let the scripts recompile (no console errors). Enter Play with the Clavinova connected. Expected: pressing a key still logs `[MIDI] … NoteOn …`; pressing/releasing the sustain pedal logs `[MIDI] Sustain DOWN` / `[MIDI] Sustain UP`.

- [ ] **Step 5: Commit.**

```bash
git add "Assets/Workspace/11.0 Biomes/src/components/network/MIDIMapping.cs"
git commit -m "feat(midi): expose NoteOn/NoteOff events + SustainHeld from MIDIMapping"
```

---

### Task 2: PianoMixerLayout (pure logic) + MidiPianoMixer skeleton with self-test

**Files:**
- Create: `Assets/Workspace/11.0 Biomes/src/components/network/PianoMixerLayout.cs`
- Create: `Assets/Workspace/11.0 Biomes/src/components/network/MidiPianoMixer.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `PianoMixerLayout` (static): consts `ResetFullNote=108`, `ResetSimsOnlyNote=107`, `TogglePauseNote=96`; `enum NoteRole { Ignored, MixerSim, ResetFull, ResetSimsOnly, TogglePause }`; `bool IsWhiteKey(int note)`; `int[] AssignSimNotes(int baseNote, int commandLowNote, int simCount)`; `NoteRole Classify(int note, int commandLowNote, int[] simNotes, out int simIndex)`; `float VelocityToWeight(float velocity01, float weightMax)`.
  - `MidiPianoMixer` (MonoBehaviour): serialized `m_Midi`, `m_SimManager`, public `mixerBaseNote=21`, `commandLowNote=96`, `weightMax=2f`, `smoothingSeconds=0.08f`; `[Button] RunLayoutSelfTest()`.

- [ ] **Step 1: Create `PianoMixerLayout.cs` with STUBS that compile but fail the self-test (red).**

```csharp
using UnityEngine;

namespace Biomes
{
    /// <summary>Pure, testable mapping logic for the Clavinova composition mixer. No Unity
    /// runtime state, so it is exercised by MidiPianoMixer.RunLayoutSelfTest().</summary>
    public static class PianoMixerLayout
    {
        public const int ResetFullNote     = 108; // C8, highest key on an 88-key board
        public const int ResetSimsOnlyNote = 107; // B7, next to it
        public const int TogglePauseNote   = 96;  // C7

        public enum NoteRole { Ignored, MixerSim, ResetFull, ResetSimsOnly, TogglePause }

        // STUBS — intentionally wrong so the self-test goes red first.
        public static bool IsWhiteKey(int note) => false;
        public static int[] AssignSimNotes(int baseNote, int commandLowNote, int simCount) => new int[0];
        public static NoteRole Classify(int note, int commandLowNote, int[] simNotes, out int simIndex)
        { simIndex = -1; return NoteRole.Ignored; }
        public static float VelocityToWeight(float velocity01, float weightMax) => 0f;
    }
}
```

- [ ] **Step 2: Create `MidiPianoMixer.cs` skeleton with the self-test button.**

```csharp
using UnityEngine;
using EasyButtons;

namespace Biomes
{
    /// <summary>Plays a MIDI piano (Clavinova) as a live composition mixer over sim
    /// compositeWeight. Subscribes to MIDIMapping note/pedal events; owns all
    /// compositeWeight writes. Coexists with MidiFighterTwister (disjoint write set).</summary>
    public class MidiPianoMixer : MonoBehaviour
    {
        [SerializeField] private MIDIMapping m_Midi;
        [SerializeField] private SimulationManager m_SimManager;

        [Header("Layout")]
        [Tooltip("First MIDI note of the mixer zone; white keys from here up auto-assign to sims in order. 21 = A0.")]
        public int mixerBaseNote = 21;
        [Tooltip("Notes at or above this are the command zone (resets/pause). 96 = C7.")]
        public int commandLowNote = 96;

        [Header("Response")]
        [Tooltip("velocity(0..1) x weightMax = target composite weight. >1 boosts a layer past its default 1.")]
        [Range(0f, 4f)] public float weightMax = 2f;
        [Tooltip("Seconds for a layer's weight to ease toward its played level.")]
        [Min(0.0001f)] public float smoothingSeconds = 0.08f;

        // Filled by RebuildLayout() in Task 3. simNotes[i] = MIDI note driving sim i.
        private int[] _simNotes = new int[0];
        private float[] _targetWeight = new float[0];

        [Button("Run Layout Self-Test")]
        public void RunLayoutSelfTest()
        {
            int pass = 0, fail = 0;
            void Check(bool cond, string label)
            { if (cond) pass++; else { fail++; Debug.LogError($"[PianoMixer:TEST] FAIL {label}"); } }

            // White keys: A0(21) white, A#0(22) black, C4(60) white, C#4(61) black.
            Check(PianoMixerLayout.IsWhiteKey(21), "A0 is white");
            Check(!PianoMixerLayout.IsWhiteKey(22), "A#0 is black");
            Check(PianoMixerLayout.IsWhiteKey(60), "C4 is white");
            Check(!PianoMixerLayout.IsWhiteKey(61), "C#4 is black");

            // Auto-assign 3 sims from A0 → first three white keys: A0(21), B0(23), C1(24).
            var notes = PianoMixerLayout.AssignSimNotes(21, 96, 3);
            Check(notes.Length == 3, "3 sims assigned");
            Check(notes.Length == 3 && notes[0] == 21 && notes[1] == 23 && notes[2] == 24,
                  $"white keys A0(21),B0(23),C1(24) got [{string.Join(",", notes)}]");

            // Classify.
            Check(PianoMixerLayout.Classify(23, 96, notes, out int si) == PianoMixerLayout.NoteRole.MixerSim && si == 1, "B0 → sim 1");
            Check(PianoMixerLayout.Classify(108, 96, notes, out _) == PianoMixerLayout.NoteRole.ResetFull, "C8 → ResetFull");
            Check(PianoMixerLayout.Classify(107, 96, notes, out _) == PianoMixerLayout.NoteRole.ResetSimsOnly, "B7 → ResetSimsOnly");
            Check(PianoMixerLayout.Classify(96, 96, notes, out _) == PianoMixerLayout.NoteRole.TogglePause, "C7 → TogglePause");
            Check(PianoMixerLayout.Classify(22, 96, notes, out _) == PianoMixerLayout.NoteRole.Ignored, "A#0 ignored");

            // Velocity → weight, clamped to 0..4.
            Check(Mathf.Approximately(PianoMixerLayout.VelocityToWeight(1f, 2f), 2f), "vel 1.0 x2 = 2.0");
            Check(Mathf.Approximately(PianoMixerLayout.VelocityToWeight(0.5f, 2f), 1f), "vel 0.5 x2 = 1.0");
            Check(PianoMixerLayout.VelocityToWeight(1f, 10f) == 4f, "clamped to 4");

            Debug.Log($"[PianoMixer:TEST] {pass} passed, {fail} failed");
        }
    }
}
```

- [ ] **Step 3: Run the self-test and confirm it FAILS (red).** In Unity: create an empty GameObject, add the `Midi Piano Mixer` component, click the **Run Layout Self-Test** button in the Inspector. Expected: several `[PianoMixer:TEST] FAIL …` errors and a final `[PianoMixer:TEST] 0 passed, 13 failed` (or similar — all core checks fail because the logic is stubbed).

- [ ] **Step 4: Implement `PianoMixerLayout.cs` for real.** Replace the stub method bodies:

```csharp
        // MIDI note is a white key when its pitch class is a natural (C D E F G A B).
        public static bool IsWhiteKey(int note)
        {
            switch (((note % 12) + 12) % 12)
            {
                case 0: case 2: case 4: case 5: case 7: case 9: case 11: return true;
                default: return false;
            }
        }

        // Assign sims to consecutive WHITE keys from baseNote upward, staying below the
        // command zone. simNotes[i] = MIDI note driving sim i, or -1 if the keys run out.
        public static int[] AssignSimNotes(int baseNote, int commandLowNote, int simCount)
        {
            var simNotes = new int[Mathf.Max(0, simCount)];
            for (int i = 0; i < simNotes.Length; i++) simNotes[i] = -1;
            int assigned = 0, note = baseNote;
            while (assigned < simCount && note < commandLowNote)
            {
                if (IsWhiteKey(note)) simNotes[assigned++] = note;
                note++;
            }
            return simNotes;
        }

        // Classify an incoming note. simIndex is set only when role == MixerSim.
        public static NoteRole Classify(int note, int commandLowNote, int[] simNotes, out int simIndex)
        {
            simIndex = -1;
            if (note >= commandLowNote)
            {
                if (note == ResetFullNote)     return NoteRole.ResetFull;
                if (note == ResetSimsOnlyNote) return NoteRole.ResetSimsOnly;
                if (note == TogglePauseNote)   return NoteRole.TogglePause;
                return NoteRole.Ignored;
            }
            if (simNotes != null)
                for (int i = 0; i < simNotes.Length; i++)
                    if (simNotes[i] == note) { simIndex = i; return NoteRole.MixerSim; }
            return NoteRole.Ignored;
        }

        // velocity01 (0..1) → composite-weight target, scaled by weightMax and clamped to the
        // SimulationBase.compositeWeight range (0..4).
        public static float VelocityToWeight(float velocity01, float weightMax)
            => Mathf.Clamp(velocity01 * weightMax, 0f, 4f);
```

- [ ] **Step 5: Run the self-test again and confirm it PASSES (green).** Click **Run Layout Self-Test**. Expected: no FAIL lines, final log `[PianoMixer:TEST] 13 passed, 0 failed`.

- [ ] **Step 6: Commit.**

```bash
git add "Assets/Workspace/11.0 Biomes/src/components/network/PianoMixerLayout.cs" \
        "Assets/Workspace/11.0 Biomes/src/components/network/MidiPianoMixer.cs"
git commit -m "feat(midi): PianoMixerLayout mapping logic + MidiPianoMixer self-test"
```

---

### Task 3: MidiPianoMixer runtime — subscribe, route, smooth weights

**Files:**
- Modify: `Assets/Workspace/11.0 Biomes/src/components/network/MidiPianoMixer.cs`

**Interfaces:**
- Consumes: `MIDIMapping.NoteOn`, `MIDIMapping.SustainHeld` (Task 1); `PianoMixerLayout.*` (Task 2); `SimulationManager.simulations` (`public List<SimulationBase>`), `SimulationManager.Reset()`, `SimulationManager.ResetSimsOnly()`, `SimulationManager.stepsPerFrame` (public int); `SimulationBase.compositeWeight` (public float).
- Produces: writes `simulations[i].compositeWeight` each frame; `public void RebuildLayout()`.

- [ ] **Step 1: Add lifecycle + routing + smoothing methods.** Inside the `MidiPianoMixer` class, after the `RunLayoutSelfTest()` method, add:

```csharp
        void OnEnable()
        {
            if (m_Midi != null) m_Midi.NoteOn += HandleNoteOn;
            RebuildLayout();
        }

        void OnDisable()
        {
            if (m_Midi != null) m_Midi.NoteOn -= HandleNoteOn;
        }

        /// <summary>Rebuild the key→sim map and seed targets from the sims' current weights
        /// (so an untouched layer holds its level). Call when the sim list changes.</summary>
        public void RebuildLayout()
        {
            int n = (m_SimManager != null && m_SimManager.simulations != null)
                ? m_SimManager.simulations.Count : 0;
            _simNotes = PianoMixerLayout.AssignSimNotes(mixerBaseNote, commandLowNote, n);
            _targetWeight = new float[n];
            for (int i = 0; i < n; i++)
            {
                var sim = m_SimManager.simulations[i];
                _targetWeight[i] = sim != null ? sim.compositeWeight : 1f;
            }
        }

        private void HandleNoteOn(int channel, int note, float velocity01)
        {
            var role = PianoMixerLayout.Classify(note, commandLowNote, _simNotes, out int simIndex);
            switch (role)
            {
                case PianoMixerLayout.NoteRole.MixerSim:
                    if (simIndex >= 0 && simIndex < _targetWeight.Length)
                    {
                        _targetWeight[simIndex] = PianoMixerLayout.VelocityToWeight(velocity01, weightMax);
                        Debug.Log($"[PianoMixer] sim {simIndex} weight → {_targetWeight[simIndex]:0.00}");
                    }
                    break;
                case PianoMixerLayout.NoteRole.ResetFull:
                    if (m_Midi != null && m_Midi.SustainHeld && m_SimManager != null)
                    { m_SimManager.Reset(); Debug.Log("[PianoMixer] Reset()"); }
                    break;
                case PianoMixerLayout.NoteRole.ResetSimsOnly:
                    if (m_Midi != null && m_Midi.SustainHeld && m_SimManager != null)
                    { m_SimManager.ResetSimsOnly(); Debug.Log("[PianoMixer] ResetSimsOnly()"); }
                    break;
                case PianoMixerLayout.NoteRole.TogglePause:
                    if (m_SimManager != null)
                    {
                        m_SimManager.stepsPerFrame = m_SimManager.stepsPerFrame > 0 ? 0 : 1;
                        Debug.Log($"[PianoMixer] Paused = {m_SimManager.stepsPerFrame == 0}");
                    }
                    break;
            }
        }

        void Update()
        {
            if (m_SimManager == null || m_SimManager.simulations == null) return;
            if (_targetWeight.Length != m_SimManager.simulations.Count) { RebuildLayout(); return; }

            // Frame-rate-independent exponential ease toward each layer's played level.
            float k = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.0001f, smoothingSeconds));
            for (int i = 0; i < _targetWeight.Length; i++)
            {
                var sim = m_SimManager.simulations[i];
                if (sim == null) continue;
                sim.compositeWeight += (_targetWeight[i] - sim.compositeWeight) * k;
            }
        }
```

- [ ] **Step 2: Re-run the layout self-test (regression).** Click **Run Layout Self-Test**. Expected: still `13 passed, 0 failed` (runtime additions didn't break the pure logic).

- [ ] **Step 3: Manual live verification in Play mode.** Assign `m_Midi` (the scene's `MIDIMapping` component) and `m_SimManager` on the component. Enter Play with the Clavinova connected. Verify:
  - Play the **lowest white key (A0)** softly → sim 0 (first in `simulations`) ghosts in; play it **hard** → it goes to full/boosted. `[PianoMixer] sim 0 weight → …` logs match velocity.
  - Play the next two white keys → sims 1 and 2 respond.
  - Play a **3-white-key chord** → all three crossfade together in the Game view.
  - **Hold sustain + C8** → whole sim state resets (`[PianoMixer] Reset()`). **C8 without the pedal** → nothing.
  - **Hold sustain + B7** → sims-only reset. **C7** → `[PianoMixer] Paused = True/False` and the sim visibly freezes/resumes.
  - Twiddle a Twister knob meanwhile → params still change; no interference.

- [ ] **Step 4: Commit.**

```bash
git add "Assets/Workspace/11.0 Biomes/src/components/network/MidiPianoMixer.cs"
git commit -m "feat(midi): MidiPianoMixer runtime — velocity→compositeWeight, pedal reset, pause"
```

---

### Task 4: Inspector visualization of the key→sim map

**Files:**
- Create: `Assets/Workspace/11.0 Biomes/src/components/network/Editor/MidiPianoMixerEditor.cs`

**Interfaces:**
- Consumes: `MidiPianoMixer` public fields (`mixerBaseNote`, `commandLowNote`); its serialized `m_SimManager`; `PianoMixerLayout.AssignSimNotes` + command-note consts; `SimulationBase.SimName` (public), `SimulationBase.compositeWeight`.
- Produces: a `[CustomEditor(typeof(MidiPianoMixer))]` inspector. (Editor-only; no runtime API.)

- [ ] **Step 1: Create the custom editor.** The `Editor/` folder + `#if UNITY_EDITOR` guard keep it out of player builds; no asmdef needed (it compiles into `Assembly-CSharp-Editor`, which references `Assembly-CSharp`).

```csharp
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Biomes
{
    [CustomEditor(typeof(MidiPianoMixer))]
    public class MidiPianoMixerEditor : Editor
    {
        static readonly Color[] Palette = {
            new Color(0.20f,0.60f,1.00f), new Color(1.00f,0.55f,0.20f), new Color(0.40f,0.85f,0.45f),
            new Color(0.85f,0.40f,0.85f), new Color(0.90f,0.80f,0.30f), new Color(0.40f,0.80f,0.85f),
            new Color(0.85f,0.35f,0.40f), new Color(0.65f,0.65f,0.70f),
        };
        static readonly string[] Pitch = { "C","C#","D","D#","E","F","F#","G","G#","A","A#","B" };
        static string NoteName(int n) => n < 0 ? "--" : $"{Pitch[((n % 12) + 12) % 12]}{n / 12 - 1}";

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var mixer = (MidiPianoMixer)target;
            var sm = serializedObject.FindProperty("m_SimManager").objectReferenceValue as SimulationManager;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Composition Mixer Map", EditorStyles.boldLabel);

            if (sm == null || sm.simulations == null || sm.simulations.Count == 0)
            {
                EditorGUILayout.HelpBox("Assign a SimulationManager with sims to see the key map.", MessageType.Info);
            }
            else
            {
                var notes = PianoMixerLayout.AssignSimNotes(mixer.mixerBaseNote, mixer.commandLowNote, sm.simulations.Count);
                for (int i = 0; i < sm.simulations.Count; i++)
                {
                    var sim = sm.simulations[i];
                    string simName = sim != null ? sim.SimName : $"sim{i}";
                    float w = sim != null ? sim.compositeWeight : 0f;

                    Rect row = GUILayoutUtility.GetRect(0, 20, GUILayout.ExpandWidth(true));
                    Rect bar = new Rect(row.x, row.y + 2, row.width, row.height - 4);
                    EditorGUI.DrawRect(bar, new Color(0f, 0f, 0f, 0.15f));
                    Rect fill = new Rect(bar.x, bar.y, bar.width * Mathf.Clamp01(w / 4f), bar.height);
                    EditorGUI.DrawRect(fill, Palette[i % Palette.Length]);
                    EditorGUI.LabelField(row, $"   {NoteName(notes[i]),-4} →  {simName}   (w {w:0.00})");
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Command keys (hold sustain to arm resets)", EditorStyles.miniBoldLabel);
            EditorGUILayout.LabelField($"   {NoteName(PianoMixerLayout.ResetFullNote)}   full Reset");
            EditorGUILayout.LabelField($"   {NoteName(PianoMixerLayout.ResetSimsOnlyNote)}   Reset sims only");
            EditorGUILayout.LabelField($"   {NoteName(PianoMixerLayout.TogglePauseNote)}   Pause");

            if (Application.isPlaying) Repaint(); // live weight bars during play
        }
    }
}
#endif
```

- [ ] **Step 2: Manual verification.** Select the GameObject holding `MidiPianoMixer` with `m_SimManager` assigned. Expected in the Inspector: a "Composition Mixer Map" section listing each sim with its assigned key name (e.g. `A0 → Termite`, `B0 → Physarum`, `C1 → Boid`) and a colored weight bar; a "Command keys" section showing `C8 full Reset`, `B7 Reset sims only`, `C7 Pause`. Enter Play and confirm the bars move as you play the keys.

- [ ] **Step 3: Commit.**

```bash
git add "Assets/Workspace/11.0 Biomes/src/components/network/Editor/MidiPianoMixerEditor.cs"
git commit -m "feat(midi): inspector visualization of piano mixer key→sim map"
```

---

### Task 5: Scene wiring, acceptance test, and docs

**Files:**
- Modify (in Unity Editor): `Assets/Workspace/11.2 SIGGRAPH Scene/Scene_SIGGRAPH.unity`
- Modify: `README.md`

**Interfaces:**
- Consumes: everything above.
- Produces: a wired, playable mixer in the SIGGRAPH scene; documented in README.

- [ ] **Step 1: Wire the component in the scene.** Open `Scene_SIGGRAPH`. On the GameObject that already hosts `MIDIMapping` (the MIDI/OSC bridge object), **Add Component → Midi Piano Mixer**. Assign `M Midi` = that same object's `MIDIMapping`; assign `M Sim Manager` = the scene's `SimulationManager`. Leave `mixerBaseNote=21`, `commandLowNote=96`, `weightMax=2`, `smoothingSeconds=0.08`. Save the scene.

- [ ] **Step 2: Full acceptance test (from the spec).** Enter Play with the Clavinova connected and confirm all of:
  1. `[MIDI] … NoteOn` logs appear when playing.
  2. Each mixer white key soft→hard fades its sim from ghosted→full in the Game view; `compositeWeight` moves in the Inspector and the editor bars track it.
  3. A 3-key chord crossfades three sims at once.
  4. Sustain-held + C8 resets; C8 alone does nothing; C7 pauses/resumes.
  5. The Twister still drives params concurrently with no interference.

- [ ] **Step 3: Update README.** Add a short subsection under the controls/OSC area of `README.md`:

```markdown
### MIDI piano composition mixer (Clavinova)

`MidiPianoMixer` turns a USB MIDI piano into a live layer mixer over the composite.
White keys from A0 up map to the sims in `SimulationManager.simulations` order —
**note velocity sets that sim's `compositeWeight`** (chords crossfade; weights ease via
`smoothingSeconds`; `weightMax` allows boost past 1). Command zone (top octave): **C8** =
full `Reset()`, **B7** = `ResetSimsOnly()` (both require the **sustain pedal held**), **C7**
= pause (`stepsPerFrame` 0↔1). Runs natively via Minis alongside the Midi Fighter Twister
(disjoint controls — the piano owns `compositeWeight`). The Inspector shows the live key→sim
map. Behavior-macro play and biome-channel compositing are future phases.
```

- [ ] **Step 4: Commit.**

```bash
git add "Assets/Workspace/11.2 SIGGRAPH Scene/Scene_SIGGRAPH.unity" README.md
git commit -m "feat(midi): wire piano mixer into SIGGRAPH scene + document"
```

---

## Self-Review

**Spec coverage:**
- Native `MIDIMapping` extension → Tasks 1, 3. ✅
- Top-octave command zone (C8 Reset, B7 ResetSimsOnly, C7 Pause, pedal-armed) → `PianoMixerLayout` consts + `HandleNoteOn` (Tasks 2–3). ✅
- Register auto-assign of white keys from A0 → `AssignSimNotes` (Task 2). ✅
- Velocity → `compositeWeight`, chords crossfade, `weightMax>1`, smoothing, note-off holds → Task 3 (`HandleNoteOn` sets target, `Update` eases; no `NoteOff` handler = holds). ✅
- Inspector visualization "like Twister" → Task 4. ✅
- Coexists with Twister (disjoint write set) → Global Constraints + acceptance test step 5. ✅
- Biome-channel visibility / behavior macros explicitly deferred → noted in README + spec, no task. ✅ (out of scope by design)

**Placeholder scan:** No `TBD`/`TODO`/"handle edge cases" — every code step is complete and runnable. ✅

**Type consistency:** `NoteRole`, `AssignSimNotes`, `Classify(…, out simIndex)`, `VelocityToWeight`, `RebuildLayout`, `HandleNoteOn`, the const note numbers (108/107/96), and field names (`m_Midi`, `m_SimManager`, `weightMax`, `smoothingSeconds`) are used identically across Tasks 2–4. `stepsPerFrame`, `simulations`, `Reset`/`ResetSimsOnly`, `compositeWeight`, `SimName` match the verified `SimulationManager`/`SimulationBase` API. ✅

**Verification model:** Adapted to the project — pure logic via EasyButtons red→green self-test; MIDI-live/visual via explicit manual steps (no Unity Test Framework exists, per Global Constraints). ✅
