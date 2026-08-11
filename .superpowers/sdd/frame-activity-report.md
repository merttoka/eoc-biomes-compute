# Frame-activity gating for burst-on-frame-advance

## Value-range evidence

Decoded `Assets/StreamingAssets/biomes11/organoid_firing.f16` directly (magic `TFR1`,
neuronCount=131, frameCount=180000, matches file size exactly — real binary data, not an LFS
pointer) and computed distribution stats over the full `180000 x 131` array:

- global min/max/mean/std: `0.0 / 1.0 / 0.0413 / 0.1253`
- percentiles (1/5/25/50/75/95/99): `0 / 0 / 0.0023 / 0.0041 / 0.0093 / 0.2551 / 0.7222`
- fraction of values `== 0`: 7.0%; fraction `< 0.01`: 75.9%
- fraction of values `> 0.5`: 2.0% (global); fraction `> 0.9`: 0.37%
- 10-bin histogram over [0,1]: `[21272974, 810306, 497822, 354877, 162380, 111149, 94940, 91261, 96568, 87723]`
  — dominant mass in the lowest bin, but a continuous, non-empty tail across every bin up to 1.0
  (not a 0/1 bimodal spike pattern).
- per-frame MEAN: ranges `0.0 .. 0.209` across all 180000 frames (mean of means 0.0413).
- per-frame FRACTION-above-0.5: ranges `0.0 .. 0.183` across all 180000 frames.
- correlation(frame mean, frame frac>0.5) = 0.875 — the two aggregates broadly agree, but
  frac>0.5 discards magnitude information below 0.5 and has a much lower ceiling.

**Conclusion: continuous, not spiky/binary.** Values span the full 0..1 range smoothly
(supported also by `neuron_firing.hlsl`'s `firingThreshold` default of 0.1 and `>=` comparison,
which only makes sense against a continuous signal), just heavily right-skewed toward 0 —
consistent with a real firing/calcium-style signal where most neurons are quiet most of the
time and only a few spike per frame.

## Aggregate chosen: MEAN

Per the task's own decision rule (continuous 0..1 -> MEAN), and confirmed by the data:
`frac > 0.5` would rarely leave 0 even on the busiest observed frame (max 18.3%), so it would
need per-installation recalibration to a threshold near 0.1-0.2 and would throw away the
magnitude of sub-0.5 activity entirely. MEAN stays monotone with synchrony/strength either way
(more neurons firing, or the same neurons firing harder, both raise it), uses the full
continuous range the data actually has, and needs no rescaling to be meaningfully compared
against `burstFiringThreshold`'s existing 0..1 range (shared with the edge-mode threshold on
`neuronIntensity`, also 0..1).

Implemented in `NeuronFiringSource.UpdateFiring()`: accumulate `sum` of `_row[i]` while decoding
the frame, `_frameActivity = sum / neuronCount`. Reset to 0 in `Initialize()` alongside
`_currentFrame`/`_intensity`. Exposed as `public float FrameActivity`. `debugLog` line now
prints `frame=<n> activity=<f3>`.

## Self-review trace

(a) **Dense stream of weak frames** (activity 0.2, threshold 0.6, `burstOnFrameAdvance` on):
`frameFired` is true every step (frame stamp changes each call), but
`neuronFrameActivity >= burstFiringThreshold` is `0.2 >= 0.6` = false, so `trigger` is false
every step. `TriggerBurst()` never called. `_burst` stays `Idle`. No bursts ever. Correct.

(b) **Strong frame arrives from idle** (activity 0.8 >= 0.6): `frameFired` true, activity gate
true -> `trigger` true -> `TriggerBurst()` -> `_burst.Trigger()` returns `true` (was Idle) ->
grid re-seeds, `Phase=Attack, Age=0`. Burst ramps up (fadeInSteps) then holds
(`burstSustainSteps`) from that trigger's `Age=0`. **Continued strong frames extend**: each
subsequent qualifying frame calls `TriggerBurst()` again; `BurstEnvelope.Trigger()` resets
`Phase=Attack, Age=0` again (Value is already 1 in Sustain, so Attack collapses back to Sustain
within a step or two) — net effect: the sustain-expiry countdown restarts from the latest
strong frame, keeping the burst alive without re-clearing the lattice work already in
`stateRead`/`stateWrite` (`Trigger()` only signals reseed when transitioning from Idle).
**Weak frames mid-burst do NOT extend**: when `neuronFrameActivity < burstFiringThreshold`,
`trigger` is false regardless of `frameFired`, so `TriggerBurst()` is skipped and
`BurstEnvelope.Age` keeps counting up from the last strong-frame trigger toward
`fadeIn + sustain`, then `Release`. This is the natural consequence of `trigger` gating
`TriggerBurst()` calls only — `_burst.Advance()` still runs every step regardless — and is
called out explicitly in the `burstOnFrameAdvance` tooltip ("because sustain runs from the LAST
qualifying frame, a run of weak frames mid-burst does not extend it and the burst still fades
on schedule").

(c) **`burstOnFrameAdvance` off**: `trigger = edgeFired` (frame-advance branch never taken).
`edgeFired` is still computed identically to before (`_firingEdge.Update(neuronIntensity,
burstFiringThreshold)`, unchanged call/order aside from being computed after `frameFired` now —
no data dependency between the two, so order doesn't matter). `_frameAdvance.Update(neuronFrame)`
still runs every step (both trackers update unconditionally, per spec) but its result is unused
when the mode is off. Edge-mode behavior is bit-for-bit identical to before.

(d) **No `NeuronFiringSource` wired**: `SimulationManager.Step()` broadcasts
`firingFrameActivity = neuronFiring != null ? neuronFiring.FrameActivity : 0f` -> `0f` to every
sim's `neuronFrameActivity`. Also `neuronFrame` stays `-1` (default), so `frameFired` is false
every step (`FrameAdvance.Update` returns false for negative stamps) independent of the activity
gate — the frame path never triggers, by two independent guards (frame never advances, and even
if it did, activity 0 < any positive threshold).

## Files changed

- `Assets/Workspace/11.0 Biomes/src/components/network/NeuronFiringSource.cs` — `_frameActivity`
  field, `FrameActivity` property, MEAN computed in `UpdateFiring()`, reset in `Initialize()`,
  debugLog includes activity.
- `Assets/Workspace/11.0 Biomes/src/components/core/SimulationBase.cs` — `[NonSerialized] public
  float neuronFrameActivity;` alongside `neuronFrame`.
- `Assets/Workspace/11.0 Biomes/src/components/core/SimulationManager.cs` — broadcasts
  `neuronFiring.FrameActivity` (or 0f) to every sim each step, alongside the existing
  intensity/frame broadcast.
- `Assets/Workspace/11.0 Biomes/src/components/core/FieldSimulationBase.cs` — `Step()` burst
  branch: explicit mode split (frame-advance REPLACES edge trigger, gated by
  `neuronFrameActivity >= burstFiringThreshold`); updated tooltips on `burstFiringThreshold` and
  `burstOnFrameAdvance`.
- `docs/ARCHITECTURE.md` — §3.4 trigger sentence updated to describe the replace-not-add
  semantics and the aggregate-activity gate.

Not staged/committed: `Assets/Workspace/11.3 SIGGRAPH DAC Scene/Scene_DAC.unity`,
`.../assets/CyclicCAParams.asset`, `.../assets/UmweltBoid_Alt.asset` (pre-existing dirty scene
files, unrelated to this task, per constraint not to stage scene files), and untracked
`ProfilerCaptures/`.

## Commit

`feat(sim): frame-advance bursts gate on per-frame firing strength — dense streams become
sporadic bursts` — see git log for SHA.
