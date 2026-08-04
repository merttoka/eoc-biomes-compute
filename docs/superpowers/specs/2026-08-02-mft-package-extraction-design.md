# MFT Package Extraction: `midi-fighter-twister-unity` — Design

**Date:** 2026-08-02
**Status:** Approved
**Files touched:** `Assets/Workspace/11.0 Biomes/src/components/network/MidiFighterTwister.cs` (1232 lines, split) · `src/Editor/MidiFighterTwisterEditor.cs` · `src/components/network/MidiPianoMixer.cs` · `src/components/utils/ParameterInterpolatorGroup.cs` · new `Packages/midi-fighter-twister-unity/`

## Problem

`MidiFighterTwister.cs` is two things in one class: a generic MFT hardware driver and a Biomes
application layer. It `[SerializeField]`s `SimulationManager`, `SimulationBase`, `ScreenLayout`,
switches on `PhysarumSim`/`BoidSim`/`TermiteSim`, and hardcodes bindings (`wasteToNutrient`,
`temp→Flow`, `temp→Perm`). Nothing in it is reusable outside this repo, and the most broadly
useful part — the LED feedback protocol built 2026-07-19 — is the most entangled.

A second copy lives at `10.0 Metaesthetica/src/components_simplecontrol/MidiFighterTwister.cs`
(1068 lines). It is a **fork, not a duplicate** — 506 lines differ, and it is referenced by
`Scene_10.0`, `Scene_LoadGallery_SN`, `Scene_reAgency`. It is out of scope; merging it would break
three scenes.

## Design

Extract the device driver into an embedded UPM package. Behavior-preserving: no MFT behavior
changes in this pass.

### Split, by existing `#region`

| Region | ~Lines | Destination |
|---|---|---|
| Header block, lines 1–190 (usings, fields, enums, constants, `EncoderBinding`) | 190 | **split** — see below |
| Lifecycle · MIDI Input · Encoder Apply · Device Management · Logging | 330 | package |
| LED Feedback minus `GetSimParamColor`/`GetSimParamBrightness`/`GetParamRange` | 150 | package |
| `SetSoftBank` · `SetHwBank` · `ColRowToEncoderIdx` | 30 | package |
| Encoder Push (`HandleEncoderPush`, `Randomize/ResetSingleBinding`) | 80 | package |
| **Bank Building** (`BuildBank0-3`, 3 layout builders, `MakeSimParamBinding`, `CollectTypeColumns`, `GetTypeCount`) | 290 | Biomes |
| `ExecuteAction` · `SaveSnapshot` · `SaveToCurrentParams` | 135 | Biomes |
| `GetSimParamColor` · `GetSimParamBrightness` · `GetParamRange` | 55 | Biomes |

The `EncoderBinding` struct is already the correct seam — `label`, `min`, `max`,
`Action<float> setter`, `Func<float> getter` are generic. Only `target`, `simIndex`, `paramName`,
`typeIndex` are app-specific and get dropped.

### Header block, lines 1–190

Not a `#region`, so it needs splitting explicitly:

| Member | Destination |
|---|---|
| `SOFT_BANK_COUNT`, `HW_BANK_COUNT`, `ENCODERS_PER_HW_BANK`, `TOTAL_ENCODERS` | package |
| `EncoderBinding` struct (trimmed), `SoftBank`/`HwBank` props, `onBankChanged` | package |
| `sendLEDFeedback`, `logMidi`, device/`_devices` state | package |
| `m_SimManager`, `m_Simulations`, `m_ScreenLayout` `[SerializeField]`s | Biomes |
| `BindingTarget` enum, `LayoutMode` enum, `layoutMode`, `pushMode` | Biomes |
| `SideButtonAction` enum + the six assignment fields (`leftTop`…`rightBot`) | Biomes |

**Two distinct side-button types.** The package defines `MftSideButton` — a *physical* id
(`LeftTop`, `LeftMid`, `LeftBot`, `RightTop`, `RightMid`, `RightBot`) and nothing more. Biomes
keeps `SideButtonAction` — the *semantic* action (`Reset`, `ResetSimsOnly`, `RandomizeColors`, …)
— plus the six serialized fields mapping one to the other. `onSideButton(MftSideButton)` fires;
Biomes looks up its own mapping and calls `ExecuteAction`.

### Two decisions that collapse the API

**Colors live on the binding.** `MftBinding` gains `color` and `brightness`, filled by the app at
bank-build time. The package's LED code reads fields instead of calling back. Removes the
color/brightness hook entirely; `GetSimParamColor`/`GetSimParamBrightness` become part of Biomes'
bank building.

**Side buttons are events.** The package raises `onSideButton(MftSideButton)`; Biomes subscribes
and runs its own `ExecuteAction`. The `SideButtonAction` enum (`Reset`, `ResetSimsOnly`,
`RandomizeColors`, …) is app vocabulary and stays.

Net package API: one inbound call `SetBindings(bank, MftBinding[])`, plus `InvalidatePickup()`,
and three events — `onSideButton`, `onEncoderPush`, `onBankChanged`.

### Layout

```
Packages/midi-fighter-twister-unity/
├── package.json            name: com.merttoka.midi-fighter-twister-unity
├── README.md               includes the CC/LED wire protocol (see below)
├── CHANGELOG.md
├── LICENSE
├── Runtime/
│   ├── MftController.cs    MonoBehaviour; SetBindings, InvalidatePickup, events
│   ├── MftDevice.cs        Minis/RtMidi connect, CC+note I/O, SendCC
│   ├── MftBanks.cs         4 hw x 4 soft, ColRowToEncoderIdx
│   ├── MftLeds.cs          ring positions, RGB, brightness, bank flash
│   ├── MftBinding.cs       { label, min, max, setter, getter, color, brightness }
│   └── MidiFighterTwister.Runtime.asmdef
└── Editor/
    ├── MftControllerEditor.cs
    └── MidiFighterTwister.Editor.asmdef

Assets/Workspace/11.0 Biomes/src/components/network/
└── BiomesMftBindings.cs    BuildBank0-3, layouts, colors, ExecuteAction
```

Dependencies (all already in `Packages/manifest.json`): `jp.keijiro.minis`,
`com.unity.inputsystem`. `RtMidi` ships inside Minis.

### Wire protocol in the README

The package README documents the MFT CC/LED protocol explicitly: encoder CC numbers per hardware
bank, ring-position CC, RGB colour CC and its **nonlinear hue anchors** (red=1, blue=33, cyan=40,
green=43, yellow=65, orange=83, purple=110 — verified 2026-07-19), brightness CC, and the
bank-switch flash timing. This is the part a stranger cannot derive from the source, and it is what
a future WebMIDI port would be written against.

## Consequences

- Publishing target is a standalone repo, `merttoka/midi-fighter-twister-unity`, UPM-installable by
  git URL. A gist alone will not work — UPM needs a directory structure gists cannot hold.
- Develop embedded under `Packages/` first so the repo keeps compiling and the package can be
  tested against the real SIGGRAPH scenes before publication.
- `MidiPianoMixer.cs` and `ParameterInterpolatorGroup.cs` reference `MidiFighterTwister` and must
  repoint to `MftController`.
- Assembly definitions are new to this area — `11.0 Biomes` code currently sits in the default
  assembly. Whether `BiomesMftBindings.cs` needs its own asmdef or the package can be referenced
  from the default assembly is an implementation-time check, not a design assumption.
- `10.0 Metaesthetica` is frozen and untouched.
- Forward compatibility only, not scope: the documented protocol plus the delegate-based binding
  model are what a web/WebMIDI prototype would reuse. No web work in this pass.

## Verification

- Unity compiles with zero errors after the move.
- `Scene_SIGGRAPH` MFT drives all 4 soft banks as before.
- LED ring position, per-type hue gradient, brightness, and 0.7s bank-switch flash are unchanged —
  primary regression risk, since this is the most recently authored behavior.
- Encoder push randomize/reset unchanged; pickup/soft-takeover unchanged.
- `Scene_10.0` still compiles, untouched.

## Related

[[2026-07-19-mft-led-feedback-design]] · [[../plans/2026-07-19-mft-led-feedback]] ·
[[../../sessions/2026-07-19-mft-led-legibility]] · [[../../../Assets/Workspace/11.0 Biomes/MIDI_OSC]]
