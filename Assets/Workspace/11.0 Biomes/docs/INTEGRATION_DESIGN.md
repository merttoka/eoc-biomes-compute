# Biome Layer & Sim Integration — Design Exploration

> Speculative design (not yet implemented). Synthesizes a 4-lens design panel
> (pragmatist / biology-maximalist / installation-architect / dynamical-art),
> each adversarially critiqued, grounded in the research PDF
> (`Simulating Biological Systems Parameters.pdf`) and the actual codebase.
> Three parts: (1) how the layers couple **today**, (2) making them interact
> **richer**, (3) an **external-input** injection architecture for the gallery.

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
1. **Q10 ignition + decay sinks.** Replace the linear `decompRate = rate·(0.5+temp)` with `rate·pow(2.74,(temp−0.5)·span)` so decomposition is near-zero when cold and *explosive* when hot → travelling fertility fronts instead of a smooth ramp. Pair with small sinks (`Oxygen decay≈0.001`, `Waste decay≈0.0005`) so the field can breathe instead of saturating. **Mandatory:** clamp `decompRate`; and the front only *travels* if heat actually moves on (needs the sun below, or higher Temp decay) — otherwise you get a static max-fertility disk.
2. **Diurnal "sun" forcing.** Per the PDF, light is *not* a layer — inject a slow sweeping warm gradient into Temperature each step (phase from `SimStepCount`). This is the master off-equilibrium pump: it moves the hot zone → re-aims flow, re-fires Q10, drives evaporation, and gives the piece a global rhythm an audience reads in seconds. **Build it as a Temperature stamp in the pre-Step injector (Part 3), NOT a new pass** — keeps `Biome.Step` untouched.

> ⚠️ Flow accumulates (`existingFX + …`) with weak decay (0.02). A too-strong/too-wide sun can saturate Flow to ±1 → re-introduces a global drift / advection smear. Co-tune `temperatureToFlowStrength` ↔ flow decay; keep sun gain modest.

### Tier 2 — Medium (structural biology; do after Tier 1 reads as alive)
- **Humidity channel (PDF's #1 missing layer; 10→11 channels).** High-diffusion, agent-consumed; Temperature negatively couples it (evaporation). Makes Physarum *and* termites compete for a renewable, depletable resource, and gives termites their true build cue: **evaporation flux = |∇Humidity|**. The one channel worth spending.
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

| # | Move | Tier | Risk | Touches struct? | New channel? |
|---|------|------|------|-----------------|--------------|
| 1 | **Spatial stamp injector** (plant-O₂, robot-Temp, sun) ✅ **shipped** | C | low | no | no |
| 2 | **Q10 + decay sinks** (stop the mush) | 1 | low | no | no |
| 3 | **B-channel predator/prey + waste scavenging** (asset edits) | 0 | low | no | no |
| 4 | **Diurnal sun** (as a stamp) | 1 | low–med | no | no |
| 5 | **Humidity** channel + evaporation/build-cue | 2 | med | no | **yes (→11)** |
| 6 | **Permeability-as-Topography** (Laplacian stigmergy) | 2 | med | flags→typeId bits | no |
| 7 | **Mortality → succession** (+ Physarum dormancy latch) | 2 | med | flags→typeId bits | no |

Ship 1–4 first (no struct, no channel, all reversible on knobs) — that alone
turns the field from relax-to-equilibrium soup into a breathing, externally-driven
system. 5–7 are the deep biology; do them once the core reads as alive.

**Status:** #1 shipped (the injector). Next up: **#2 Q10 + decay sinks** — the cheapest
move that converts the relax-to-mush field into travelling fertility fronts.

---

## Open questions
1. **Channel budget:** OK to grow 10→11 for Humidity (the PDF's #1 pick), or stay at 10 and fake moisture off Permeability/Oxygen?
2. **Robot mapping:** static "altar" hot-spot, or does the arm's physical pose sweep the stamp uv as it moves?
3. **Neuron disruption register:** ecological (perturb biome channels, slow) vs graphic (scar trails directly, immediate)? Or both, per neuron group?
4. **Plant signal:** live CO₂/light sensors per plant, or constant emitters the artist tunes?
5. **Authoring:** is a manual in-editor "pin the plant/robot/neuron here" gizmo + OSC `/map/<name>` live-remap enough, or do you want camera/projection-calibrated auto-mapping from physical → biome coords?
