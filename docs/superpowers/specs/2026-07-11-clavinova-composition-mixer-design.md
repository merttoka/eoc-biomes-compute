---
title: Clavinova MIDI Composition Mixer (v1)
date: 2026-07-11
status: draft
tags: [midi, input, composition, clavinova, spec]
---

# Clavinova MIDI Composition Mixer — v1

Play a Yamaha Clavinova (USB MIDI) as a **live composition mixer** for the biome
composite: each sim's on-screen presence is set by which key you play and how hard.
Coexists with the Midi Fighter Twister. Minimal first slice; expressive per-sim
behavior play and biome compositing are later phases.

## Goal

A performer at the Clavinova fades sim layers (Termite / Physarum / Boid …) in and
out of the composited output by playing keys — velocity sets the layer's level,
chords crossfade several at once — without touching Unity or the Twister.

## Non-goals (v1)

- **Per-sim behavior play** (register-zoned Speed/Trail/Sensor/Cohesion macros) → **v2**.
- **Biome-channel visibility in the artwork** → **v3** (needs a composite-shader change;
  see Constraint below). v1 does *not* composite biome fields.
- Pitch→hue and note-density→energy always-on layers → deferred (kept minimal).
- No new Python/OSC bridge — native only.

## Key constraint discovered in the code

`SimulationManager.Render()` composites **only sims**: `simInput0..7` each multiplied
by `simWeights[i] = simulations[i].compositeWeight`, plus an external-video overlay and
the neuron firing-ring overlay. The 13 **biome channels**
(`Nutrient, Pheromone_0..2, Oxygen, Temperature, Waste, Permeability, Flow_X, Flow_Y,
Dispersal, Humidity`, per `BiomeFieldConfig.BiomeChannel.Names`) render only as **debug
quads / debug grid** (`debugChannel` / `CycleDebugChannel` / `ToggleDebugGrid`) — an
inspection view, not the output image.

**Therefore the only first-class composition lever today is per-sim
`compositeWeight`** (`SimulationBase.compositeWeight`, `public float [Range(0,4)]`,
default 1). True biome-channel compositing is a v3 render change, out of scope here.

The Twister's `BindingTarget` enum (`SimParam, BiomeChannel, BiomeCrossField, Umwelt,
Global`) does **not** include sim visibility/weight — so the piano owns `compositeWeight`
cleanly, with no write conflict against the Twister.

## Architecture

Native, via the existing Minis MIDI path. No new process, lowest latency.

```
Clavinova ──USB──▶ Unity Input System ──▶ Minis.MidiDevice
                                              │ onWillNoteOn(note, velocity)
                                              ▼
                                        MIDIMapping.cs
                          (existing device I/O; add a public note event)
                                              │  onNoteOn(channel, note, vel)
                                              ▼
                                        MidiPianoMixer.cs   ← NEW
                        (top-octave command zone + sim-mixer routing)
                                              │  sim.compositeWeight = …
                                              ▼
                          SimulationManager.Render() → composite kernel
```

### Components

**`MIDIMapping.cs` (modified, small):** already connects all MIDI devices and receives
`OnWillNoteOn/Off/ControlChange`. Add a public `event Action<int,int,float> NoteOn`
(channel, noteNumber, velocity01) and `NoteOff`, plus a `SustainPedal` bool from CC64,
raised from the existing callbacks. Keeps `MIDIMapping` the reusable device layer; no
composition logic leaks into it. Its current CC→param `MIDIControlMapping` path is
untouched.

**`MidiPianoMixer.cs` (new MonoBehaviour):** subscribes to `MIDIMapping.NoteOn` /
`SustainPedal`. Holds the key layout and a reference to `SimulationManager` (to read the
`simulations` list in order). Owns all `compositeWeight` writes. Serialized config:
- `commandOctaveLowNote` (default 96 = C7) — notes ≥ this are the command zone
  (spans C7..C8, 96..108).
- `mixerBaseNote` (default 21 = A0) — **white keys** from here up auto-assign to sims
  0..N-1 in `SimulationManager.simulations` order (skip black keys).
- `weightMax` (default 2.0, range 0..4) — velocity01 × weightMax → target weight; >1
  boosts a layer past its default 1.0.
- `smoothingSeconds` (default 0.08) — lerp `compositeWeight` toward target in `Update`
  to avoid pops. (`ParameterInterpolator` is a waypoint/preset tool, not a per-value
  smoother, so use a simple per-sim lerp here.)

**`MidiPianoMixerEditor.cs` (new, `Editor/`):** custom Inspector that draws the live
key→sim mapping as a keyboard strip (which white key drives which sim, current weight per
sim, command-zone keys labeled) — the piano analogue of the Twister's binding-table
readout (`MidiFighterTwister.LogBindingTable`). Read-only visualization; assignment stays
automatic.

### Key layout (v1)

| Zone | Notes | Behavior |
|---|---|---|
| **Command zone** | top octave C7–C8 (96–108) | reserved; v1 wires the two Reset keys + **TogglePause** (rest reserved for v2) |
| **Sim mixer** | white keys from A0 up, one per sim | **note-on velocity → that sim's `compositeWeight` target** (`vel01 × weightMax`) |
| everything else | — | ignored in v1 |

Command-zone assignments (v1):
- **C8 (108, the last/highest key)** → full `SimulationManager.Reset()`.
- **B7 (107, next to it)** → `SimulationManager.ResetSimsOnly()`.
  - Both fire **only while the sustain pedal is held** (guard against accidental hits).
- **TogglePause** — one white key.
- Termite/Physarum/Boid **target-select** keys are *reserved* (labeled) but inert in v1;
  they activate in v2 when per-sim behavior play exists.

### Interaction semantics (v1)

- A mixer key is a **fader position**, not a gate: **note-off does nothing**; the layer
  stays at its last-played level. Re-play the key louder/softer to re-level it.
- **Chord** = set several sim weights in one gesture → instant crossfade.
- Weights **smooth** toward their target (`smoothingSeconds`) so playing doesn't pop.
- Sustain-pedal **freeze/latch** of the whole mix → v2 (kept out of minimal v1; the only
  pedal use in v1 is arming Reset).

## Data flow (per event)

1. Key pressed → `MIDIMapping.OnWillNoteOn(note, velocity)` raises `NoteOn(ch, note, vel01)`.
2. `MidiPianoMixer` classifies the note: command zone → action; mixer zone → set
   `_targetWeight[simIndex] = vel01 × weightMax`.
3. `MidiPianoMixer.Update()` lerps each `simulations[i].compositeWeight` toward its target.
4. `SimulationManager.Render()` (unchanged) reads `compositeWeight` into `simWeights` and
   composites. Layer fades on screen.

## Coexistence with the Twister

Both are input front-ends onto the same public sim/manager APIs. The Twister writes sim
**params** / biome / umwelt / global via its banks; the piano writes **`compositeWeight`**
+ Reset/Pause. Disjoint write sets → no conflict. Reset is shared (both may call it);
last call wins, which is fine.

## Verification (manual, in Editor)

No Unity GPU test harness exists here, so v1 is verified by driving it live:
1. Enter Play with the Clavinova connected; confirm `[MIDI]` NoteOn logs appear
   (`MIDIMapping` already logs these).
2. Play each mixer key soft→hard; confirm the matching sim fades from ghosted→full in the
   Game view, and `compositeWeight` moves in the Inspector.
3. Play a 3-key chord; confirm simultaneous crossfade.
4. Hold sustain + Reset key; confirm sims reset. Reset key alone (no pedal) does nothing.
5. Confirm the Twister still drives params concurrently (no interference).

Add a one-line `[PianoMixer]` debug log on each weight change as the observability hook.

## Phasing

- **v1 (this spec):** sim `compositeWeight` mixer + reserved command zone (Reset, Pause).
- **v2:** per-sim behavior play — target-select command keys + register-zoned
  Speed/Trail/Sensor/Cohesion (velocity→bias, decay to neutral, sustain=freeze,
  sostenuto=latch held layers).
- **v3:** biome-channel compositing — composite-shader change to blend selected biome
  fields into the output, then a biome-channel mixer zone on the keyboard.

## Decisions (resolved)

- **Reset keys:** C8 (108, last key) → full `Reset()`; B7 (107) → `ResetSimsOnly()`; both
  sustain-pedal-armed.
- **`weightMax`:** allow >1 — default 2.0, range 0..4.
- **Mixer keys:** auto-assign white keys from A0 in sim-list order, plus a custom Inspector
  visualization of the key→sim map (Twister-style).
- **Mixer note-off:** holds the level (fader semantics), does not fade out.
