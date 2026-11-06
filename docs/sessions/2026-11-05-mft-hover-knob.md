---
status: closed
date: 2026-11-05
tags: [session, mft, midi, editor, imgui]
related: [[../ARCHITECTURE]], [[../adr/0020-mft-hover-knob-imgui-debugger-data]], [[../../Assets/Workspace/11.0 Biomes/MIDI_OSC]]
---
# MFT hover knob: one knob moves the field under the mouse

Branch `feat/mft-hover-knob`. Brief: bring the feel of the lab site's scroll-to-slider into Unity, so one
assigned knob moves whatever numeric inspector field the mouse is over.

## Shipped
- **`src/Editor/MftHoverKnob.cs`:** finds the property under the mouse and writes it. Active only on the hover page.
- **`MidiFighterTwister`:** `BindingTarget.Hover` slot on soft bank 3, HW bank 4 (knob `hoverEncoder`, speed
  `hoverGain`), relative input, ring sync, and press-and-turn to move a field on every array element.
- **`src/core_math/HoverKnobMath.cs`:** stepping and property path handling, plus `[KnobIgnore]`, with 38 tests.
- **`AgentColorsGUI`:** H/S/B sliders bound to their properties, so the knob can find them.
- **Bank flash timing:** moved to real time, because `Time.unscaledTime` barely advances in Edit mode.
- **`typeCount`:** `[KnobIgnore]` on all four agent param sets.

## Decided
- Target lookup through internal IMGUI debugger data ([[../adr/0020-mft-hover-knob-imgui-debugger-data|ADR-0020]]).
- **Page:** the owner's choice. Soft bank 3 / HW bank 4 is empty in all 10 scenes (3 to 4 sims), so the knob
  costs no param slot.
- **Press and turn:** applies the same change to each element, so differences between types survive.
- **Target lock:** held until 300 ms after the last tick. The ring is not refreshed during that window, so
  feedback can't fight the turning knob.
- **Out-of-range values:** `Step` clamps only in the direction of travel. Authored values can sit outside
  `ranges` (0.94 against a max of 0.1), and a plain clamp would snap them to the bound on the first tick.
- **Plain ints skipped:** without `[Range]` they may be seeds, counts or channel indices.

## Found
- **Spike picked the row below:** the cursor mapping added `rootVisualElement.worldBound.position` to a
  `window.position` that already gave panel space, so the 24 px tab strip was counted twice. The owner spotted
  it on screen; the numbers had looked consistent. The pointer event replaced that mapping.
- **Every inspector is IMGUI:** EasyButtons' blanket editor overrides the Unity 6 default.
- **Params rows aren't sliders:** `PhysarumAgentType` fields have no `[Range]`, so their travel comes from `ranges`.

## Verified
- EditMode suite 226/226.
- Write path run on a temporary `PhysarumParams`: no snap, single and all-elements moves, one Undo per gesture,
  exclusions.
- Owner's device test on macOS passed. Windows not run.

## Open / next session
1. Horizontal trackpad scroll over the same hover code, so it works without the MFT. Not started.
2. Shift layer on regular knobs does nothing useful: `ApplyEncoder` ignores `delta` outside the hover branch and
   `_fineTune` is never read, yet the MFT inspector still advertises 10x precision. See `ROADMAP.md`.
