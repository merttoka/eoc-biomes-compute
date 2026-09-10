---
title: Umwelt interpolation + SimTimeline debug-video cues
date: 2026-09-09
status: approved
tags: [11.0-biomes, parameters, interpolation, umwelt, timeline, external-input, dac]
related: [[ARCHITECTURE]], [[2026-06-07-parameter-interpolator-design]], [[2026-06-07-external-texture-share-design]]
---

# Umwelt interpolation + SimTimeline debug-video cues

Two bounded extensions for the DAC (Shanghai) run, packaged together because both
are "make the installation authorable from assets + timeline" work.

- **Part A** — `ParameterInterpolator` also crossfades each sim's `UmweltMapping`
  (perception weights, deposit amounts, habitat band, metabolism, lifecycle) between
  waypoint umwelt assets, alongside the agent params it already handles.
- **Part B** — `SimTimeline` can start / stop the `ExternalTextureReceiver`'s debug
  input video (frame 0 on Play, cue-driven start mid-timeline, stop on Stop), so a
  recorded take's "video coda" lands at an authored sim-time second.

---

## Part A — Umwelt interpolation

### Findings

- `SimulationBase.umwelt` is a **plain reference to the on-disk asset**. There is no
  runtime clone (unlike `paramsSO` → `agentParams`). `SimulationManager` reads
  `sim.umwelt.reads/writes/metabolicHeat/oxygenConsumption` every step
  (`Biome.BuildPerceptionTex`, fused writeback), so mutating the live object is
  sufficient — no GPU plumbing.
- `MidiFighterTwister` Bank 3 setters already write `sim.umwelt.*` directly, so in the
  Editor knob tweaks silently dirty and persist to the asset.
- Interpolating into the asset would (1) overwrite the source preset and (2) break when a
  waypoint asset is also the live one (`from == to`). A runtime clone is required.
- Umwelt `reads`/`writes` are **variable-length lists** keyed by biome channel (+ effect
  for reads). Real DAC assets differ in shape per species, so index-matching is brittle.

### Decisions (locked)

- **Runtime clone**: `umwelt` stays the assigned preset (scene refs untouched). Add
  `[NonSerialized] UmweltMapping liveUmwelt`, `Instantiate`d in `SimulationBase.Reset()`.
  Accessor `LiveUmwelt => liveUmwelt ?? umwelt`. All runtime readers/writers switch to it.
- **Save-back**: `SaveLiveParamsToPreset()` also `CopySerialized(liveUmwelt → umwelt)`,
  so the existing "save live to preset" flow keeps capturing umwelt tweaks (it would
  otherwise regress once MFT writes go to the clone).
- **Flat key surface on `UmweltMapping`** (no `IParamSet` — its `TypeCount`/`Scale*`/
  `Randomize*` contract doesn't fit and would pull it into `ParamsEditor`):

  | key | field |
  |---|---|
  | `permMin`, `permMax` | `preferredPermeabilityMin/Max` |
  | `metabolicHeat`, `oxygenConsumption` | same |
  | `deathO2`, `deathPerm` | `deathThresholdOxygen/Permeability` |
  | `corpseWaste`, `corpseDecay` | `corpseWasteAmount`, `corpseDecayRate` |
  | `read:<channel>:<effect>.weight` | `reads[i].weight` for the entry with that channel+effect |
  | `write:<channel>.amount` | `writes[i].amount` for the entry with that channel |

  API: `IEnumerable<string> Keys` (current entries), `float GetValue(string key)`,
  `void SetValue(string key, float v)` — `SetValue` on a missing `read:`/`write:` key
  **adds** the entry; `RemoveEntry(string key)` removes one. `enableDeath` (bool) is not
  a key.
- **Union-key crossfade** for reads/writes: per leg, iterate `from.Keys ∪ to.Keys`.
  - key in both → lerp.
  - key only in target → add entry to live at leg start, lerp `0 → target`.
  - key only in from → lerp `from → 0`; remove entry when the leg completes.
  Perceptions therefore fade in/out rather than pop.
- **`enableDeath`**: snaps to the target's value at leg end (not lerped).
- **Waypoint pairing**: `ParameterInterpolator` gains `List<UmweltMapping> umweltWaypoints`,
  index-paired with `waypoints`. `null` entry → umwelt untouched that leg. A leg whose
  agent-param waypoint is null/non-`IParamSet` but has an umwelt waypoint runs
  umwelt-only (no warning). Same `durationSteps` / `holdSteps` / `easing`.
- **Toggles**: `RefreshParamList` also lists umwelt scalar keys prefixed `umwelt.`
  (e.g. `umwelt.metabolicHeat`). Read/write entry keys are governed by two group toggles
  `umwelt.reads` and `umwelt.writes` (per-channel toggles would churn the list).
- **Override pause/resume** (MIDI coexistence via `ParameterInterpolatorGroup`) applies
  unchanged: `SnapshotFrom()` re-snapshots umwelt too, so resume continues from knob-edited
  values.

### Changes

| File | Change |
|---|---|
| `components/core/SimulationBase.cs` | `liveUmwelt` + `LiveUmwelt`; clone in `Reset()`; extend `SaveLiveParamsToPreset` |
| `components/core/SimulationManager.cs` | `sim.umwelt` → `sim.LiveUmwelt` (perception build, fused writes, null-guards) |
| `components/network/MidiFighterTwister.cs` | Bank 3 umwelt getters/setters resolve `sim.LiveUmwelt` **at call time** (not captured at binding build) |
| `components/core/UmweltMapping.cs` | `Keys` / `GetValue` / `SetValue` / `RemoveEntry`; key helpers |
| `components/utils/ParameterInterpolator.cs` | `umweltWaypoints`; `_fromUmwelt` snapshot; union-key leg apply; `enableDeath` snap; toggles |
| `docs/ARCHITECTURE.md` | §3.5 (live clone, save-back), interpolator bullet |

### Verification (in-editor, `_DAC Empty Test.unity`)

1. Pair `Boid_0`/`UmweltBoid_0` → a second umwelt asset with a different read list;
   `durationSteps` ≈ 120. Play → `liveUmwelt` scalars sweep; a read present only in the
   target appears at weight 0 and rises; one present only in the source falls to 0 and is
   gone after the leg. On-disk assets unchanged (git diff clean).
2. `umwelt.metabolicHeat` toggle off → frozen while others move.
3. MFT Bank 3 knob mid-leg → Group pauses; after cooldown resumes from the knob value
   toward the same waypoint, no jump.
4. Save-live-to-preset writes umwelt tweaks back into the assigned umwelt asset.

---

## Part B — SimTimeline debug-video cues

### Findings

- `ExternalTextureReceiver` debug path: `UpdateInput()` (called from
  `SimulationManager.Step()` only) → `InitializeDebugVideoIfNeeded()` creates a
  `VideoPlayer` and **auto-plays on first step** if `m_DebugUseVideoInput`. It then blits
  the player's RT into `OutputTexture` every step. Loop / speed are inspector fields.
- The `VideoPlayer` runs on its own clock; the sim clock only gates the copy. Nothing
  can restart, seek, or stop it at runtime except `Release()`.
- `SimTimeline` already owns two side-transports on Play/Stop: `figureExporter` frame export
  and `firingPlayback.Restart()/Stop()`. The video should follow the same pattern.
- DAC scene: one receiver, `m_DebugUseVideoInput: 1`, clip assigned; `streamName`
  `TouchDesigner/TDSyphonSpoutOut` for the live path.

### Decisions (locked)

- **Receiver transport API** (`ExternalTextureReceiver`):
  - `public bool debugVideoAutoPlay = true` — existing behaviour preserved by default.
    When `false`, `InitializeDebugVideoIfNeeded` prepares the player but does not `Play()`.
  - `RestartDebugVideo()` → ensure init, `time = 0`, `Play()`.
  - `StopDebugVideo()` → `Stop()` the player and **clear the debug RT to black**, so
    downstream influence goes to zero rather than holding the last frame.
  - `PauseDebugVideo()`; `bool IsDebugVideoPlaying`; `bool DebugUseVideoInput`.
- **Timeline wiring** (`SimTimeline`):
  - `[Header("External input (optional)")] ExternalTextureReceiver externalReceiver;`
    `bool startDebugVideoOnPlay = false;`
  - On **Play**: if assigned → `externalReceiver.debugVideoAutoPlay = false` (runtime-only
    edit, same convention as the `fadeSecondsOverride` write), `StopDebugVideo()` (frame 0,
    black), then `RestartDebugVideo()` if `startDebugVideoOnPlay`.
  - On **Stop** / `OnDisable`: `StopDebugVideo()`.
  - New `TimelineAction`s: `StartDebugVideo` (= `RestartDebugVideo()`), `StopDebugVideo`,
    `PauseDebugVideo`. No-op with one warning if `externalReceiver` is null or its debug
    input is off.
- **Scope guard**: only the *debug clip* is cued. The live Syphon/NDI path is unaffected.
- **Clock caveat (documented, not solved)**: the `VideoPlayer` advances on wall time, like
  `NeuronFiringPlayback`. Under Unity Recorder's constant capture clock, `skipOnDrop`
  keeps it roughly synced; frame-exact video cuts are out of scope.

### Changes

| File | Change |
|---|---|
| `components/network/ExternalTextureReceiver.cs` | `debugVideoAutoPlay`; `Restart/Stop/PauseDebugVideo`; clear RT on stop; accessors |
| `components/utils/SimTimeline.cs` | `externalReceiver`, `startDebugVideoOnPlay`; Play/Stop hooks; 3 new actions in `Fire` |
| `docs/ARCHITECTURE.md` | `SimTimeline` bullet (video transport), receiver bullet |

### Verification

1. DAC scene, `startDebugVideoOnPlay` off, cue `StartDebugVideo @ 72 s`: composite shows
   no video influence until 72 s, then the clip from frame 0. `Stop` at `endAtSeconds`
   blackens the influence.
2. `startDebugVideoOnPlay` on: clip starts at 0 s each Play, always from frame 0.
3. Receiver with `debugVideoAutoPlay` left `true` and no timeline → unchanged behaviour.

---

## Out of scope

- Sequencer (`ParamSnapshotClip`/`Mixer`) umwelt snapshots.
- Per-channel umwelt toggles; per-waypoint timing.
- Frame-locking the video to the sim clock.
