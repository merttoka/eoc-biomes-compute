# Interaction Design II — richer coupling + outside-signal expressivity

> Speculative planning note (not yet implemented). Follow-on to `INTEGRATION_DESIGN.md`,
> written after the **Humidity** channel shipped (field is now 12 channels). Same lens:
> grounded in the actual kernels/structs, staged by cost/risk, with a clamp + a knob per term.
> Goal framing from the brief: (1) **communicate better with outside signals** (audio,
> organoid firing, sensors); (2) **drive richer visual pattern change from the fields** — let
> fields modify agent params more freely, and/or make lifecycle (death → succession) real.

---

## 0. Where the coupling actually bottlenecks today (read this first)

Four interaction surfaces, mapped to the real code. Three are rich and cheap to extend; one
is a hard bottleneck that quietly caps everything the brief is asking for.

| Surface | Mechanism (code) | State |
|--------|------------------|-------|
| **Field → field** | `InteractFieldsKernel` couplings + `GenerateFlow`/`Advect`/`Diffuse` (`Biome.compute`) | Rich, ~free on the 320×180 decimated grid. Easy to grow. |
| **Agent → field** | `UmweltMapping.writes` (deposit/consume) + `metabolicHeat`→Temp, `oxygenConsumption` | Open; add rows freely. |
| **Field → agent** | **4 fixed perception slots** — `ReadFieldKernel` packs every read into `perceptionTex` RGBA: **R**=chemotaxis dir, **G**=speed-down, **B**=avoidance, **A**=speed-boost. Effects enum `UmweltEffect` has exactly these 4. | **THE BOTTLENECK.** Every field influence on an agent must collapse into one of four summed scalars. This is *why* "fields can't change agent params freely." |
| **Agent → agent** | Indirect only — stigmergy through shared pheromone channels (`Pheromone_0/1/2`) + the one summed `avoidance` scalar. No direct agent↔agent. | Sparse; the `B` channel conflates all antagonisms into one number. |
| **Outside signals** | `BiomeInjector` Gaussian stamps via OSC `/inject/<name>` (calibrated, smoothed, dropout-guarded); neuron firing → Dispersal pulses (`firingBuffer`); `externalInfluenceTex` (Syphon/NDI) = **dead wiring**, sampled but keyword-gated off. | One good primitive (stamps) + one dead path to revive. |

**The single highest-leverage move** for *both* goals is to widen the field→agent surface
(§B1): once a field can drive turn rate / sensor geometry / deposit rate / size / color — not
just the 4 steering scalars — every outside signal routed into a channel (§A) *also* gets to
reshape behaviour, and lifecycle (§B2) becomes expressible. Everything else is incremental
on top of that.

---

## A. Better communication with outside signals

All three drivers (audio, organoid firing, sensors) already have a front door: the
`BiomeInjector` stamp. The work is (a) richer *targets* for a stamp than "scalar into a
channel," and (b) reviving the dead texture path for spatial sensors.

### A1. Audio → field, beyond a single stamp *(field←external; serves Goal 1)*
- **Today:** an audio engine can push one scalar to one channel via `/inject/audio` (the
  example "audio → Dispersal" source already exists, `BiomeInjector.AddExampleDispersalSources`).
- **Next — band-split routing:** map an FFT's bands to *different channels* — bass →
  Temperature (drives flow + Q10 + evaporation = the whole room pulses on the kick), mids →
  Nutrient blooms, highs → Dispersal scatter. Pure config: N injector `Source` rows, each an
  OSC address from a TD/Max audio analyzer. **Zero code.** Highest ratio of payoff to effort.
- **Beat → procedural envelope:** a "Procedural" value source (the design's §Also "diurnal
  sun = just another stamp") driven by a tempo phase, so the field breathes on the bar even
  without continuous OSC. Needs the procedural-source type (small `BiomeInjector` addition).

### A2. Organoid firing → more than Dispersal *(field←external; serves Goal 1 + 2)*
- **Today:** firing injects Dispersal pulses (scatter) at neuron/agent positions
  (`firingDispersalSource`). One semantic: agitation.
- **Next — per-neuron-group channel routing:** the firing vector already carries per-neuron
  intensity (`ScaledValues` / `firingBuffer`). Route *groups* of neurons to *different*
  channels via a neuron→channel table: one cluster fires Nutrient (bloom where it lit up),
  another fires Temperature (heat front), another Dispersal (rupture). Turns "the organoid
  lit up" from a uniform scatter into a *legible, located, differentiated* response. Reuses
  `InjectStampKernel`; needs a small per-group routing table beside the existing neuron→uv one.
- **Firing → trail scar** (from `INTEGRATION_DESIGN` Part 3 (b)): inject firing into a sim's
  `trailReadArray` to rupture established veins so the transport network visibly re-routes —
  a graphic register distinct from the ecological one. Choose per neuron group.

### A3. Spatial sensors → field (revive `externalInfluenceTex`) *(field←external; serves Goal 1)*
- **Today:** `externalInfluenceTex` is assigned every frame but never sampled (gated off) —
  confirmed dead wiring.
- **Next — Texture-valued stamp source:** add a `Texture` value mode to `BiomeInjector.Source`
  that samples a TD-painted / depth-camera texture into a channel (depth silhouette →
  Temperature, projection mask → Nutrient). Makes a camera/Kinect silhouette a first-class
  spatial driver instead of a single point. This is the design's open §5e item; it retires the
  dead path by turning it into a real one. Medium effort (one stamp mode + a sampler).

> **Threading rule for all of A:** OSC callbacks may fire off-thread; keep using the
> `Source.value` + `valueDirty` volatile pattern (`BiomeInjector` already does this) — write on
> the callback, consume in `Inject` on the main thread. Never dispatch GPU from a callback.

---

## B. Richer visual pattern change from the fields

### B1. Generalize field → agent: let fields drive *any* agent param *(field→agent; serves Goal 2 — the big one)*

**Problem.** `UmweltEffect` is 4 hardcoded meanings and `ReadFieldKernel` packs into 4 RGBA
slots. A field can only ever steer, slow, repel, or boost. It can't widen a sensor, tighten a
turn, fatten a deposit, or recolour an agent — so the fields can't *sculpt behaviour*, only
*push position*. This is the ceiling the brief is hitting.

**Direction — a small effect→param bus instead of fixed RGBA.** Two stage options:

1. **Cheap, immediate (widen the vector to 8 and add effect types).** Make `perceptionTex` carry
   a second `float4` (a second RWTexture or a packed `ARGBHalf` pair) and add `UmweltEffect`
   entries that the sims already have inputs for:
   - `TurnRateMod` — field scales `sensorAngle`/turn strength (Physarum vein sharpness, boid
     cohesion radius) → fields carve **fine vs ropey** texture, not just direction.
   - `DepositMod` — field scales `UmweltWriteEntry.amount` at sample time → trails **thin out in
     dry/hostile zones, thicken in rich ones** (huge legibility win; today deposit is constant).
   - `SizeMod` / `ColorShift` — field drives agent point size / palette index → **visible
     phenotype** tracking the field (e.g. agents redden in heat, shrink in hypoxia). Pure
     render-side read, no sim-physics risk.
   Each is one branch in `ReadFieldKernel` + one consumer line in each sim's Move/Render. The
   per-sim consumer is the only real work; the perception plumbing is shared.
2. **Deeper, general (a typed param-modulation buffer).** Replace the fixed slots with a small
   `StructuredBuffer<ParamMod>{ targetParamId; value; }` the perception pass fills and each sim
   applies by id. This is the *right altitude* (no new RGBA slot per idea ever again) but is a
   bigger refactor touching all three sims' param structs. Do it only after option 1 proves
   which modulations read well — don't pay the abstraction cost up front.

> ⚠️ Stacking modulations re-introduces the mush/freeze failure modes (`INTEGRATION_DESIGN`
> Part 2 caveats): multiplicative speed terms drive →0, summed avoidance conflates. Keep **one
> meaning per perception slot per sim**, clamp every modulation, MIDI-knob each.

### B2. Lifecycle: make death → succession real *(agent state; serves Goal 2)*

**The scaffolding already exists and is unused.** `UmweltMapping` exposes `enableDeath`,
`deathThresholdOxygen`, `deathThresholdPermeability`, `corpseWasteAmount`, `corpseDecayRate` —
but mortality is **not executed** (README roadmap: "parked"). This is the brief's headline
aesthetic (bloom/collapse, not homeostasis) sitting one step from done.

- **No struct growth needed.** `typeId` is a `uint` using ~3 bits (≤8 types); pack `alive` /
  `dormant` lifecycle flags into its high bits (`INTEGRATION_DESIGN` Tier-2 💡). No 20-byte
  struct migration, no ripple to `WriteField`.
- **Mechanic:** high-metabolism Boids in hypoxia (`Oxygen < threshold`, free — no channel) flip
  `alive→0` → dump `corpseWasteAmount` into Waste → Q10 decomposition spikes Nutrient →
  next wave recolonizes. **Physarum doesn't die — it latches dormant** (halt-and-hold) until
  O₂/Nutrient return = true hysteresis (breathing), which a pure `SpeedPenalty` can't express.
- **Humidity ties in now:** gate desiccation death on `Humidity < threshold` too — the
  evaporation wake behind a heat front becomes a visible kill-zone that then re-greens as
  Humidity's relax refills it. Couples the new channel straight into the succession loop.
- **Cost/risk:** medium — needs agent-state writes + a respawn path. Highest *visual* payoff in
  the doc; do it after B1 (B1's `SizeMod`/`ColorShift` make death/dormancy legible —
  a corpse should visibly fade, a spore visibly shrink).

---

## C. Field ↔ field (cheap, on the decimated grid)

All land in `InteractFieldsKernel` next to the existing waste→nutrient / temp→perm /
temp→humidity couplings; each is a few lines + a knob, ~free.

- **Humidity → decomposition gate** *(Goal 2):* scale the Q10 `decompRate` by local Humidity
  (microbial activity needs moisture) — `decompRate *= saturate(humidity·k)`. Dry hot zones
  stop fertilising → fertility tracks the *moist* hot edge, not all hot ground. Tightens the
  travelling-front aesthetic the mush-fix started.
- **Permeability-as-topography (Laplacian stigmergy)** *(Goal 2):* the design's #6 — curvature
  of Permeability (neighbours already sampled) drives build/dig so mounds/galleries
  self-organize and persist. No new channel. Pairs with `|∇Humidity|` as the termite build cue.
- **Humidity ↔ Flow feedback** *(Goal 2):* let evaporation add a small upward/▽ bias to Flow
  (latent-heat convection) — moist→dry transitions stir the field, so the wake isn't passive.
  ⚠️ Flow saturates easily (`±1` clamp, weak decay); keep the gain tiny, co-tune with
  `temperatureToFlowStrength`.

---

## D. Agent ↔ agent (close the trophic loops)

Today antagonisms are one summed `avoidance` scalar that only Boids read. Make the web real:

- **Asymmetric predator/prey via the dead B channel** *(Goal 2, asset-cheap):* give Physarum &
  Termite real `Avoidance` reads and have those sims sample `perception.b` (3 lines each, mirror
  `BoidSim`). A-flees-B / B-ignores-A produces chase/retreat instead of mutual freeze.
  ⚠️ one avoidance *meaning* per sim — don't pile two antagonists into the one scalar.
- **Waste scavenging** *(Goal 2, asset-only):* Physarum/Boid get a `Waste +Chemotaxis` read →
  they forage the waste they emit; closes matter recycling at the agent level (today only the
  field recycles Waste, no agent eats it).
- **Mound autocatalysis** *(Goal 2, asset-only):* Termite reads its **own** `Pheromone_2
  +Chemotaxis` → aggregation on established galleries (slow-decay trail = medium-term memory).
- **Cross-species via outside signal** *(Goal 1+2):* once §A2 routes organoid-firing groups to
  per-species pheromone channels, the *organoid* becomes a fourth "species" steering the other
  three — the outside signal enters the agent↔agent web instead of sitting on top of it.

---

## E. Recommended sequence (most leverage first)

1. **§A1 audio band-split + §D asset-only loops** — zero/low code, immediate richness, reversible
   on knobs. Proves the routing ideas before any plumbing.
2. **§B1 option 1** — widen perception to 8 slots; add `DepositMod` + `ColorShift` first (most
   visible, lowest physics risk). *This unlocks the brief's "fields modify agent params freely."*
3. **§A3 texture-valued sensor source** (revive `externalInfluenceTex`) — spatial sensors become
   first-class; one stamp mode.
4. **§B2 lifecycle** (death→succession + Physarum dormancy, Humidity-gated desiccation) — the
   headline bloom/collapse aesthetic; do after B1 so it reads visually.
5. **§A2 firing→channel routing / trail scar**, **§C field↔field**, **§B1 option 2** (typed
   param bus) — generalize once the concrete versions prove out.

**Open questions for the artist:**
1. Audio routing — fixed band→channel map the artist tunes, or live-remappable per show?
2. Field→agent params — which phenotype reads do you want *visible* first (size, colour,
   trail thickness)? That decides B1's first effect types.
3. Lifecycle register — ecological/slow (succession blooms) vs graphic/immediate (trail scars),
   or both per species/neuron-group?
4. Organoid semantics — is firing an *agitation* (scatter), a *bloom* (nutrient), or a
   *rupture* (trail scar)? Probably per-group — needs the neuron→channel table.
