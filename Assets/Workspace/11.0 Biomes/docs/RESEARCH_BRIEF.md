# Research Brief — Grounding a GPU Artificial-Life Ecosystem in Real Biology

> Hand this to a deep-research agent. Goal: find literature that lets us map the
> **exposed simulation/environment parameters** of a GPU-compute artificial-life
> ecosystem onto the **real behavior and metabolism** of slime molds, termites,
> and flocking birds — so the simulation produces a believable *morphogenic*
> environment rather than arbitrary pattern.

---

## 1. What the system is (as currently implemented)

A Unity GPU-compute ecosystem. A single `SimulationManager` drives a
read–modify–write loop over a **shared chemical field** (the "Biome") sampled by
three independent agent simulations. Each agent species perceives the field
through an **Umwelt** (a per-species sensory/effector mapping) and writes back
into it (stigmergy). Everything runs on the GPU as ping-ponged textures + agent
buffers.

### 1.1 The environment ("Biome") — 10 scalar field layers

A double-buffered `Texture2DArray` (R16F), resolution independent of the agents.
Channels:

| # | Channel | Role (intended) |
|---|---------|-----------------|
| 0 | Nutrient | Food / chemoattractant; consumed by agents, replenished by waste decomposition |
| 1 | Pheromone_0 | Species-0 (physarum) scent |
| 2 | Pheromone_1 | Species-1 (boid) scent |
| 3 | Pheromone_2 | Species-2 (termite) scent *(recently added)* |
| 4 | Oxygen | Respiration substrate; consumed metabolically |
| 5 | Temperature | Metabolic heat accumulates here; drives convection (flow) and phase/permeability |
| 6 | Waste | Excreted by agents + corpses; decomposes → Nutrient (rate scales with temperature) |
| 7 | Permeability | "Solid vs open matter." **Low = solid/impermeable** (slows/should block). High = open |
| 8 | Flow_X | Advection velocity x (generated from temperature gradients) |
| 9 | Flow_Y | Advection velocity y |

**Per-channel parameters** (authored per layer): `diffuseRate`, `decayRate`,
`advectedByFlow` (bool), `initialValue`.

**Global cross-field couplings**: `wasteToNutrientRate` (decomposition),
`temperatureToFlowStrength` (convection), `temperatureToPermeability` (phase
change), `noiseScale` + `noiseThreshold` (procedural initial "matter map" for
Permeability via fractal simplex noise).

**Field dynamics per step (intended):** generate flow from temperature gradient
→ advect scalar fields by flow → cross-field interactions (waste→nutrient,
temp→permeability) → diffuse + decay.

### 1.2 The Umwelt (per-species sensory/effector contract)

Each species has an `UmweltMapping`:

- **reads**: list of `(channel, weight ∈ [-2,2], effect)` where `effect ∈
  {Chemotaxis, SpeedPenalty, Avoidance}`. These are collapsed into a per-pixel
  perception texture: **R = chemotaxis gradient signal, G = speed multiplier, B =
  avoidance**.
- **writes**: list of `(channel, amount ∈ [-1,1])` — positive deposits, negative
  consumes, at each agent's position.
- **Habitat**: `preferredPermeabilityMin/Max`.
- **Metabolism**: `metabolicHeat` (→ Temperature), `oxygenConsumption` (→ −Oxygen).
- **Lifecycle**: `enableDeath`, `deathThresholdOxygen`, `deathThresholdPermeability`,
  `corpseWasteAmount`, `corpseDecayRate`.

So an agent's ecological niche is fully data-driven: *what it smells, what it
flees, what slows it, what it eats/excretes, how much heat/oxygen it metabolizes,
and how it dies.*

### 1.3 The three agent systems

All three share a 20-byte agent struct (`position`, `direction/velocity`,
`typeId`), up to 8 sub-types each with their own parameters + color, a private
multi-layer trail texture (per-type + a summed layer), and a sense→steer→move→
deposit→diffuse loop. Each samples the Biome perception texture for chemotaxis
(R) and speed (G).

**A. Physarum (slime mold) — `PhysarumSim`**
Classic 3-sensor agent (Jones 2010 model). Per-type params:
`senseAngle`, `senseDistance`, `turnAngle`, `moveSpeed`, `depositAmount`,
`eatAmount`, `diffuseRate`, `hue`, `saturation`. Steering: sample own-type trail
vs. *total* trail at left/middle/right sensors → attracted to own species,
repelled by others; biome chemotaxis added. Deposits its own pheromone, eats
other species' trails. Can seed agents from a neuron-position CSV.

**B. Boids (flocking) — `BoidSim`**
GPU spatial-hash flocking. Per-type (13 params): separation / alignment /
cohesion(attraction) ranges + weights, `maxSpeed`, `maxForce`, food-seeking
strength, deposit/eat, diffuse, hue, saturation. Forces clamped to `maxForce`,
steered toward `maxSpeed`. Reads biome chemotaxis + avoidance + speed.

**C. Termites (stigmergy swarm) — `TermiteSim`**
Pheromone-following swarm (sense+turn like physarum, no eating). Adds a **"firing"**
mechanism: per-agent activation streamed from an external neural-recording blob
(`termite_firing.f16`); firing agents move faster (`firingSpeedMul`) and deposit
bright trails (`firingDepositAmount`, `firingDepositProbability`, `firingThreshold`).
Builds/erodes **Permeability** "mounds" via Umwelt writes and avoids them via a
negative-weight permeability read. Default ~13,100 agents.

### 1.4 Exposed parameters — full list to ground in biology

- **Physarum (per type):** senseAngle, senseDistance, turnAngle, moveSpeed,
  depositAmount, eatAmount, trail diffuseRate; population size; type count.
- **Termite (per type):** senseAngle, senseDistance, turnAngle, moveSpeed,
  depositAmount, depositProbability, firing{Threshold, SpeedMul, DepositAmount,
  DepositProbability}, trail diffuseRate; population size.
- **Boid (per type):** separationRange/Weight, alignmentRange/Weight,
  cohesionRange/Weight, maxSpeed, maxForce, foodSeekStrength, deposit/eat,
  trail diffuseRate.
- **Umwelt (per species):** chemotaxis/avoidance/speed read weights per channel,
  deposit/consume amounts per channel, preferredPermeability range, metabolicHeat,
  oxygenConsumption, death thresholds (oxygen, permeability), corpse waste
  amount + decay.
- **Biome (per channel):** diffuseRate, decayRate, advectedByFlow, initialValue.
- **Biome (global):** wasteToNutrientRate, temperatureToFlowStrength,
  temperatureToPermeability, noiseScale, noiseThreshold.

---

## 2. The research questions

I want each abstract parameter grounded in measured/observed biology so I can
set defaults and ranges that yield realistic morphogenesis. For **slime molds
(*Physarum polycephalum*), termites (mound-building *Macrotermes* and similar),
and flocking birds (e.g. starlings)**, please find literature on:

### 2.1 Sensing & movement
- **Slime mold:** chemotactic sensing geometry — how far ahead and at what angle
  do plasmodial fronts/agents sample gradients? Oscillation period, growth/front
  speed, branching/anastomosis rules. (Map to senseAngle/Distance, turnAngle,
  moveSpeed, deposit/diffuse.)
- **Termites:** trail-pheromone following — deposition rates, evaporation/
  half-life, sensing distance, the trail-vs-noise threshold that triggers
  recruitment vs. dispersal. Stigmergic building rules (cement pheromone, soil
  pickup/deposit probabilities). (Map to depositProbability, diffuse/decay,
  permeability writes.)
- **Birds:** empirically measured flocking metrics — interaction range
  (topological vs. metric, ~6–7 nearest neighbors in starlings), speed,
  turn/acceleration limits, density. (Map to separation/alignment/cohesion
  ranges + weights, maxSpeed, maxForce.)

### 2.2 Metabolism & environment interaction
- Per-organism **oxygen consumption** and **metabolic heat** (mass-specific
  metabolic rate). Termite mounds as respiration/ventilation structures — CO₂/O₂
  and temperature gradients they maintain.
- **Substrate / permeability**: which organisms modify their substrate (termite
  soil transport, slime-mold tube remodeling) and how the "preferred permeability"
  and erosion/accretion should be signed and scaled.
- **Decomposition / nutrient cycling**: realistic waste→nutrient conversion and
  temperature dependence (Q10 of decomposition).

### 2.3 Life & death
- Realistic mortality drivers: hypoxia thresholds, desiccation/temperature, food
  depletion. Corpse decomposition timescales feeding the nutrient cycle.
- Population dynamics: birth/death balance, density dependence — what closes the
  loop into a sustainable ecosystem rather than runaway growth or extinction.

### 2.4 Morphogenesis target
- What spatial/temporal patterns are the *signatures* of each system (Physarum
  transport networks, termite galleries/mounds, murmuration density waves), and
  which parameter regimes produce them? I want parameter **ranges** that sit near
  the interesting (critical / edge-of-chaos) regime.

---

## 3. What environmental layers might we be missing? (please advise)

Current layers: Nutrient, 3× Pheromone, Oxygen, Temperature, Waste, Permeability,
Flow(x,y). Candidate additions to evaluate against the biology:

- **Humidity / moisture** (critical for both slime molds and termite mound
  microclimate; arguably more important than temperature for these taxa).
- **Light** (phototaxis — slime molds are photophobic; birds are diurnal).
- **CO₂** as a distinct channel (termite ventilation; currently only Oxygen).
- **Substrate height / topography** (vs. flat permeability) for true mound
  morphogenesis and for boid terrain.
- **Soil/material load** carried by termites (distinct from generic waste).
- A **directional wind/flow forcing** that is independent of temperature (today
  flow is only convection-driven and tends to vanish).

Please recommend which of these are biologically load-bearing for the three taxa,
and whether any current channel is redundant or mis-modeled.

---

## 4. Answers from the author (constraints for the research)

1. **Fidelity:** *Qualitatively plausible* — right directions and relative
   magnitudes, not real units or allometric scaling. Prefer findings that give
   defensible *orderings* and *ratios* between parameters (e.g. "termite trail
   evaporates faster than slime-mold trail") over absolute measurements.
2. **Flocking species:** Boids are a **general flocking agent**, not committed to
   one species. Please survey the canonical anchors (starling murmurations —
   topological, ~6–7 nearest neighbors; fish schools; classic Reynolds boids) and
   recommend the best qualitative reference, or give a small parameter set per
   candidate.
3. **Termites:** **Both** foraging-trail stigmergy **and** mound construction are
   in scope — cover trail-pheromone dynamics *and* soil pickup/deposit (building)
   rules, and how they should share or split the pheromone vs. permeability
   channels.
4. **System goal:** A **driven art system** fed by external inputs — *not* a
   self-sustaining closed ecosystem. Biological equilibrium is not required;
   prioritize parameter regimes that yield rich, legible **morphogenesis** and
   respond expressively to external forcing. Birth/death is wanted later as an
   *aesthetic* mechanism (blooms/collapses), not for homeostasis.
5. **Spatial scale:** One field pixel = one **unit of environment space**; treat
   the system as **scale-independent** for now. Ground parameters in
   *dimensionless* terms (sensing distance ÷ body size, trail half-life in steps,
   etc.) rather than metric units.

---

*Implementation note for whoever wires the findings in:* flow (Flow_X/Flow_Y)
**transports the chemical fields** (advection) but is *deliberately not* coupled
to agent motion — agents are never pushed by flow, by design. The lifecycle/death
parameters are defined but not yet executed (birth/respawn is a planned later
addition, intended as an aesthetic bloom/collapse mechanism). So grounding the
death-cycle parameters will eventually require small engine changes, not just
data; everything else maps onto existing channels/params.


Research Agent Response: /Users/toka/Developer/Graphics/EoC-biomes-compute/Assets/Workspace/11.0 Biomes/docs/Simulating Biological Systems Parameters.pdf