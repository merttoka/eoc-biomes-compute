# Biome Layer & Sim Integration — Design Exploration

> Living design doc — **partly shipped** (see Part 4 ledger for per-item status).
> Synthesizes a 4-lens design panel (pragmatist / biology-maximalist /
> installation-architect / dynamical-art), each adversarially critiqued, grounded
> in the research PDF (`Simulating Biological Systems Parameters.pdf`) and the
> actual codebase. Three parts: (1) how the layers couple **today**, (2) making
> them interact **richer**, (3) an **external-input** injection architecture for
> the gallery. Shipped so far: the stamp **injector**, **Q10 + decay sinks +
> relax-to-baseline**, the **permeability runaway fix**, and the **Humidity
> channel** (consumer read still to wire). Last verified against code: 2026-06-23.

---

## Part 1 — How the biome layers interact *today*

Three coupling surfaces. Read this as the current wiring diagram.

### 1a. Field-internal (the PDE in `Biome.Step`, per step)
```
Temperature ──gradient×90°──▶ Flow_x/Flow_y          (GenerateFlow: circulation, mean-zero)
Flow ──advects──▶ Nutrient, Pheromone_0/1/2, Oxygen  (Advect: semi-Lagrangian)
Waste + Temperature ──▶ Nutrient                     (Interact: decomp ∝ (0.5+temp), LINEAR)
Temperature ──▶ Permeability                         (Interact: perm += (temp−0.5)·rate  ← integrator)
every channel ──▶ diffuse + decay                    (most channels decay = 0)
```

### 1b. Agents → field (deposits at agent positions, per Umwelt `writes`)
| Sim | deposits | consumes | heat (→Temp) | O₂ (−) |
|-----|----------|----------|--------------|--------|
| Physarum | Pheromone_0, Waste | Nutrient | low | low |
| Boid | Pheromone_1, Waste | Nutrient | **highest** | **highest** |
| Termite | Pheromone_2, **−Permeability (digs)** | Nutrient | mid | low |

### 1c. Field → agents (perception: R=chemotaxis, G=speed, B=avoidance)
| Sim | seeks (+) | flees (−) | speed-gated by |
|-----|-----------|-----------|----------------|
| Physarum | Nutrient | Pheromone_1 (boids), Pheromone_2 (termites) | Permeability |
| Boid | Nutrient, **Oxygen** | Pheromone_0 (physarum), **Waste (B)** | Permeability |
| Termite | Nutrient | Permeability (chemo) | Permeability |

### 1d. Emergent inter-species web
- Physarum ⊣ Boid (mutual pheromone avoidance).
- Physarum → flees Termite scent, but Termite **does not** avoid Physarum → **asymmetric** (the only chase/retreat asymmetry present).
- All three **compete for Nutrient** and all dump **Waste**, which the field recycles to Nutrient — but **no agent eats Waste directly**.
- Only Boids couple to Oxygen.

### 1e. Honest assessment — why it reads as "soup" not "ecosystem"
- **Loops are open.** The intended Boid-heat → nutrient → Physarum cascade has no *return arm* and no delay/integrator, so the field **relaxes to equilibrium**.
- **Mush by construction.** Nutrient/Oxygen/Waste have `decay=0` + constant deposits + `diffuse≈0.99` → they ramp to a flat clamped 1.0.
- **Flow is near-cosmetic.** Uniform Temperature ⇒ tiny gradients ⇒ weak flow; and agents never *feel* flow (perception has no flow vector — by design).
- **The B (avoidance) channel is computed for every sim but only Boids read it** (`BoidSim.compute:256`); Physarum/Termite ignore it, so antagonisms collapse into one chemotaxis scalar.
- **`temperature→permeability` is a runaway integrator** (the "disappearing permeability" you saw).
- **Pheromone_2 has no consumer loop** — termites don't aggregate on their own trail, so no mound autocatalysis.
- **`externalInfluenceTex` is dead wiring** — `SimulationManager` assigns it to every sim each frame (`:126`), but **no sim shader ever samples it**. The only live external path is the cosmetic composite overlay + the termite firing blob.

---

## Part 2 — Making the layers + sims interact richer

Staged by cost/risk. **Do them in order; ablate one at a time** (5 new interacting
knobs shipped at once is an authoring cliff and a path to mush).

### Tier 0 — Free, asset-only (UmweltMapping edits, zero code)
Close the open trophic loops by adding `reads`/`writes` entries:
- **Scavenging:** give Physarum/Boid a `Waste +Chemotaxis` read → they forage the waste they emit (closes matter recycling at the agent level, not just the field).
- **Termite mound autocatalysis:** Termite reads its **own Pheromone_2 +Chemotaxis** → aggregation on established galleries (the PDF's slow-decay trail = medium-term memory).
- **Predator/prey via the dead B channel:** add `Avoidance`-effect reads for Physarum/Termite **and** make those two sims actually sample `perception.b` (3 lines each, mirror `BoidSim:256`). Asymmetric avoidances (A flees B, B ignores A) produce chase/retreat instead of mutual freeze.

> ⚠️ Critic caveats: (a) `perception.b` is a **single summed scalar** — don't pile CO₂-avoidance *and* Waste-avoidance into it on Boids or they conflate. (b) `speedMod` is **multiplicative**; stacking SpeedPenalty reads drives speed→0 = frozen ecosystem. Keep speed couplings to one per sim.

### Tier 1 — Cheap shader, highest leverage (the "make it alive" engine)
1. **Q10 ignition + decay sinks.** ✅ **Shipped** (`Biome.compute:229`): the linear `decompRate = rate·(0.5+temp)` is now `rate·pow(2.74,(temp−0.5)·decompTempSpan)` so decomposition is near-zero when cold and *explosive* when hot → travelling fertility fronts instead of a smooth ramp. Sinks shipped as Waste `decay 0.001` + Nutrient `decay 0.0005`; Oxygen breathes via relaxation (→0.8) rather than decay. `decompRate` is clamped. ⚠️ Still: the front only *travels* if heat moves on — needs the **diurnal sun (row 6, pending)** or higher Temp decay, else you get a static max-fertility disk.
2. **Diurnal "sun" forcing.** Per the PDF, light is *not* a layer — inject a slow sweeping warm gradient into Temperature each step (phase from `SimStepCount`). This is the master off-equilibrium pump: it moves the hot zone → re-aims flow, re-fires Q10, drives evaporation, and gives the piece a global rhythm an audience reads in seconds. **Build it as a Temperature stamp in the pre-Step injector (Part 3), NOT a new pass** — keeps `Biome.Step` untouched.

> ⚠️ Flow accumulates (`existingFX + …`) with weak decay (0.02). A too-strong/too-wide sun can saturate Flow to ±1 → re-introduces a global drift / advection smear. Co-tune `temperatureToFlowStrength` ↔ flow decay; keep sun gain modest.

### Tier 2 — Medium (structural biology; do after Tier 1 reads as alive)
- **Humidity channel (PDF's #1 missing layer).** ✅ **Shipped** as `BiomeChannel.Humidity` (index 11, `Count → 12`): high-diffusion (0.97), flow-advected, relaxes toward an ambient 0.5 baseline (renewable), and Temperature evaporates it via `temperatureToEvaporation·max(0, temp−0.5)` in `InteractFieldsKernel` (`Biome.compute`). Makes Physarum *and* termites compete for a renewable, depletable resource, and gives termites their true build cue: **evaporation flux = |∇Humidity|** (sampled agent-side via `UmweltMapping` reads — wire per-scene). The one channel worth spending.
- **Permeability-as-Topography (no new channel).** Reinterpret Permeability as a height map; compute its **Laplacian (curvature)** in `InteractFieldsKernel` (neighbors already sampled): convex → build, concave → dig. Curvature-driven stigmergy replaces the (biologically obsolete) "cement pheromone" and makes **mounds/galleries self-organize and persist**. Needs a slow relaxation toward baseline so build/dig don't pin to 0/1.
- **Mortality → succession (the PDF's headline aesthetic).** Hypoxia/CO₂ (= `1−Oxygen`, free, no channel) kills high-metabolism Boids → Waste spike → residual heat → Q10 hyper-fertility → next wave recolonizes. Physarum **doesn't die** — it halts into a static spore until O₂/Nutrient return (true hysteresis = breathing).

> 💡 **`carryingSoil` / `alive` / `dormant` flags do NOT require growing the 20-byte struct.** `typeId` is a `uint` that only needs ~3 bits (≤8 types). Pack lifecycle flags into its high bits → **no struct migration, no ripple to the 3 sims + WriteField**. This de-risks the parked death/respawn work substantially.
>
> ⚠️ True dormancy/death needs *agent state* (the typeId bits above) — it is **not** achievable as a pure SpeedPenalty asset edit (that gives smooth slowdown, not a halt-and-hold latch).

### The feedback loops to aim for (the "alive" signatures)
- **Trophic front:** Boid swarm overheats a region → Q10 nutrient flash → Physarum floods in → consumes → region cools → front moves on (peristalsis of fertility).
- **Diurnal breath:** sun sweep → evaporation behind it, flow vortices under it, fertility in its wake.
- **Succession:** mass die-off → waste → fertilize → next generation (population blooms/collapses — the aesthetic the brief wants, not homeostasis).

### Failure modes to design against
Mush (saturation to flat 1.0) · runaway (Q10/flow blow-up) · dead equilibrium (over-avoidance voids, speed→0 freeze, global nutrient drain). Every Tier-1/2 term needs a clamp + a MIDI knob + a per-channel dynamic-range budget.

---

## Part 3 — External-input architecture (plants / robot / neurons)

**All four design lenses independently converged on the same answer**, which is a
strong signal: **one reusable spatial *stamp injector*.**

> ✅ **Implemented** (`feat/stamp-injector` → main): `BiomeInjector` component +
> `InjectStampKernel` + `Biome.InjectSources`, dispatched pre-Step in
> `SimulationManager.Step`. Live drive via OSC — `/inject/<name>` (value) and
> `/inject/<name>/pos` (u,v) through `OSCMapping` (see `MIDI_OSC.md`). Each source maps a
> physical location to a normalized biome UV + channel; default `MaxToward` blend avoids
> saturation; `valueTimeout` guards sensor dropout.

### The primitive
A `ComputeBuffer<Stamp>` + one per-texel `InjectStampKernel`:
```
struct Stamp { float2 uv; float radius; float falloff; int channel; float gain;
               float value; int mode; }   // mode: 0=Additive 1=Max 2=Set
```
Each texel accumulates a soft Gaussian disc from every stamp into the target channel.

### Placement (the one correctness rule)
**Write stamps into `fieldReadArray` *before* `Biome.Step()`, in the same slot
`WriteField` already uses** (`SimulationManager.Step`, between the umwelt-write
loop and `biome.Step()`; `Biome.cs:300` binds `fieldReadArray`). Then the
ping-pong's `CopyAllChannels` carries it through every pass. This:
- **cannot trigger the clobber bug** (it's upstream of `GenerateFlow`, not a pass inside the chain),
- needs **no swap and no parity reasoning** (the "5th-pass parity hazard" the panel raised is a *false alarm* — `DispatchFieldPass` swaps every call, so `fieldReadArray` always holds the latest output regardless of pass count; the real invariant is copy-through),
- avoids `WriteFieldKernel`'s non-atomic per-agent texel race,
- needs **no new biome channel and no Agent-struct change**.

A `BiomeSource` MonoBehaviour owns each stamp (explicit `fieldUV` 0–1 as the
source of truth + an editor gizmo to place it), with a value source of
`Constant | OSC | Procedural | Texture`.

### The three scenarios as instances of the one primitive
| Scenario | channel | value source | notes |
|----------|---------|--------------|-------|
| **Plant → O₂** | Oxygen (+ optional Humidity) | Constant or plant CO₂/light sensor (OSC) | advected + diffused into a drifting plume Boids flock to. **Use mode=Max or tiny gain** — additive+persistent saturates to a flat blob (mush). |
| **Robot + proximity** | **Temperature** | proximity sensor (OSC) → amount; arm pose → uv | Temperature is the *master* variable — one stamp ripples through flow + decomposition + permeability → the whole room visibly leans toward the visitor. Add a **value timeout / lerp-to-baseline** so a stuck/dropped sensor doesn't pin a permanent hot artifact. |
| **Neuron firing** | (a) field "alarm" stamp; (b) **trail-layer scar** | decoded firing vector + a **neuron→uv table** | (a) perturbs flow/physarum via existing reads; (b) injects into a sim's `trailReadArray` to *rupture* established veins/streams → network re-routes. |

> ⚠️ **Neuron honesty:** the existing termite `firingBuffer` is **per-agent** (`agent i → neuron i%131`) and tracks a *wandering* agent; neuron CSV positions only seed termite *spawn*. A location-accurate neuron stamp needs a **separate neuron→uv table** — it's a *different* aesthetic (fixed-location scar) from the existing wandering vibrational-alarm. Choose deliberately; don't try to "generalize the existing path" — it changes semantics.

### Also
- **Diurnal sun = just another stamp** (Procedural value source, Temperature channel, animated uv) — proving the model generalizes from sensors to procedural drivers, and keeping `Biome.Step` untouched.
- **Wire or delete `externalInfluenceTex`** (confirmed dead). Cleanest: make a real full-frame video driver one *Texture*-valued stamp source; otherwise remove it to avoid confusion.
- **OSC threading:** OscJack callbacks may fire off-thread; existing `OSCMapping` calls `SetParameter` inline. Verify empirically — either it's main-thread-pumped (no extra work) or there's a latent bug to fix. Write sensor values to a volatile field, consume on the main thread; never dispatch GPU from a callback.

---

## Part 4 — Recommended build sequence

Status legend: ✅ shipped · 🟡 partial (scaffolded, not fully wired) · ⬜ pending.

| # | Move | Tier | Risk | Struct? | New channel? | Status |
|---|------|------|------|---------|--------------|--------|
| 1 | **Spatial stamp injector** (plant-O₂, robot-Temp) — `BiomeInjector` + `InjectStampKernel` + OSC `/inject/<name>` | ext | low | no | no | ✅ shipped |
| 2 | **Q10 + decay sinks** (stop the mush) — `pow(2.74,(temp−0.5)·decompTempSpan)`; Waste decay 0.001, Nutrient 0.0005 | 1 | low | no | no | ✅ shipped |
| 3 | **Relax-to-baseline + permeability runaway fix** — per-channel `relaxRate` (O₂→0.8, Temp→0.5); perm relaxes toward recomputed noise terrain | 1 | low | no | no | ✅ shipped |
| 4 | **Humidity** channel — high-diffusion (0.97), flow-advected, relax→0.5, Temp evaporation | 2 | med | no | **yes (→12)** | 🟡 channel shipped; build-cue `\|∇Humidity\|` read **not yet wired** (per-scene `UmweltMapping`) |
| 5 | **B-channel predator/prey + waste scavenging** (asset edits + 3 lines/sim) | 0 | low | no | no | ⬜ pending (only Boid reads `.b`; Physarum/Termite don't) |
| 6 | **Diurnal sun** (as a procedural stamp, phase from `SimStepCount`) | 1 | low–med | no | no | ⬜ pending |
| 7 | **Permeability-as-Topography** (Laplacian stigmergy) | 2 | med | flags→typeId bits | no | ⬜ pending |
| 8 | **Mortality → succession** (+ Physarum dormancy latch) | 2 | med | flags→typeId bits | no | 🟡 lifecycle fields scaffolded (`UmweltMapping`, `enableDeath=false`); death/dormancy not wired |

Rows 1–3 are shipped: the field is no longer relax-to-equilibrium soup — Q10 +
decay sinks + relaxation give it travelling fertility fronts and a breathing
baseline, and the injector makes it externally drivable. Humidity (4) exists as a
field but doesn't yet *do* anything agent-side until its evaporation-flux read is
wired per scene. 5–8 are the remaining richness/biology work.

**Status:** rows 1–3 shipped + Humidity channel landed. Next up, cheapest-first:
**wire the Humidity build-cue read** (close row 4), then **#5 B-channel
predator/prey + waste scavenging** (asset-only, makes antagonisms real) and **#6
diurnal sun** (the master off-equilibrium pump). 7–8 are the deep biology once the
core reads as alive.

---

## Part 5 — Perf-aware refresh (2026-06-09, exhibition pass)

Re-examined the biome/ALife structure through the **performance** lens (the M4 exhibition
scale-up) and the three live asks: ecosystem richness, neuron-trigger display, injector
usability. See [[PERFORMANCE]] for the GPU budget this is reconciled against.

### 5a. The key realization: ecosystem richness is essentially **free** on the GPU
The frame budget is spent on **physarum agent count** (Move / WriteTrails / write-back) and
**per-pixel sim passes at output res**. The biome PDE runs on a **320×180 grid, decimated to
every 4th step** — ~0.06 M px × 4 passes / 4 = trivial. Therefore:
- **Adding biome channels is ~free.** Each channel is +0.23 MB (320×180×2B×2 buffers) and
  one more iteration of the per-texel channel loops on a tiny, decimated grid. The Humidity
  growth (→12, index 11) cost nothing measurable. *Budget is not the reason to hold the count.*
- **Richer Umwelts are ~free.** Perception build now runs at `perceptionResScale` (≈0.25);
  more `reads` = a slightly longer per-texel loop on a small texture. More `writes` = with
  fused write-back, a longer in-register per-agent loop, **same dispatch count**.
- **So the only real costs of "more ecosystem" are authoring complexity and *mush*** — not
  GPU time. Spend the freed-up thinking on legibility, not optimization.

### 5b. Mush gets *worse* as agent count scales — fix it before 10 M, not after
> ✅ **Shipped** (`Biome.compute`, `Biome.cs`, `BiomeFieldConfig.cs` + per-show
> `BiomeFieldConfig_Homeostatic.asset`): per-channel **relaxation toward baseline**
> (Oxygen→0.8 replenish, Temperature→0.5 cap), **Q10 decomposition** (`decompositionTempSpan`;
> 4 in the C# default, 2 in the homeostatic asset), and the **permeability runaway fix**
> (bounded relaxation toward the recomputed noise terrain). All gated: `relaxRate=0` + the
> legacy integrator branch reproduce the old behaviour for a clean A/B. The homeostatic preset
> lives as a tuned asset **per show** (`11.1 CURRENTS`, `11.2 SIGGRAPH`) after the scene split,
> not just in code. Decay sinks added to Waste (0.001) + Nutrient (0.0005).
Today Nutrient/Oxygen/Waste have `decay=0` + constant per-agent deposits + `diffuse≈0.99`,
so they ramp to a flat clamped 1.0 (Part 1e). **This is a fidelity bug that the scale-up
amplifies:** 10 M physarum deposit ~30× more than the current 300 k, so the field saturates
~30× faster, the perception R/G gradients flatten, agents lose their cue, and the composite
washes out. **The "make it alive" Tier-1 work (Part 2) is therefore a prerequisite for the
high-count target, not a nice-to-have:**
- **Decay sinks** (Oxygen `decay≈0.001`, Waste `decay≈0.0005`, Nutrient small) — asset-only,
  zero code, zero perf. Do this first; it stops the saturation.
- **Q10 decomposition** (replace linear `(0.5+temp)` with `pow(2.74,(temp−0.5)·span)` +
  clamp) — a few lines in `InteractFieldsKernel`, runs on the decimated grid = free.
- **Fix the `temperatureToPermeability` runaway integrator** (Part 1e) — it accumulates
  unbounded; make it relax toward a baseline. One line, free.

### 5c. Channel-structure recommendations (ranked, all perf-cheap)
1. **Decay sinks + Q10 + perm-integrator fix** (5b) — *do before scaling counts.*
2. **Humidity channel (→12, index 11).** ✅ **Shipped.** `BiomeChannel.Count=12` + `Names`
   (`BiomeFieldConfig.cs`), `CH_HUMIDITY 11` (`Biome.compute`), a `FieldChannelSettings` row
   (diffuse 0.97, advected, relax→0.5, decay 0.001), and `ExternalTextureSender.ChannelNames`
   now derives from `BiomeChannel.Names` (no longer hand-maintained/stale). High-diffusion,
   Temperature evaporates it. **Still to wire:** the termite build cue
   (`evaporation ≈ |∇Humidity|`) as a per-scene `UmweltMapping` read — the channel exists but
   nothing consumes it yet. Perf: negligible.
3. **Permeability-as-topography (Laplacian stigmergy), no new channel** — curvature in
   `InteractFieldsKernel` (neighbours already sampled) drives build/dig so mounds
   self-organize. Free; medium authoring risk (needs a slow relaxation clamp).
4. **Make the dead `B` (avoidance) channel real for physarum + termite** (3 lines each,
   mirror `BoidSim` avoidance read) → asymmetric predator/prey instead of mutual freeze.
   Free. ⚠️ `perception.b` is one summed scalar — one avoidance meaning per sim.
5. **Retire / repurpose dead wiring:** `externalInfluenceTex` is assigned every frame but no
   shader samples it (Part 1e) — either delete it or turn it into a real Texture-valued
   injector source (5e). `Pheromone_2` has no consumer loop — give termites a
   self-Pheromone_2 chemotaxis read for mound autocatalysis (free, asset-only).

### 5d. Displaying neuron triggers — get the HUD ring off the composite
The gaussian ring overlay reads as a HUD on top of an organic field — you flagged it, and
it's now off by default for the show. Firing **already** expresses organically (firing
agents move faster + deposit brighter via `firingSpeedMul`/`firingDepositAmount`). Better
structural options, roughly in order of "in-keeping with the evolved ecosystem":

1. **Firing → biome stamp (ecological, recommended).** `NeuronFiringSource` already loads the
   neuron→uv table (`PositionsCPU`) and the live decayed values (`ScaledValues`). Feed firing
   neurons through the **injector primitive** as location-accurate stamps — e.g. a Temperature
   or Nutrient pulse at each firing neuron's uv. The field then *responds* (flow re-aims, Q10
   flares, agents flock in) so "the network lit up here" becomes an emergent bloom, not a
   drawn marker. This **unifies neuron triggers and external sensors into one mechanism**
   (a neuron is just another injector source whose value/position come from the blob). Cheap:
   a handful of stamps per active neuron, reuses `InjectStampKernel`.
2. **Firing → trail-layer scar (graphic but organic).** Inject firing into a sim's
   `trailReadArray` to *rupture* established veins → the transport network visibly re-routes
   around the disturbance. Immediate and striking, still part of the medium rather than over it.
3. **Rings as a separate Syphon "infographic" layer (your idea).** Keep `NeuronRingKernel`
   but render it to its **own** RenderTexture (not `compositeOut`) and publish it as a
   dedicated stream via `ExternalTextureSender` (new `SendSource.NeuronRings`). TD composites
   / styles it independently — data-viz stays out of the art. Cheap (the kernel already
   exists and is compacted to active neurons; just retarget it + add one send source).
4. If a *subtle* in-composite cue is still wanted: replace the hard ring with a soft local
   **bloom/persistence lift** in a disc — reads as "a pulse passed through" rather than a ring.

> Recommendation: **(1) for the room, (3) for documentation/screens.** Both reuse existing
> machinery (injector + sender); neither draws on the composite. Decide whether neuron
> disruption is *ecological* (slow, perturb channels) or *graphic* (scar trails) per Part 3's
> open question — they're different aesthetics; you can route different neuron groups to each.

### 5e. Injector usability + external-sensor connection
Shipped this pass (additive, defaults preserve current behaviour — `BiomeInjector.cs`,
`OSCMapping.cs`, `MIDI_OSC.md`):
- **Raw→0..1 calibration** per source (`inputMin`/`inputMax`) so real sensor ranges (ppm,
  distance, lux) map without TD-side math; **EMA `smoothing`** denoises jittery feeds.
- **`oscAddress` override** decouples the wire protocol from the display name (rename a
  source without breaking the sender).
- **"Log Live Source Values"** button — per-source channel, uv, raw→calibrated value, OSC
  address, and time-since-last-message, so bring-up shows at a glance which sensors arrive.

Still recommended (not yet built — need your call on direction):
- **Click-to-place in a composite-aspect preview**, or drive `fieldUV` from a scene transform
  aligned to the projection, so "pin the plant *there*" isn't manual 0..1 guessing. (Today's
  gizmo lives on an arbitrary transform plane.)
- **Texture-valued injector source** (revive `externalInfluenceTex`): sample a TD-painted
  texture into a channel — e.g. a depth-camera silhouette → Temperature, or a projection-mask
  → Nutrient. One new stamp `mode`/source type; makes spatial sensor data first-class.
- **Connection model:** standardize on **TD as the sensor hub** (it already speaks serial /
  MQTT / Art-Net / DMX / HTTP) emitting OSC `/inject/<name>`; Unity stays OSC-in only. The
  calibration + monitor above make raw feeds usable directly, so most sensors need zero
  Unity-side code — just a source row + a TD→OSC route.

---

## Open questions
1. **Channel budget:** ~~OK to grow 10→11 for Humidity?~~ → **Yes — it's perf-free** ([[PERFORMANCE]] §; Part 5a). Sequence it after the mush fix (5b).
2. **Robot mapping:** static "altar" hot-spot, or does the arm's physical pose sweep the stamp uv as it moves?
3. **Neuron disruption register:** ecological (perturb biome channels, slow) vs graphic (scar trails directly, immediate)? Or both, per neuron group?
4. **Plant signal:** live CO₂/light sensors per plant, or constant emitters the artist tunes?
5. **Authoring:** is a manual in-editor "pin the plant/robot/neuron here" gizmo + OSC `/map/<name>` live-remap enough, or do you want camera/projection-calibrated auto-mapping from physical → biome coords?
