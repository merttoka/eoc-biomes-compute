# Design: Termite ballistic streams, visible trails, firing shockwaves, dispersal channel

Date: 2026-06-11
Scope: `Assets/Workspace/11.0 Biomes`
Status: Approved forks, pending spec review

## Summary

Four coupled features for the 11.0 Biomes simulation, plus answers to three
standing questions (injection flow, `externalInfluenceTex` wiring, trail
packing). The features compose: removing termite steering (1) makes trails a
pure visual record that needs help to read (2); neuron firing becomes a physical
scatter event (3) driven by a new dispersal biome channel (4).

A deliberate departure from the reference (`PDE_Nefeli_Termites`) is recorded
explicitly: the reference *does* re-steer termites every frame via 3-sensor
chemotaxis (`Agent_Class.pde:24-35`). We remove that. Termites become ballistic
streams. This kills emergent trail-following stigmergy for termites — the trail
no longer feeds back into behavior. This is intended.

---

## Feature 1 — Termite fixed-heading ballistic streams

**Goal.** Each termite flies a fixed random heading set at init and kept for
life; no pheromone chemotaxis. Agents sharing a neuron index (`i % neuronCount`)
share the SAME heading → 131 coherent directional streams. Keep the reference's
±0.05 rad organic wiggle. Keep the firing speed boost and dotted white firing
trail (faithful to reference).

**Current state.** `TermiteSim.compute`:
- `ResetAgentsKernel:73` seeds heading per-agent: `Hash1u(id.x * 747796405u + time)`.
- `MoveAgentsKernel:151` calls `SensorTurns` (3-sensor chemotaxis + biome
  perception R) every frame, then wiggle (`:154`), then biome speed + firing.

**Changes.**
1. **Per-neuron heading seed.** In `ResetAgentsKernel`, seed the hash from the
   neuron index, not the agent index: `Hash1u((id.x % neuronCount) * 747796405u + time)`
   (fall back to `id.x` when `neuronCount == 0`). All agents on a neuron get one
   heading.
2. **Remove chemotaxis steering.** In `MoveAgentsKernel`, replace the
   `SensorTurns` call with the agent's stored heading. The pheromone trail
   sensing in `SensorTurns:106-126` becomes dead code for termites and is
   stripped (the function is retained only if reused by the dispersal-flee term
   below; otherwise deleted).
3. **Keep wiggle** (`:154-155`) unchanged.
4. **Keep** biome speed multiplier (`:159`) and firing speed (`:160`) — these are
   not steering and stay.

**New termite move rule (per frame):**
```
heading   = normalize(a.direction)              // fixed, from init
heading   = RotateVectorBy(heading, wiggle)     // ±0.05 rad
[dispersal-flee override — see Feature 4]
speed     = moveSpeed * biomeSpeedMult * firingMul
a.position += heading * speed
```

Heading magnitude is preserved frame-to-frame (re-normalize before applying
speed, since `a.direction` currently stores `heading * speed`).

---

## Feature 2 — Visible termite trails

**Goal.** The termite pheromone trail reads clearly in the composite, not just
the permeability mounds.

**Current state.** The trail render path already exists: `WriteTrailsKernel`
deposits, `DiffuseTextureKernel` fades, `RenderKernel:230-248` colors it into
`outTex`, and `SimulationManager.compute` blends it additively. It's faint
because render brightness caps at `0.8 * baseB` (`:240`) and, once steering is
removed, 80k straight agents spread thin while `diffuseRate 0.97` erases streaks
faster than they accumulate.

**Changes (tuning, no new kernel).**
1. `diffuseRate` → ~0.99 (slow fade, minimal blur) so the 131 streams stay
   line-like and persistent.
2. Raise `depositAmount` / `depositProbability` so lines accumulate visibly.
3. Render brightness `0.8 * baseB` → full `baseB` (`RenderKernel:240`).
4. Verify the termite sim's entry in `simWeights` is non-zero in the current
   scene; give termites a distinct hue from Boids/Physarum.
5. Optional `renderPersistence` bump for slight afterglow on the streams.

Exact values are tuned live; ranges already exist in `TermiteParams.cs`.

---

## Feature 3 — Neuron firing as expanding shockwave + agent scatter

**Goal.** Firing is dramatic and legible: an expanding ring AND a physical
scatter of nearby agents.

**Current state.** `NeuronRingKernel:88-111` draws a *static-radius* gaussian
ring per firing neuron over the composite, brightness/radius lightly pulsing with
intensity. Firing agents also move 2× and deposit white dotted trails.

**Changes.**
1. **Expanding shockwave (visual).** In `NeuronRingKernel`, drive ring radius by
   the firing decay envelope so it grows as the neuron's intensity fades:
   `r = ringRadius * (1 + expandGain * (1 - saturate(f)))`, plus a bright core
   flash at onset (high `f`). Reuses existing `ringFiring` / `ringPositions`
   buffers — near-zero added cost.
2. **Physical scatter (behavior).** Firing injects a dispersal pulse into the new
   agitation channel at the neuron's location (Feature 4). All three sims flee
   down the dispersal gradient → agents physically blow outward from firing
   neurons. The dispersal stamp radius expands with the same decay envelope so
   the scatter front tracks the visible ring.

---

## Feature 4 — Dispersal (agitation) biome channel

**Goal.** A new channel that, where high, scatters all sims rapidly. Triggered
both by neuron firing (local pulses) and global OSC injection. Agent effect:
flee down-gradient + speed burst. (Humidity: explicitly out of scope.)

**Channel definition.** Add `BiomeChannel.Dispersal = 10`, `Count → 11`, name
`"Dispersal"` (`BiomeFieldConfig.cs:8-28`). New `FieldChannelSettings`:
- `diffuseRate` ~0.9 (some spread so the gradient is smooth),
- `decayRate` high (~0.1) so pulses fade rapidly = "rapid dispersal",
- `relaxRate` 0, `initialValue` 0, `advectedByFlow` false.

**All hardcoded `Count == 10` sites must update.** Notably
`ExternalTextureSender` `ChannelNames` (recent commit `8074abe` fixed a
9-vs-10 mismatch — add the 11th name), the field texture-array allocation in
`Biome.cs`, and any per-channel buffer uploads.

**Triggering (both paths).**
1. **Firing-driven local pulses.** A new injector path builds stamps from the
   firing buffer (`ringPositions` + `ringFiring`, ≤131 neurons) and feeds them
   through the existing `Biome.InjectSources` / `InjectStampKernel` pipeline,
   targeting `CH_DISPERSAL`. Stamp radius expands with the decay envelope
   (tracks the shockwave ring). Reuses the stamp pipeline — no new kernel.
2. **Global OSC override.** A `BiomeInjector.Source` targeting `CH_DISPERSAL`
   with a full-field flat stamp (large radius / falloff ≈ 0) so an OSC value
   floods agitation everywhere at once.

**Agent effect — flee down-gradient + speed burst.**
- **Boids & Physarum:** add `CH_DISPERSAL` reads to their `UmweltMapping`:
  negative-weight Chemotaxis (steer away from high dispersal = flee
  down-gradient) + positive-weight Speed (burst). No shader change — this is the
  existing perception mechanism (`perceptionTex.r` chemotaxis, `.g` speed).
- **Termites:** steering was removed in Feature 1, so they need a dedicated
  dispersal-flee term. When dispersal at the agent exceeds a threshold, sample
  dispersal at the 3 sensor positions and steer toward the lowest, scaled by
  local intensity; otherwise keep the fixed heading. Speed burst from the same
  channel. This is the ONLY input that bends a termite off its fixed heading →
  calm streams that explode outward on firing, then re-straighten.

---

## Answers to standing questions (recorded, no work implied)

**Biome injection (Q5).** External scalars → `BiomeInjector.SetValue` →
calibrate `(raw-inputMin)/(inputMax-inputMin)` clamp01 (`:118-123`) → EMA smooth
(`:168`) → pack Gaussian `Stamp` (uv, radius, falloff, channel, amount, mode) →
`Biome.InjectSources` → `InjectStampKernel` (`Biome.compute:330-352`), three
modes (Additive / MaxToward / SetToward). Runs after sims write trails, before
`Biome.Step()`, so injected values ride the full PDE evolution. Homeostatic
relaxation (`relaxRate`) holds channels at ambient baselines instead of
saturating.

**externalInfluenceTex.** `ExternalTextureReceiver` pulls a texture over
Syphon/NDI/Spout; `SimulationManager` assigns it to each sim; Physarum samples by
UV, Boids by integer index. No real bypass today: when unused, a dummy texture is
bound and strength zeroed, but **every agent still samples it every frame**. Fix:
gate the sample behind a shader keyword (`multi_compile _ EXTERNAL_INFLUENCE`),
enabled only when a real source is connected — removes the per-agent fetch
entirely when idle. Cheap; can fold into this work.

**Trail packing into one ARGBHalf.** Not recommended. ~33% trail-memory saving
but ~2–3% GPU time, and it touches every deposit/diffuse/render kernel in 11.0.
Fails the "only if significant FPS" bar. Shelved.

---

## Out of scope
- Humidity channel (revisit later).
- ARGBHalf trail packing (shelved).
- Pheromone_2 autocatalysis (already removed by user; staying removed).

## Implementation order
1. Feature 1 (termite ballistic streams) + Feature 2 (trail tuning) — coupled.
2. Feature 4 (dispersal channel + injection + Umwelt/termite flee).
3. Feature 3 (shockwave ring + wire firing→dispersal stamps).
4. Optional: `externalInfluenceTex` keyword gate.

## Unresolved questions
1. Dispersal speed-burst magnitude — same `firingSpeedMul` (2×) or a separate, larger value?
2. Global OSC dispersal — single field-wide flat value, or a few placeable stamp sources?
3. `externalInfluenceTex` keyword gate — include now or defer to its own pass?
