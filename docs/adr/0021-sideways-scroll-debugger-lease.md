---
status: accepted
date: 2026-11-05
amends: ADR-0020
tags: [adr, editor, imgui, mft]
related: [[../ARCHITECTURE]], [[0020-mft-hover-knob-imgui-debugger-data]], [[../sessions/2026-11-05-inspector-scroll-fine-control]]
---
# ADR-0021: Sideways scroll shares the IMGUI debugger lookup, held on a lease

## Context
Sideways scroll over an inspector field needs the same lookup as the hover knob, but it has no page to act as
an on/off switch: it works in any scene, with no MFT. ADR-0020 limited the internal debugger to the hover page
so that it cost nothing elsewhere.

Options:
- **Record whenever the mouse is over an inspector.** Simple, but it keeps an internal API running for anyone who
  never scrolls sideways.
- **Start recording on the first sideways scroll, and let it lapse.** The first swipe has to wait for a repaint.

## Decision
Recording starts on demand and is held by a lease:
- `ImguiPropertyRects.Lease(window, seconds)` extends a timer, and recording stops when it expires. The hover knob
  renews 1 s leases while on its page, and scroll takes 5 s on each sideways event.
- A swipe's first travel is held until the window has repainted, then applied. A vertical scroll never starts a lease.
- The cursor comes from the wheel event itself, so scroll needs no `wantsMouseMove` or pointer tracking.
- `InspectorFieldTarget` is the one lookup and write path for both inputs.

The scroll gesture follows the lab site's `Slider.tsx`: direction is decided once, so a vertical scroll never moves a
field however it drifts. Shift is 1/10 speed. Ctrl moves every array element, not Alt as on the web, since Option +
scroll is macOS zoom on the owner's machine.

## Consequences
- Only one window records at a time, and the last lease wins. There is only one mouse, so this hasn't mattered.
- Anyone using Unity's IMGUI Debugger window will have its session ended when a lease expires.
- 30 ms is assumed to be enough for the first repaint. A slower one reads stale rects, or none and retries for 250 ms.
- It is on by default, per machine, under **Biomes > Scroll Sideways To Edit Inspector Fields**.

## Related
[[0020-mft-hover-knob-imgui-debugger-data]] · [[../sessions/2026-11-05-inspector-scroll-fine-control]] ·
`Assets/Workspace/11.0 Biomes/src/Editor/ImguiPropertyRects.cs` · `Assets/Workspace/11.0 Biomes/src/Editor/InspectorScrollScrub.cs`
