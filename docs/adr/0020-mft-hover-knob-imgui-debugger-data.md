---
status: accepted
date: 2026-11-05
tags: [adr, mft, midi, editor, imgui]
related: [[../ARCHITECTURE]], [[../sessions/2026-11-05-mft-hover-knob]], [[../superpowers/specs/2026-08-02-mft-package-extraction-design]]
---
# ADR-0020: The MFT hover knob finds its target through internal IMGUI debugger data

## Context
One MFT knob should move whichever numeric inspector field is under the mouse, with no click and no
arming step: the feel of the lab site's scroll-to-slider.

IMGUI keeps no widgets to query, and every inspector here is IMGUI. EasyButtons registers
`[CustomEditor(typeof(UnityEngine.Object), true)]` (`Assets/EasyButtons/Editor/ButtonEditor.cs`), which
replaces Unity 6's UI Toolkit default inspector for all objects.

Options:
- **UI Toolkit picking.** Public API, but finds no fields here. A spike got only `IMGUIContainer` and the
  inspector's own scrollbar.
- **Right-click to bind (`EditorApplication.contextualPropertyMenu`).** Public API, works everywhere, but
  adds the step this feature exists to remove.
- **Instrument our own editors.** Covers only lines we draw, not the children of `PropertyField(prop, true)`.
- **Patch `EditorGUI.BeginProperty` (Harmony).** New dependency and native method hooks. Not tried.
- **Read `UnityEditor.GUIViewDebuggerHelper.GetPropertyInstructions`.** The data behind the IMGUI Debugger:
  target type, property path and rect for every property a view draws. Internal.

## Decision
Read the internal debugger data by reflection, with these limits:
- **Runs only on the hover page** (soft bank 3, HW bank 4), and only for windows holding an
  `InspectorElement`. Off that page it adds no debugger session and no `wantsMouseMove` change.
- **Cursor from a trickle-down `PointerMoveEvent`.** It is already in panel space, the same space as the
  rects, and needs no OS calls. Mapping the OS cursor through `window.position` was off by one row in the
  spike.
- **Exact object from the tree.** Walk up from the picked element to `InspectorElement.editor` (internal),
  so two components of one type stay distinct.
- **Writes go through the driver's own `SerializedObject`,** collapsed to one Undo step per gesture.
- **Range:** `[Range]`, then the set's `ranges` list for per-type params, then a step proportional to the
  value for other floats. Plain ints are skipped, since they may be seeds, counts or channel indices.

## Consequences
- Measured on a 70-property inspector: repaint 2.2 → 3.5 ms with the debugger on, 0.1 ms per read.
- A Unity upgrade can break it. All members are checked up front; if one is missing, the knob logs one
  warning and stays idle. The rest of the MFT is unaffected.
- It depends on EasyButtons. Without its blanket editor, default inspectors return to UI Toolkit and draw
  no IMGUI rects, so this route stops seeing them. The fix would be `panel.Pick` to `BaseField<float>`,
  excluding `Scroller` parts.
- It only sees controls drawn from a `SerializedProperty`. `AgentColorsGUI`'s H/S/B sliders were rebound to
  their properties for this reason; a float-only `EditorGUILayout.Slider` stays invisible.
- Knob writes skip a custom inspector's change checks. `typeCount` syncs `types` in one, so it carries
  `[KnobIgnore]`, and any new field with that kind of side effect needs it too.
- Package extraction: `IHoverDriver` is the seam. `TryParamRange` is the driver's only dependency on Biomes
  param types.

## Related
[[../sessions/2026-11-05-mft-hover-knob]] · `Assets/Workspace/11.0 Biomes/src/Editor/MftHoverKnob.cs` ·
`Assets/Workspace/11.0 Biomes/src/core_math/HoverKnobMath.cs` · [[../../Assets/Workspace/11.0 Biomes/MIDI_OSC]]
