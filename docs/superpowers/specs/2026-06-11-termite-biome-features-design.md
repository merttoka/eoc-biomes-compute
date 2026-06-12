# Design: Termite ballistic streams, visible trails, firing shockwaves, dispersal channel

Date: 2026-06-11
Scope: `Assets/Workspace/11.0 Biomes`
Status: Approved forks, pending spec review

## Summary

Four coupled features for the 11.0 Biomes simulation, plus answers to three
standing questions (injection flow, `externalInfluenceTex` wiring, trail
packing). The features compose: per-group termite identity (1) plus trail tuning
(2) make distinct curvy streams legible; neuron firing becomes a physical scatter
event (3) driven by a new dispersal biome channel (4).

**Revision (2026-06-11, post-F1-verify):** the original F1 removed chemotaxis to
make termites fly straight. The user wants the opposite — termites keep their
curvy/wavy chemotaxis behavior (true to `PDE_Nefeli_Termites`), but each neuron
group (`i % neuronCount`) carries its OWN fixed random *turn-angle magnitude*
instead of one global turn angle. So this is now *closer* to the reference, not a
departure: the only change is per-group turn angles (via `turnAngleSpread`) plus
a per-neuron heading seed. Termite count drops to 131 (1:1 with neurons, matching
the reference's `numTermites`) — also the main termite perf lever, since the
3-sensor sampling is the dominant termite GPU cost.

---

## Feature 1 — Per-group termite turn angles (curvy streams) — IMPLEMENTED

**Goal (revised).** Termites keep their reference chemotaxis (3-sensor sensing →
curvy/wavy stigmergic paths). Each neuron group (`i % neuronCount`) gets its own
*fixed random turn-angle magnitude*, set deterministically and constant for life,
replacing the single global turn angle. At 131 agents each termite is its own
group. Keep the ±0.05 rad wiggle, firing speed boost, and dotted white firing
trail. Per-neuron heading seed so each group starts coherent.

**Changes (shipped in commit `96a8bef`).**
1. **Per-neuron heading seed.** `ResetAgentsKernel`: `headingSeed = (neuronCount>0)
   ? id.x % neuronCount : id.x`, hash that for the initial heading.
2. **Keep `SensorTurns`** (3-sensor chemotaxis + perception R) — restored, not
   removed. Inside it the turn magnitude is now per-group:
   ```
   grp  = (neuronCount>0) ? id.x % neuronCount : id.x
   tRand = Hash1u(grp * 2246822519u + 9871u)            // deterministic per group
   tang = p.turnAngle * lerp(1-turnAngleSpread, 1+turnAngleSpread, tRand)
   ```
   `turnAngleSpread` is a new global uniform (C# field on `TermiteSim`, default
   0.8). 0 = all groups use the global turn angle; 1 = groups span 0..2× base.
3. **Keep wiggle, biome speed, firing speed** exactly as the reference.
4. **`agentsCount` default → 131** (1:1 with neurons). Set it in the scene's
   `TermiteSim` inspector (serialized there overrides the code default).

Heading evolves frame-to-frame via the steering (curvy), as in the reference —
`a.direction` stores `direction * effectiveSpeed`, re-normalized next frame.

---

## Feature 2 — Visible termite trails

**Goal.** The termite pheromone trail reads clearly in the composite, not just
the permeability mounds.

**Current state.** The trail render path already exists: `WriteTrailsKernel`
deposits, `DiffuseTextureKernel` fades, `RenderKernel:230-248` colors it into
`outTex`, and `SimulationManager.compute` blends it additively. It's faint
because render brightness caps at `0.8 * baseB` (`:240`), and with only 131
agents the deposited trail is sparse, so `diffuseRate ~0.97` erases the curvy
streaks faster than they build.

**Changes (tuning, no new kernel).**
1. `diffuseRate` → ~0.99 (slow fade, minimal blur) so the 131 curvy streams
   persist and read as continuous paths.
2. Raise `depositAmount` / `depositProbability` so the sparse 131-agent trail
   accumulates visibly.
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
   floods agitation on command; its radius is parameterized like any other
   source.

**Parameterized intensity (resolved).** Dispersal is not a fixed slam. Exposed
params: `dispersalRadius` (base stamp radius, px), `dispersalExpandGain` (how
much radius grows over the decay envelope), and `dispersalAmount` (max scatter
strength). The injected stamp amount scales with the firing intensity `f`:
`amount = dispersalBaseline + (dispersalAmount - dispersalBaseline) * saturate(f)`,
where `dispersalBaseline` is small. So a weak fire nudges; a strong fire blows
agents outward. The OSC path uses its own source `amount` as the intensity input.

**Agent effect — flee down-gradient (simplified by the F1 revision).**
All three sims now flee via the SAME mechanism, because termites kept their
`SensorTurns` (3-sensor perception read). Add a `CH_DISPERSAL` negative-weight
Chemotaxis read to each sim's `UmweltMapping`. The `ReadFieldKernel` folds it into
`perceptionTex.r` (low where dispersal is high), and every sim's existing sensor
steering then turns away from the pulse = flee down-gradient. **No per-sim shader
change, no dedicated termite flee term, no `dispersalResponse` uniform** — the
original plan's Task 6 is dropped, and termite Umwelt is *appended to*, not
stripped (keeps its nutrient/pheromone chemotaxis for curvy behavior).

Speed burst **reuses `firingSpeedMul`** — firing agents already move faster; a
firing-driven pulse therefore scatters AND speeds the agents on the firing
neuron. (`SpeedPenalty` can only attenuate, so a dispersal-magnitude boost for
non-firing agents would need a new effect type — deferred.)

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
- `externalInfluenceTex` keyword gate (deferred to its own pass).

## Implementation order
1. Feature 1 (termite ballistic streams) + Feature 2 (trail tuning) — coupled.
2. Feature 4 (dispersal channel + injection + Umwelt/termite flee).
3. Feature 3 (shockwave ring + wire firing→dispersal stamps).

## Resolved decisions
1. Speed burst reuses `firingSpeedMul` (no separate param).
2. Dispersal radii parameterized (`dispersalRadius`, `dispersalExpandGain`);
   scatter strength scales with firing intensity `f` from a small baseline
   (weak fire nudges, strong fire scatters). OSC source uses its own amount.
3. `externalInfluenceTex` keyword gate deferred to a later pass.
