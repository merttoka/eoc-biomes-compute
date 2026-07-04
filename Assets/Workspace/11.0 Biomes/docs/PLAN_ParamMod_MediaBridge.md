---
status: proposed
implements: [INTERACTION_DESIGN_II.md, INTEGRATION_DESIGN.md]
created: 2026-07-01
---

> Implementation plan for the decisions taken against `INTERACTION_DESIGN_II.md`:
> (1) param-mod **route** as the shared substrate for negative-space + texture-driven params;
> (2) media-agent bridge via **topology B** (Unity listens to `/sn/…` direct; maestra-ready);
> (3) `behavior/*` as a **light** per-sim global-multiplier patch; (4) Humidity + B-channel
> scavenging shipped **now** as asset-level wins. Synthesized from four code-grounded design
> passes over the actual shaders/scripts — anchors are real file:line references.

# EoC Biomes — Sequenced Implementation Plan: Perception Param-Mod Route, Media Bridge, Humidity & Waste Consumers

This plan sequences four code-grounded design tracks for the Unity GPU biome A-life sim into one ordered build. Two tracks are asset-level "ship now" wins that unlock artist-authored behavior with zero or near-zero shader work (Humidity/|∇Humidity| consumers; B-channel waste-scavenging + predator/prey). One track is the **keystone**: generalizing the perception path (4→8 effect slots, `externalInfluenceTex` promoted to a first-class source, negative-space expressed through the same route, and an Umwelt `source→effect(gain,curve)` data model) — this unlocks the two artist-facing features (#2 negative-space, #3 texture-driven params) that depend on it. One track is independent: a media-agent → Unity OSC bridge (warmth→Temperature stamp + non-destructive per-sim global multipliers). We do this now because the ship-now asset wins de-risk the runtime, the keystone consolidates all future perception features onto one substrate before more one-off shader forks accrue, and the media bridge can proceed in parallel without touching the perception core.

## Dependency / Sequence Overview

**Fully parallel, no gating (start immediately):**
- **Track A — Humidity Tier 1** (asset-only): moisture chemotaxis + consumption rows. Zero code.
- **Track B — Waste-scavenging Part 1** (asset-only): Waste→Chemotaxis rows on Physarum/Boid. Zero code.
- **Track D — Media bridge** (independent): OSC warmth stamp + per-sim behavior multipliers. Never touches perception.

**Small shader unlocks (independent of the keystone):**
- **Track A — Humidity Tier 2**: `CH_HUMIDITY_GRAD` channel precompute in `InteractFieldsKernel` + `BiomeFieldConfig`. Gates the termite build-cue asset rows.
- **Track B — Waste Part 2**: activate `perception.b` sampling in `PhysarumSim`/`TermiteSim`. Gates Part 3-prey (avoidance/flee).

**Keystone and its dependents:**
- **Track C — Param-mod route (KEYSTONE)**: struct+packer, `perceptionTex2`, external-source promotion, 8-slot Umwelt model. Everything internal must land as an atomic unit (struct↔packer, tex2 alloc+neutral-init before any sample).
  - **#2 Negative-space** — expressed as ordinary rows once C lands (no new mechanism).
  - **#3 Texture-driven params** — ExternalTex→DepositMod/TurnRateMod rows once C lands.

**Hard ordering constraints inside tracks:**
- C: `ReadEntry` struct (Biome.compute) and C# packer stride (Biome.cs) MUST change together or the buffer is misread. `perceptionTex2` alloc + neutral-init (0.5) MUST precede any consumer sample. `BindParamModDepths` must exist before consumers reference depth uniforms.
- A-Tier2: shader `#define`/`CH_COUNT` + `BiomeFieldConfig.cs` Names/Count MUST land before channel-12 asset rows (dropdown + inertness).
- B-Part3-prey depends on B-Part2 (prey avoidance is dead until `.b` is sampled).
- D: `SimulationBase` multiplier fields + setter + `BehaviorLeaf` enum must land before callback registration and per-sim consumption.

## Phase Plan

| Phase | Items | Why this order |
|-------|-------|----------------|
| **0 — Ship-now asset wins** | A-Tier1 (Humidity chemotaxis+consumption), B-Part1 (Waste forage) | Zero code, independent, immediate artist value; validates the existing perception.R path end-to-end before we widen it. |
| **1 — Small independent shader unlocks (parallel)** | A-Tier2 (Humidity_Grad channel), B-Part2 (activate perception.b in Physarum/Termite), D (media bridge) | All independent of the keystone and of each other; can run concurrently across three workstreams. B-Part2 also unblocks B-Part3-prey. |
| **2 — Post-unlock asset rows** | A-Tier2 termite build-cue rows, B-Part3 (predator/prey) | Depend only on Phase-1 shader unlocks (channel 12 exists; `.b` sampled). Pure asset config. |
| **3 — KEYSTONE core** | C (ReadEntry+packer, perceptionTex2, external-source promotion, 8-slot Umwelt, depth knobs, consumers) | Largest, riskiest; land after ship-now wins prove the substrate. Internal steps are tightly ordered. |
| **4 — Keystone-dependent features** | #2 negative-space rows, #3 texture-driven-param rows | Pure asset authoring on the widened route; only possible once C ships. |

---

## Track A — Humidity (ch 11) + |∇Humidity| consumers

### A-Tier1 — Moisture chemotaxis + consumption (asset-only)
- **Goal:** Physarum + Termite drift toward moist regions and draw Humidity down where they cluster; relax (0.01) refills elsewhere → renewable competed resource.
- **Files touched:**
  - `Assets/Workspace/11.1 CURRENTS Scene/assets/UmweltPhysarum_Alt.asset`
  - `Assets/Workspace/11.1 CURRENTS Scene/assets/UmweltTermite_Alt.asset`
  - `Assets/Workspace/11.2 SIGGRAPH Scene/assets/UmweltPhysarum_Alt.asset`
  - `Assets/Workspace/11.2 SIGGRAPH Scene/assets/UmweltTermite_Alt.asset`
- **Concrete steps:**
  1. Physarum (both scenes): `reads += {channel:11, weight:0.8, effect:0(Chemotaxis)}`; `writes += {channel:11, amount:-0.006}`.
  2. Termite (both scenes): `reads += {channel:11, weight:0.6, effect:0}`; `writes += {channel:11, amount:-0.004}`.
- **Effort:** S · **Risk:** Two species + evaporation may locally exhaust Humidity faster than relaxRate 0.01 refills (resource collapse). First-guess balance values.
- **Verification:** Play mode — both species drift toward moist regions, draw Humidity down where clustered, relax refills. Confirm no collapse; adjust write magnitude / relaxRate if it flatlines.
- **Depends-on:** Nothing (Humidity is already live channel 11).

### A-Tier2 — |∇Humidity| derived channel (`CH_HUMIDITY_GRAD` = 12) + build-cue
- **Goal:** Precompute evaporation-flux magnitude as a new channel so the termite build-cue is a pure asset read row (ReadFieldKernel stays a point sampler and must NOT be modified).
- **Files touched:**
  - `Biome.compute:30` (CH defines / `CH_COUNT`→13)
  - `Biome.compute:215` (`InteractFieldsKernel` — add |∇Humidity| block)
  - `Biome.compute:433` (`RenderDebugKernel` — optional colormap branch, recommended)
  - `BiomeFieldConfig.cs:10` (BiomeChannel enum + Names + Count→13)
  - `BiomeFieldConfig.cs:73` (default channels list + optional `humidityGradientGain` field)
  - `Biome.cs:253` (Step uniform wiring — optional gain `SetFloat`)
  - `BiomeFieldConfig_Homeostatic.asset` (both scenes)
  - `UmweltTermite_Alt.asset` (both scenes)
- **Concrete steps:**
  1. `Biome.compute`: add `#define CH_HUMIDITY_GRAD 12`, set `CH_COUNT 13`.
  2. `InteractFieldsKernel`: after `uv`, 4 neighbour taps of `CH_HUMIDITY` via `fieldRead.SampleLevel` at `uv±dx/±dy` (`dx=1/rezX`, `dy=1/rezY`); central diffs `gradX=(hR-hL)*0.5`, `gradY=(hU-hD)*0.5`; `fieldWrite[uint3(id.xy,CH_HUMIDITY_GRAD)] = saturate(length(float2(gradX,gradY))*humidityGradGain)`. Reads center-neighbours, writes own cell only → race-free. Use pre-evaporation Humidity (sharpest edge). Runs pass 3, before Diffuse (pass 4).
  3. (Recommended) add uniform `float humidityGradientGain;`; add field to `BiomeFieldConfig.cs`; `SetFloat` in `Biome.cs` Step alongside `temperatureToEvaporation` (~line 267). Otherwise hardcode gain ~8–16 (raw magnitude is tiny because Humidity diffuseRate 0.97 smooths the field).
  4. (Optional) `RenderDebugKernel` colormap branch for channel 12 → auto debug quad.
  5. `BiomeFieldConfig.cs`: `const int HumidityGrad = 12`, `Count = 13`, append `"Humidity_Grad"` to Names, append default row (diffuse/decay/relax all 0 — MUST be diffuse 0 or DiffuseFieldsKernel blurs the edge it encodes).
  6. Assets (both scenes): append `Humidity_Grad` channel row to `BiomeFieldConfig_Homeostatic.asset`; termite `reads += {channel:12, weight:1.0, effect:0}` (+ optional `{channel:12, weight:0.6, effect:1(SpeedPenalty)}` for settle-and-deposit construction ridges).
- **Effort:** M · **Risk:** Premise that Interact already samples neighbours is FALSE — the 4 taps are new work (Interact is still correct home: physical coherence, pre-diffusion edge). Raw grad tiny without gain. Decimated Steps (`stepEvery`) refresh grad only on active steps (slightly stale edge). Adding a channel touches shared `Biome.compute`/`BiomeFieldConfig` used by ALL 11.x scenes → other scenes' 12-row configs log a harmless tail-inert warning until updated.
- **Verification:** Enable `debugChannel 12` → bright ridges at hot-zone drying wakes; termites accumulate on ridges and (with speed-penalty row) settle and deposit.
- **Depends-on:** A-Tier1 (recommended, for coherent moisture behavior); shader `#define`+`CH_COUNT`+`BiomeFieldConfig.cs` Names/Count MUST precede the channel-12 asset rows.

---

## Track B — B-channel waste-scavenging + asymmetric predator/prey

### B-Part1 — Waste foraging (asset-only)
- **Goal:** Physarum + Boid drift toward waste plumes (Waste→Chemotaxis lands in perception.R, which both already sample).
- **Files touched:** Physarum + Boid `UmweltMapping` assets (inspector/YAML — note: 0 `.asset` files currently in repo; author in Unity inspector).
- **Concrete steps:** add `{channel:Waste(6), weight:+0.5..0.8, effect:Chemotaxis}` to Physarum and Boid Umwelt.
- **Effort:** S · **Risk:** perception.R is one summed, saturated scalar (`saturate(x*0.5+0.5)`) shared by Waste + Nutrient + prey-chase — can conflict/clip at 1.0; keep weights modest.
- **Verification:** agents drift toward waste plumes; no recompile.
- **Depends-on:** Nothing. Confirm Waste is actually emitted where foraging is wanted (see open questions).

### B-Part2 — Activate perception.b in Physarum & Termite (shader)
- **Goal:** Make Avoidance read entries (effect==2 → perception.B) actually steer these sims; both currently ignore `.b` so all Avoidance rows are silently dead for them.
- **Files touched:**
  - `PhysarumSim.compute:121-128` (SensorTurns)
  - `TermiteSim.compute:134-139` (SensorTurns)
  - Reference: `BoidSim.compute:317` (`ahead -= pAhead.b`)
- **Concrete steps:**
  1. Physarum: replace the three `.r`-only samples (biomeLeft/Middle/Right) with `float4 pL/pM/pR = perceptionTex.SampleLevel(...)`; combine `leftLevel += (pL.r-0.5)*2.0 - pL.b;` (middle/right identical).
  2. Termite: identical — float4 fetch at each sensor, subtract `.b` from each of leftLevel/middleLevel/rightLevel.
- **Effort:** S · **Risk:** Boid flees using only the ahead sensor while new Physarum/Termite code subtracts `.b` at all three sensors → flee not symmetric across sims by construction. Avoidance uses `max(0,weighted)` so a negative weight on an Avoidance read is a no-op — authors must use positive weight for flee. Three ARGBHalf samples/sensor/agent (negligible at scale but not free).
- **Verification:** author a temporary Avoidance row → confirm Physarum/Termite steer away.
- **Depends-on:** Nothing structural; unblocks B-Part3-prey.

### B-Part3 — Asymmetric predator/prey (asset)
- **Goal:** Predator reads prey's Pheromone as Chemotaxis (chase, R); prey reads predator's Pheromone as Avoidance (flee, B). Same pheromone pair, opposite effect.
- **Files touched:** predator + prey `UmweltMapping` assets.
- **Concrete steps:**
  1. Predator: `write {Pheromone0(1), +0.02}`; `read {Pheromone1(2), +0.8, Chemotaxis}`.
  2. Prey: `write {Pheromone1(2), +0.02}`; `read {Pheromone0(1), +1.5, Avoidance}`.
  3. Tune weights so R doesn't clip (chemotaxis remapped `saturate(sum*0.5+0.5)`); Avoidance is `saturate(sum of max(0,weighted))` so a single strong flee weight is fine.
- **Effort:** S · **Risk:** perception.b is one summed scalar → prey fleeing two predators cannot distinguish source/direction beyond magnitude. Must confirm sim-index→pheromone-channel alignment (Pheromone0=sim0).
- **Verification:** predator converges on prey trails; prey scatters. Toggle B-Part1 Waste read on Boid to confirm asset-only path works without rebuild.
- **Depends-on:** B-Part2 (prey Avoidance is dead until `.b` sampled); per-sim pheromone write rows must exist.

---

## Track C — KEYSTONE: Param-modulation route (8 effect slots, external source, negative-space, source→effect data model)

- **Goal:** Widen perception 4→8 effect slots via a second `perceptionTex2`, promote `externalInfluenceTex` (currently assigned every Step at `SimulationManager.cs:214-218` but never sampled — dead) to a first-class ReadEntry source, express negative-space through ordinary rows, and grow the Umwelt row from `channel→effect(weight)` to `source→effect(gain,curve)`. Extend, don't fork — keep the "biome field → ReadEntry buffer → perception texture → sim samples one lane" substrate.
- **Effect slot map (one meaning per slot per sim):**
  - `perceptionTex` (existing): R Chemotaxis, G SpeedPenalty, B Avoidance, A SpeedBoost.
  - `perceptionTex2` (new): R TurnRateMod, G DepositMod, B SizeMod, A ColorShift.
  - Encoding: TurnRateMod/DepositMod/SizeMod are multipliers in [0,2] stored as `val*0.5` (decode ×2, neutral 0.5); ColorShift signed [-1,1] stored as `*0.5+0.5` (neutral 0.5). Reset/clear MUST init tex2 to (0.5,0.5,0.5,0.5).
- **Files touched:**
  - `Biome.compute:98` (ReadEntry struct)
  - `Biome.compute:370` (ReadFieldKernel packing + externalInfluenceTex source + perceptionTex2 write + ApplyCurve helper)
  - `Biome.cs:576` (BuildPerceptionTex: float*8 packer, bind perceptionTex2 + externalInfluenceTex)
  - `UmweltMapping.cs:22` (UmweltEffect→8, UmweltSource, UmweltResponse, UmweltModEntry)
  - `SimulationBase.cs:219` (allocate/bind/neutral-init perceptionTex2, BindParamModDepths) + `:48` externalInfluenceTex hand-off
  - `SimulationManager.cs:214` (feed externalInfluenceTex + perceptionTex2 into BuildPerceptionTex)
  - `PhysarumSim.compute:92,193,244` (TurnRateMod/DepositMod/Size+Color)
  - `TermiteSim.compute:105,214,257`
  - `BoidSim.compute:321,367,416`
- **Concrete steps (ordered — internal dependencies are hard):**
  1. **ReadEntry widen** (`Biome.compute:98-104`) — struct → `{int source; int channel; float gain; int effect; int curveType; float curveA; float curveB; float pad;}` (32B). source 0=biome channel, 1=external tex. curveType 0=linear, 1=gamma(curveA), 2=smoothstep threshold(curveA lo, curveB hi). **Must land in the same commit as step 4** (packer stride) or the buffer is misread.
  2. **ReadFieldKernel packing** (`Biome.compute:379-407`) — replace 4 accumulators with 8 (keep chemotaxis/speedMod/avoidance/boost; add `turnMod=1, depositMod=1, sizeMod=1, colorShift=0`). Source the value: `float raw = (entry.source==1) ? externalInfluenceTex.SampleLevel(sampler_externalTex, uv, 0).r : fieldRead.SampleLevel(sampler_fieldRead, float3(uv, entry.channel), 0);` then `float val = ApplyCurve(raw, entry.curveType, entry.curveA, entry.curveB); float weighted = val*entry.gain;`. Extend the if-ladder: `==4 turnMod*=saturate(1.0+weighted); ==5 depositMod*=saturate(1.0+weighted); ==6 sizeMod*=saturate(1.0+weighted); ==7 colorShift+=weighted;`. Add `ApplyCurve()` helper above kernel.
  3. **Second output write** (end of ReadFieldKernel) — keep existing perceptionTex write; add `perceptionTex2[id.xy] = float4(saturate(turnMod*0.5), saturate(depositMod*0.5), saturate(sizeMod*0.5), saturate(colorShift*0.5+0.5));`. Declare `RWTexture2D<float4> perceptionTex2;` and `Texture2D<float4> externalInfluenceTex; SamplerState sampler_externalTex;` near line 108.
  4. **BuildPerceptionTex** (`Biome.cs:576-616`, pack at `:596-605`) — grow packer stride float*4→float*8 (`perceptionEntryBuffer` + `_perceptionEntryData`); pack source/channel/gain/effect/curveType/curveA/curveB. Take perceptionTex2 + externalInfluenceTex params; `cs.SetTexture(readFieldKernel,"perceptionTex2",perceptionTex2)`; bind externalInfluenceTex with a 1×1 black dummy when null (mirror dummyNeuron pattern). **Always write all 4 tex2 lanes even when no rows target them.**
  5. **SimulationBase** (`:219-222`) — allocate perceptionTex2 alongside perceptionTex (same ARGBHalf, pw×ph); add `s_PerceptionTex2ID`; bind both in BindPerceptionTex; null both in Release(). ReadFields/Reset MUST seed tex2 to neutral 0.5 (or clear in Biome after build if no rows target tex2). **Must precede any consumer sample.**
  6. **BindParamModDepths** (SimulationBase) — float uniforms `turnModDepth, depositModDepth, sizeModDepth, colorShiftDepth` (default 1, 0 = disable lane = identity). Consumers multiply the decoded modulation delta by depth so any sim opts a slot out. **Must exist before consumers reference depth uniforms.**
  7. **SimulationManager.Step** (`:214-219` + BuildPerceptionTex call ~`:232+`) — pass `sim.externalInfluenceTex` + `sim.perceptionTex2` into BuildPerceptionTex so external tex feeds the SAME route; retire the dead direct assignment (becomes source hand-off only).
  8. **Physarum consumers** (`PhysarumSim.compute`) — declare `Texture2D<float4> perceptionTex2;`. TurnRateMod line 92: `float tang = p.turnAngle * (perceptionTex2.SampleLevel(sampler_perceptionTex, middleUV.xy, 0).r*2.0);`. DepositMod line 193: `dep *= perceptionTex2.SampleLevel(sampler_perceptionTex, a.position/float2((float)rezX,(float)rezY),0).g*2.0;`. Size/Color line 244: sample tex2 at pixel uv, `float sz=t2.b*2.0; float hue=frac(p.hue+(t2.a*2.0-1.0)*colorShiftDepth);` scale brightness `0.8*val*sz`.
  9. **Termite consumers** (`TermiteSim.compute`) — identical pattern; TurnRateMod multiplies `tang` at line 105 (after per-group spread), DepositMod multiplies depositAmount/firingDepositAmount in WriteTrails ~`:214-216`, Size/Color in Render ~`:257`.
  10. **Boid consumers** (`BoidSim.compute`) — sample once `float turnMul = perceptionTex2.SampleLevel(sampler_perceptionTex, uv, 0).r*2.0;` in MoveAgents; multiply the three `p.maxForce*p.foodSeekingStrength[...]` caps (lines 321,324,327) by turnMul. DepositMod line 367; Size/Color line 416.
  11. **UmweltMapping.cs** — expand enum to 8, restructure row per asset model below. Add bake helper: `AnimationCurve → (curveType,curveA,curveB)` or small LUT if arbitrary curves needed later.
- **Asset model (migration keeps old assets 1:1 — channel→source=BiomeChannel, weight→gain, curve→Linear):**
  ```
  enum UmweltSource { BiomeChannel, ExternalTexture }
  enum UmweltEffect { Chemotaxis, SpeedPenalty, Avoidance, SpeedBoost,   // tex1
                      TurnRateMod, DepositMod, SizeMod, ColorShift }     // tex2
  struct UmweltResponse { enum Shape{Linear,Gamma,Threshold} shape;
                          [0.1,4] float gammaOrLo; [0,1] float thresholdHi; }
  class UmweltModEntry { UmweltSource source; [BiomeChannelField] int channel;
                         [-2,2] float gain; UmweltResponse curve; UmweltEffect effect; }
  List<UmweltModEntry> reads;   // supersedes List<UmweltReadEntry>
  ```
  AnimationCurve deliberately avoided on GPU path — 3-shape parametric response covers remap/gamma/threshold; if arbitrary curves needed, bake each row's curve into a 64-tap R8 LUT row indexed by row id.
- **Effort:** L · **Risk:**
  - **MUSH:** DepositMod gain>0 amplifies into a saturated uniform field. Mitigate: `saturate(1+weighted)`, dep clamped [0,1] (already in WriteTrails), conservative depositModDepth default.
  - **FREEZE:** SpeedPenalty→0 or TurnRateMod→0 + Avoidance trapping halts agents that pile at mask edge. Mitigate: pair SpeedPenalty with Avoidance, floor speedMod (`max(speedMod,0.05)`), floor tang.
  - **ENCODING TRAP:** tex2 lanes are multipliers around neutral 0.5 — if cleared to 0, every sim sees ×0 → total freeze/black. Reset MUST seed 0.5 and BuildPerceptionTex must always write all 4 lanes.
  - **FEEDBACK:** ExternalTex→DepositMod is safe (sims read trail array, not biome); but a future biome channel that agents WRITE routed into DepositMod self-amplifies — keep DepositMod off self-written channels or rely on clamp+gamma.
  - **BANDWIDTH:** second ARGBHalf perception tex doubles perception build + one sampler per consumer (hottest sampler). At 2×FHD real — bind tex2 only to sims whose Umwelt authors tex2 effects; consider packing the pair if profiling demands.
  - **SizeMod mismatch:** trail sims have no point-sprite size — mapped to render brightness/deposit footprint (softer than requirement implies).
  - **ColorShift wrap:** additive across rows can wrap hue via `frac()`; clamp range, expose colorShiftDepth to keep subtle.
- **Verification:** Migrate an existing asset (confirm 1:1, identity behavior unchanged). Author one tex1 row (existing effect) + one tex2 row per effect; confirm TurnRateMod bends paths, DepositMod changes trail strength, SizeMod scales brightness, ColorShift tints — and that a depth=0 knob returns identity. Confirm unmodulated regions stay identity (tex2 neutral 0.5). Route externalInfluenceTex → a tex2 effect and confirm the previously-dead texture now drives behavior.
- **Depends-on:** Nothing external; internal ordering per steps above. Land after Phase 0 ship-now wins prove the substrate. **Migration pass** needed for serialized `.asset` rows (old effect ints 0-3 stay valid; add `source=Channel` default) — confirm no code switches on `effect>3` elsewhere.

### #2 Negative-space (depends on C)
- **Goal:** Thin local density via ordinary rows — no new mechanism.
- **Files touched:** per-scene Umwelt assets (authoring only).
- **Concrete steps:** author a mask (biome channel or external tex) as three rows: `Avoidance gain>0` (repel), `SpeedPenalty gain→0` (halt), `DepositMod gain<0` (starve). Pair SpeedPenalty with Avoidance so agents flee rather than pile.
- **Effort:** S · **Risk:** FREEZE (see C mitigations — floor speed/turn). · **Verification:** density thins in masked region, agents steer away and stop reinforcing trails without freezing at the edge. · **Depends-on:** Track C.

### #3 Texture-driven params (depends on C)
- **Goal:** Video/camera texture drives deposition/turn strength via ExternalTexture source rows.
- **Files touched:** per-scene Umwelt assets (authoring only).
- **Concrete steps:** author e.g. `ExternalTex → DepositMod gain +1.0 Linear` (texture-driven deposition, feature #3), `ExternalTex → TurnRateMod gain -0.6 Gamma(2.0)` (straighten paths where bright).
- **Effort:** S · **Risk:** MUSH on DepositMod (see C). · **Verification:** feeding a live texture visibly modulates deposition/turning where bright. · **Depends-on:** Track C (external-source promotion + tex2).

---

## Track D — media-agent → Unity biome OSC bridge (topology B)

- **Goal:** Unity subscribes directly to media-agent's `/sn/<entity>/…` schema. Route `warmth` → BiomeInjector Temperature stamp (reuse thread-safe `SetValue`); route `behavior/{trail,speed,sensor,cohesion}` → a NEW non-destructive per-sim global multiplier layer applied in `UploadTypeParams` (NOT the destructive `SetParameter`/param-mod route). All callbacks CPU-only volatile writes on the socket thread, consumed on main thread. Keep prefix/port/entity-map data-driven for maestra-core readiness.
- **Files touched:**
  - `OSCMapping.cs:26` (Start — register warmth + behavior callbacks; add prefix/port/entity-binding fields)
  - `SimulationBase.cs:118` (volatile behavior multipliers + `SetBehaviorMultiplier` + `BehaviorLeaf` enum, near ModulatableParams/SetParameter)
  - `TermiteSim.cs:89` (UploadTypeParams — apply behSpeedMul/behTrailMul/behSensorMul)
  - `PhysarumSim.cs` (UploadTypeParams — same multipliers)
  - `BoidSim.cs` (UploadTypeParams — maxSpeed/depositAmount/attractRange/sensorAngleRad multipliers)
  - `BiomeInjector.cs:445` (optional `AddExampleWarmthSources` button; `SetValue` at `:148` is the reused warmth path)
- **Concrete steps (ordered — step 4 gates 3 and 5-7):**
  1. `OSCMapping.cs`: add serialized `string m_SnPrefix='/sn'` and `[Serializable] EntityBinding{ string entityId; SimulationBase sim; }` list `m_EntityBindings` (+ optional per-entity warmth Source name). Build `Dictionary<string,SimulationBase>` in Start().
  2. `OSCMapping.cs` Start(): per binding register `/{prefix}/{entityId}/warmth → m_BiomeInjector.SetValue(warmthSourceName, data.GetElementAsFloat(0))` — inline, thread-safe, no enqueue.
  3. `OSCMapping.cs` Start(): per binding register the four SHORT behavior leaves `/{prefix}/{entityId}/behavior/{speed,trail,sensor,cohesion} → sim.SetBehaviorMultiplier(<leaf>, data.GetElementAsFloat(0))` — inline. **Subscribe to the SHORT names OSCSink emits (`osc.py:85-88`), NOT `trail_gain`/`sensor_angle`.**
  4. `SimulationBase.cs`: add `volatile float behSpeedMul=1, behTrailMul=1, behSensorMul=1, behCohesionMul=1;`, `enum BehaviorLeaf{Speed,Trail,Sensor,Cohesion}`, configurable `minMul/maxMul/neutral` fields, and `public void SetBehaviorMultiplier(BehaviorLeaf, float bias01)` that lerps bias→multiplier (neutral 0.5→1.0) and stores the volatile field. CPU-only; document OSC-thread-safe like BiomeInjector.SetValue. **Land before steps 3, 5-7.**
  5. `TermiteSim.UploadTypeParams` (~`:96-111`): multiply transient `_typeParamsCache` fields — `moveSpeed *= behSpeedMul`, `depositAmount *= behTrailMul`, `senseAngle *= behSensorMul` (before Deg2Rad). Cohesion no-op.
  6. `PhysarumSim.UploadTypeParams`: same moveSpeed/depositAmount/senseAngle; cohesion no-op.
  7. `BoidSim.UploadTypeParams`: `maxSpeed *= behSpeedMul`, `depositAmount *= behTrailMul`, `attractionRange *= behCohesionMul`, `sensorAngleRad *= behSensorMul` (or alignRange — see open question).
  8. `BiomeInjector.cs`: optional `[Button] AddExampleWarmthSources()` (mirror AddExampleDispersalSources) authoring warmth-termite/-physarum/-boid Sources on channel Temperature, mode MaxToward, inputMin/Max 0..1, valueTimeout ~0.5s.
  9. Scene wiring: assign `m_EntityBindings` (termite→TermiteSim, physarum→PhysarumSim, boid→BoidSim) + warmth source names; confirm `m_BiomeInjector` reference set.
  10. Verify at integration: media-agent OSCSink host/port → Unity `m_Port` (9000). Use BiomeInjector "Log Live Source Values" + catch-all `*` debug callback to confirm arrivals.
- **Entity→sim map:** `termite→TermiteSim`, `slime/physarum→PhysarumSim`, `boid→BoidSim`. **Leaf→field:** `behavior/speed`→Termite/Physarum moveSpeed / Boid maxSpeed; `behavior/trail`→depositAmount (all); `behavior/sensor`→Termite/Physarum senseAngle / Boid sensorAngleRad; `behavior/cohesion`→Boid attractRange / Termite+Physarum no-op.
- **Effort:** M · **Risk:**
  - **Wire-address mismatch:** OSCSink emits SHORT leaves; subscribing to long names silently receives nothing. Verify against `osc.py:85-88`.
  - **Non-destructive contract:** multipliers MUST be applied only in UploadTypeParams into transient `_typeParamsCache`, never via SetParameter into agentParams — else presets clobbered and multipliers compound every frame.
  - **senseAngle compounding:** apply multiplier to degree value before Deg2Rad; clamp so high bias doesn't push sensor cone past usable range.
  - **Missing concepts:** Termite/Physarum no cohesion, Boid no first-class senseAngle — unhandled leaves must no-op gracefully, not throw.
  - **Shared Temperature field:** warmth writes the same channel SimulationManager stamps with metabolicHeat (`SimulationManager.cs:277`) — MaxToward + per-entity UV placement mitigate; needs tuning.
  - **Port collision:** OSCSink defaults 9000 = OSCMapping m_Port; OSCInput listens 9001 — ensure outbound sink targets 9000, not the inbound server.
- **Verification:** warmth stamps Temperature; behavior multipliers move sim speed/trail without editing authored presets; unhandled leaves no-op.
- **Depends-on:** Nothing (OscJack + BiomeInjector refs already wired). Fully parallel to Tracks A/B/C. Warmth route (steps 2, 8) is pure reuse and can ship before the behavior route.

---

## Unresolved Questions

**Track A (Humidity):**
- Build-cue strength: chemotaxis-only vs chemotaxis + SpeedPenalty (settle+deposit → true mound construction)? Recommend adding effect:1 row for termites.
- Physarum also read |∇Humidity| (ch 12) or only raw Humidity (11)? Keeping Physarum on raw = distinct niches (Physarum=water-seeker, Termite=boundary-builder).
- `humidityGradientGain` as asset field (matches temperatureToEvaporation pattern) or hardcoded constant for ship-now cut?
- Consumption magnitudes (-0.006 Physarum / -0.004 Termite) + weights (0.8/0.6) are first-guess — playtest to confirm resource competes without collapse.

**Track B (Waste + predator/prey):**
- Which sims are predator vs prey (Physarum=predator/Termite=prey or reverse)? Is Boid a third species in the loop?
- Confirm sim-index→Pheromone-channel convention (Pheromone0=sim0) for actual scene wiring.
- Is Waste emitted where foraging is wanted (corpseWasteAmount death writes, or explicit write rows), or does emitting species need a Waste write row added?
- Should prey flee also raise speed (Dispersal/SpeedBoost read for panic burst), or is directional turning enough for ship-now?

**Track C (KEYSTONE):**
- SizeMod on trail sims: accept redefinition as render-brightness/deposit-footprint gain, or does artist want a genuine point-sprite render path (larger scope)?
- externalInfluenceTex sample `.r` only, or add a per-row swizzle field (r/g/b/a) for packed control textures?
- Do any current `.asset` files rely on effect ordering by int? Migration keeps 0-3 stable — confirm no code switches on `effect>3` elsewhere.
- Per-row AnimationCurve ever needed, or is 3-shape parametric (Linear/Gamma/Threshold) sufficient? Affects whether we add the R8 LUT bake path.
- TurnRateMod for Boid maps to steering-force cap (maxForce*foodSeekingStrength). Confirm reads as "turn strength" vs scaling sensorAngleRad.

**Track D (media bridge):**
- Warmth spatiality: localized Temperature stamp per swarm region, or single near-global tint (large radius, center UV)? Warmth is a global valence — confirm intended visual.
- Boid sensor mapping: `behavior/sensor` → sensorAngleRad, or alignRange/foodSensorDistance? Needs artist intent (boids have no chemotactic sense cone).
- Termite/Physarum cohesion: leave `behavior/cohesion` no-op, or approximate via senseDistance?
- Multiplier curve+range: neutral-0.5→1.0 linear [0.25,4], or exponential `2^((bias-0.5)*k)` for perceptual symmetry? Per-leaf defaults?
- Behavior multipliers decay to 1.0 on dropout (BiomeInjector.valueTimeout-style) or hold last value indefinitely?
- maestra rollout: will the OSC gateway preserve exact `/sn/<entity>/…` addresses or remap to bus-native topics? If remapped, is serialized prefix+entity map sufficient or is a leaf-name override table also needed?
