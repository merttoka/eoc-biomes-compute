---
status: closed
date: 2026-11-05
tags: [session, mft, midi, editor, imgui, deslop]
related: [[../ARCHITECTURE]], [[../adr/0021-sideways-scroll-debugger-lease]], [[2026-11-05-mft-hover-knob]], [[../../Assets/Workspace/11.0 Biomes/MIDI_OSC]]
---
# Sideways scroll, fine control and press-and-turn on every knob

Branch `feat/mft-hover-knob`, continuing [[2026-11-05-mft-hover-knob]]. Brief: bring the web slider's trackpad
gesture into the Inspector, make Shift a fine control everywhere, and extend press-and-turn to regular knobs.

## Shipped
- **`src/Editor/InspectorScrollScrub.cs`:** sideways scroll moves the field under the mouse, in any scene and with no
  MFT. Shift is 1/10 speed and Ctrl moves every array element. Toggle under the `Biomes` menu.
- **`ScrollGesture`:** ports `wheelScrub` and its tests from the lab site, so vertical scrolls never move a field.
- **`ImguiPropertyRects`, `InspectorFieldTarget`:** split out of `MftHoverKnob` as the shared lookup and write path.
- **`KeyboardShift`:** reads Shift from the OS, since a MIDI callback has no `Event.current`. Windows path not run.
- **Fine control on regular knobs:** keyboard Shift, the hardware shift layer and `ToggleFineTune` all give 1/10 speed,
  relative to the param's current value.
- **`pressTurnScope`:** press-and-turn on regular knobs, with `KnobPressState` telling it from a tap.
- **Push action:** now runs at release, on a press with no turn.
- **Rename:** `11.4 PPS Scene` to `11.4 PPS Dev Scene`, with the setup tool's path and menu item.

## Decided
- Debugger recording on a lease ([[../adr/0021-sideways-scroll-debugger-lease|ADR-0021]]).
- **Default scope `AllTypesOfSim`:** that param on every agent type of the sim. `AllSims` adds same-named params on other sims.
- **Echo on every fine tick:** the knob's position returns to the param's, so at 1/10 speed it can't reach an end stop first.
  The hover knob at full speed keeps its echo between gestures, which is the feel the owner tested.
- **Wheel units:** Unity reports trackpad travel in 0.05 steps and a comfortable swipe measured 50 to 350 units, so
  full travel stays at 200 units. The owner found that speed right.

## Found
- **Regression, already pushed in `f3a5a5f`:** property-bound H/S/B sliders stopped responding to the mouse, because
  `AgentColorsGUI.Draw` called `Update()` while their edits were pending. The knob hid it by using its own
  `SerializedObject`. A synthetic drag on the real slider showed 0.000 unchanged before the fix and 0.771 after.
- **Shift "not working" on knobs:** the owner meant the keyboard key. The trace showed 917 ticks on Ch 0 with Shift held
  and none on Ch 4.
- **MIDI output reopened 52 times in one test:** `OnDeviceChange` fires per Minis channel. It now reopens only when the
  Twister itself returns.
- **Wrong page on push:** the action rebuilt its slot from `_hwBank`, which can lag behind the device.
- **Hover knob stuck at an end stop on unranged fields:** the ring always asks for 64, so "send if changed" never
  re-centred it.
- **`/deslop-global` review fixes:** see the list below.

### Review fixes, not device-tested
Applied after the owner's device test. Unit tests and simulated gestures cover the logic, not the hardware.
- Ring stays off an end stop unless the value is at that end (`RingPosition`).
- Press state clears on device loss and soft bank change, and raw values don't carry across soft banks.
- `_fineTune` is per knob and soft bank, now that it is live.
- A bank flash records the knob position it zeroes, and expires on its own in Edit mode.
- Boid's four renamed fields find their `ranges` entry through `[KnobParam]`.
- `[Min]` is honored on unranged fields, and `hoverEncoder` can't be moved by the knob itself.
- A closed inspector no longer throws from the hover driver, and a slow first repaint no longer drops a short flick.
- **Rebuild All Banks** refreshes the device, and player builds no longer get a dead hover knob.

## Verified
- EditMode suite 254/254. About 25 errors per full run come from `AgentPaletteCyclerTests.SetUp`, not this work.
- Owner's device test on macOS before the deslop pass: scroll, Shift on knobs, press-and-turn.
- After the deslop pass: compile, suite, simulated gestures through the MIDI handler, synthetic H slider drag.

## Open / next session
1. ~~Device-test the review fixes above.~~ → **done, see below**
2. ~~A long fine hover gesture stalls at the knob's end stop until a 300 ms pause.~~ → **fixed, see below**
3. Follow-ups from the review are in `ROADMAP.md`.
4. ~~The presets in `11.4 PPS Dev Scene/assets` hold values from `SaveToCurrentParams` during testing.~~ → **owner kept them**

## Device retest
- Passed on the MFT: replug restores LEDs and rings, a held knob across a soft bank change doesn't fire its push action,
  `ToggleFineTune` stays on its own knob, the flash clears off the hover page in Edit mode, Boid's renamed fields use their range.
- **Failed:** a fine turn couldn't reach min or max. The knob stopped, and moved a little more only after a pause.
- **Cause:** the hover path sent no position back during a gesture, so the knob ran ahead of a value moving at 1/10
  and sat on its end stop until the gesture ended. A model of the device stopped at 0.4496 after 64 ticks; regular knobs
  reached both limits. Fields with no range stalled the same way after 63 ticks.
- **Fix:** `HoverKnobMath.NeedsResync` sends the position back once knob and value diverge, or from an end stop the
  value hasn't reached. A full-speed turn keeps them together, so it sends nothing, which keeps the feel already tested.
- **After:** the model reaches 0 and 1 exactly, a field with no range takes all 400 ticks, and a full-speed turn sends
  no position back. EditMode suite 263/263.
