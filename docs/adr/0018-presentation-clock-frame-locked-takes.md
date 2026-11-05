---
status: accepted
date: 2026-11-04
tags: [adr, recording, clock, firing, playback]
related: [[../ARCHITECTURE]], [[../sessions/2026-11-04-mit-brain-scene]], [[0019-unreleased-datasets-stay-local]]
---
# ADR-0018: Presentation-paced effects run on one clock that follows the capture clock during a take

## Context
Unity Recorder and `FigureExporter` set `Time.captureDeltaTime`. Game time then advances one fixed
step per rendered frame, while unscaled time keeps following the wall.

The sims step on game time (`FixedUpdate`), so they follow a capture. Firing playback, firing decay,
palette fades and output fades ran on unscaled time, so in a take they ran ahead of the sims by the
ratio of capture rate to render rate. A recorded firing pass did not match its data.

Options:
- Move those effects to game time. This breaks live use, because `Time.timeScale` would desync the
  blob from a video playing alongside it.
- Branch on capture state at each call site. Every new effect would have to repeat the check.
- One small clock that every presentation-paced effect reads.

## Decision
`PresentationClock` (`src/components/utils/`) is that clock. `DeltaTime` and `Now` return wall time
live and game time while `Time.captureDeltaTime > 0`.

Consumers: `NeuronFiringPlayback`, `NeuronFiringSource` decay, the backbone scaffold,
`AgentPaletteCycler` ticks and the manager's output fade.

Anything keyed to the show's data reads the playhead, not a clock: `NeuronFiringCues` fire on blob
frames, and `NeuronFiringParamMorph`'s weight is a pure function of the frame.

`LoopTakeRecorder` (Editor) records on this basis: constant frame rate from `recorderTarget`, with a
pre-roll through the end of the loop so the sims carry end-of-pass state into the first recorded frame.

## Consequences
- A take matches the data at any render speed. Live behaviour is unchanged.
- `Now` jumps when a capture starts or stops. Consumers that difference it clamp at 0.
- New presentation-paced effects must use `PresentationClock`, not `Time.unscaledDeltaTime`.
- Not covered: the external OSC streamer and the debug video clip still pace by wall clock, and
  drift under a capture clock.
- No test covers the clock. Takes were checked by eye on excerpts; a full-length loop, and so its
  seam, has not been recorded yet.

## Related
[[../ARCHITECTURE]] §3.7–3.8 · [[../sessions/2026-11-04-mit-brain-scene]]
