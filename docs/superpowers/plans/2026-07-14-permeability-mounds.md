# Permeability Mounds Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the static permeability noise with persistent, termite-built walls that partition the field into habitats each species is confined to.

**Architecture:** Permeability (channel 7) starts uniform-open and only changes where termites build. Termites lower permeability probabilistically (pulsing with neuron firing) via a dedicated GPU kernel; a near-zero relax makes walls persist. The per-species perception build gates agents by their authored `preferredPermeability` band (avoidance + speed penalty). `ResetTermites` clears channel 7 to open, freeing the confined species. A post-composite overlay paints the walls.

**Tech Stack:** Unity 6 (HDRP), C# MonoBehaviours, HLSL compute shaders (`Biome.compute`, `SimulationManager.compute`, `TermiteSim.compute`). Reference: the permeability-mounds design spec (`docs/superpowers/specs/2026-07-14-permeability-mounds-design.md`).

## Global Constraints

- Workspace: `Assets/Workspace/11.0 Biomes` (shared engine). Show assets: `Assets/Workspace/11.2 SIGGRAPH Scene/assets`.
- **No automated test harness exists** (no Unity Test Framework / NUnit). Every task verifies by **compile-clean (LSP diagnostics) + play-mode observation**, described per task.
- Reuse **permeability channel 7** — no new GPU field, no deterministic terrain.
- Semantic: **permeability high = open/passable, low = solid/wall.** Termites build *downward*.
- Authored habitat bands (already in the umwelt assets, currently unread): Boid `0.6–1`, Physarum `0.3–0.7`, Termite `0–0.5`.
- Commit messages: concise, **no attribution** (repo convention).
- Implement on a dedicated branch `feat/permeability-mounds` (created at execution start).
- Every new inspector field carries a `[Tooltip]`.

---

### Task 1: Persistence spine — uniform-open baseline, walls persist

Drop the noise from both the init kernel and the relax target; relax toward a uniform-open baseline with near-zero rate; keep `temp→perm` but tune it to ~0. After this task the *existing* termite `−0.75` umwelt dig **persists** instead of healing back to noise (Task 2 replaces that dig with gradual accretion).

**Files:**
- Modify: `Assets/Workspace/11.0 Biomes/src/computes/Biome.compute` (uniform decl ~line 58; `InitPermeabilityKernel` ~133–142; `InteractFieldsKernel` perm block ~237–252)
- Modify: `Assets/Workspace/11.0 Biomes/src/components/core/Biome.cs` (property ID ~line 93 area; set uniform in the init dispatch ~229 and the interact setup ~267)
- Modify: `Assets/Workspace/11.0 Biomes/src/components/core/BiomeFieldConfig.cs` (add `permeabilityOpenBaseline` near `temperatureToPermeability`)
- Modify: `Assets/Workspace/11.2 SIGGRAPH Scene/assets/BiomeFieldConfig_Homeostatic.asset` (perm channel `relaxRate`; `temperatureToPermeability`; add `permeabilityOpenBaseline`)

**Interfaces:**
- Consumes: nothing new.
- Produces: shader uniform `float permOpenBaseline`; C# field `BiomeFieldConfig.permeabilityOpenBaseline` (float, default 0.9).

- [ ] **Step 1: Add the shader uniform**

In `Biome.compute`, near the other cross-field uniforms (around line 56–59, by `tempToEvaporation`/`humidityGradientGain`), add:

```hlsl
float permOpenBaseline;      // open-ground permeability; termite mounds build downward from it
```

- [ ] **Step 2: Replace the noise init with the uniform baseline**

`Biome.compute` `InitPermeabilityKernel` — replace the body:

```hlsl
void InitPermeabilityKernel(uint3 id : SV_DISPATCHTHREADID) {
    if (id.x >= rezX || id.y >= rezY) return;
    // Uniform-open baseline: agent-authored termite mounds replace the static noise terrain.
    fieldWrite[uint3(id.xy, CH_PERMEABILITY)] = permOpenBaseline;
}
```

- [ ] **Step 3: Relax toward the open baseline, not the noise**

`Biome.compute` `InteractFieldsKernel`, the permeability block (~237–252) — replace the `relaxPerm > 0` branch's target:

```hlsl
    float relaxPerm = channelRelax[CH_PERMEABILITY];
    if (relaxPerm > 0.0) {
        // Relax toward uniform-open so built walls slowly heal to open ground, not to a fixed
        // noise terrain. Temperature coupling retained but tuned to ~0 (asset).
        float permTarget = saturate(permOpenBaseline + (temp - 0.5) * tempToPermeability);
        perm = lerp(perm, permTarget, relaxPerm);
    } else {
        perm = clamp(perm + (temp - 0.5) * tempToPermeability, 0, 1);
    }
```

- [ ] **Step 4: Add the C# config field**

`BiomeFieldConfig.cs`, next to `temperatureToPermeability`:

```csharp
        [Tooltip("Open-ground permeability the field starts at and slowly relaxes toward. Termite mounds build downward from this; replaces the old noise terrain.")]
        [Range(0f, 1f)] public float permeabilityOpenBaseline = 0.9f;
```

- [ ] **Step 5: Wire the uniform in `Biome.cs`**

Add the property ID by the others (~line 93):

```csharp
        private static readonly int s_PermOpenBaselineID = Shader.PropertyToID("permOpenBaseline");
```

Set it in **two** places (it must be current before the init dispatch AND for the interact kernel). Before the `initPermeabilityKernel` dispatch block (around line 229) and in the per-step interact-uniform setup (around line 267, by `cs.SetFloat(s_TempToPermID, ...)`), add:

```csharp
            cs.SetFloat(s_PermOpenBaselineID, fieldConfig.permeabilityOpenBaseline);
```

- [ ] **Step 6: Retune the SIGGRAPH asset**

In `BiomeFieldConfig_Homeostatic.asset`: on the `Permeability` channel row set `relaxRate: 0.0005` (near-zero heal); set `temperatureToPermeability: 0.02` (retained hook, ~off); and add a top-level `permeabilityOpenBaseline: 0.9` line (alongside `temperatureToEvaporation`, `humidityGradientGain`).

- [ ] **Step 7: Compile-clean**

Focus Unity; open Console. Expected: **no compile errors.** Confirm no stray noise use remains in the permeability paths:

```bash
cd "/Users/toka/Developer/Graphics/EoC-biomes-compute" && grep -n "FractalSimplexNoise\|permTerrain" "Assets/Workspace/11.0 Biomes/src/computes/Biome.compute"
```
Expected: **no matches inside `InitPermeabilityKernel` or the `InteractFieldsKernel` perm block** (noise may still appear elsewhere if another channel uses it — verify the two permeability sites are clean).

- [ ] **Step 8: Play-mode verify**

Open `Scene_SIGGRAPH`, Play. Open the biome debug grid; look at channel 7 (Permeability).
Expected: permeability starts **uniform** (flat mid-tone, no noise blobs). Where termites move, their `−0.75` digs now **persist** (dark marks stay instead of healing away). This confirms the persistence spine.

- [ ] **Step 9: Commit**

```bash
cd "/Users/toka/Developer/Graphics/EoC-biomes-compute"
git add "Assets/Workspace/11.0 Biomes/src/computes/Biome.compute" \
        "Assets/Workspace/11.0 Biomes/src/components/core/Biome.cs" \
        "Assets/Workspace/11.0 Biomes/src/components/core/BiomeFieldConfig.cs" \
        "Assets/Workspace/11.2 SIGGRAPH Scene/assets/BiomeFieldConfig_Homeostatic.asset"
git commit -m "biome: permeability starts uniform-open + persists (drop noise, relax to baseline, temp-perm ~0)"
```

---

### Task 2: Probabilistic firing-gated wall-build

Replace the termite's one-shot `−0.75` umwelt dig with gradual, probabilistic accretion that pulses with neuron firing, via a dedicated kernel modeled on `WriteFieldKernel`.

**Files:**
- Modify: `Assets/Workspace/11.0 Biomes/src/computes/Biome.compute` (add `BuildPermeabilityKernel` + `#pragma kernel` line 13 area + build uniforms)
- Modify: `Assets/Workspace/11.0 Biomes/src/components/core/Biome.cs` (add `buildPermeabilityKernel` find; `BuildPermeability(...)` method modeled on `WriteField` at line 498)
- Modify: `Assets/Workspace/11.0 Biomes/src/components/Sim/TermiteSim.cs` (add sim-level build fields + probability accessors)
- Modify: `Assets/Workspace/11.0 Biomes/src/components/core/SimulationManager.cs` (call `BuildPermeability` in the write-back loop ~line 328)
- Modify: `Assets/Workspace/11.2 SIGGRAPH Scene/assets/UmweltTermite_Alt.asset` (remove the `channel: 7, amount: -0.75` write entry)

**Interfaces:**
- Consumes: `Biome.FieldReadArray` (existing); `NeuronFiringSource.Buffer` + `.NeuronCount` (existing); `TermiteSim.agentParams.types[0].depositProbability`/`firingDepositProbability` (existing).
- Produces: `Biome.BuildPermeability(ComputeBuffer agentPositions, int agentCount, ComputeBuffer firing, int neuronCount, float depositProb, float firingDepositProb, float firingThreshold, float buildAmount, int timeSeed, int simRezX, int simRezY)`; `TermiteSim.wallBuildAmount` (float), `TermiteSim.WallBuildProbability`/`WallBuildFiringProbability`/`wallBuildFiringThreshold`.

- [ ] **Step 1: Declare the build kernel**

`Biome.compute`, in the `#pragma kernel` list (~line 11–13), add:

```hlsl
#pragma kernel BuildPermeabilityKernel
```

- [ ] **Step 2: Add the build kernel + uniforms**

`Biome.compute`, after `WriteFieldKernel` (~line 350), add the uniforms and kernel (reuses existing `agentPositions`, `fieldWrite`, `rezX/rezY`, `simToFieldX/Y`, `agentCount`):

```hlsl
// --- Termite mound build (probabilistic, firing-gated permeability lowering) ---
StructuredBuffer<float> buildFiring;   // per-neuron firing intensity (shared source)
uint  buildNeuronCount;
float buildDepositProb;                // baseline per-step build chance
float buildFiringDepositProb;          // build chance when the agent's neuron is firing
float buildFiringThreshold;            // firing intensity above which the firing chance applies
float buildAmount;                     // permeability lowered per build event (positive)
uint  buildTimeSeed;                   // per-step seed for the per-agent RNG

float BuildHash(uint a, uint b) {
    uint h = a * 747796405u + b * 2891336453u;
    h = (h ^ (h >> 16)) * 2246822519u;
    h = (h ^ (h >> 13)) * 3266489917u;
    h ^= h >> 16;
    return (h & 0x00FFFFFFu) / 16777216.0;   // 0..1
}

[numthreads(256, 1, 1)]
void BuildPermeabilityKernel(uint3 id : SV_DISPATCHTHREADID) {
    if (id.x >= agentCount) return;
    float2 simPos = agentPositions[id.x].position;
    uint2 fieldPos = uint2(
        clamp(simPos.x * simToFieldX, 0, rezX - 1),
        clamp(simPos.y * simToFieldY, 0, rezY - 1)
    );
    // Build pulses with the agent's neuron (agent i -> neuron i % neuronCount).
    float firing = buildNeuronCount > 0 ? buildFiring[id.x % buildNeuronCount] : 0.0;
    float prob = firing > buildFiringThreshold ? buildFiringDepositProb : buildDepositProb;
    if (BuildHash(id.x, buildTimeSeed) < prob) {
        float current = fieldWrite[uint3(fieldPos, CH_PERMEABILITY)];
        fieldWrite[uint3(fieldPos, CH_PERMEABILITY)] = clamp(current - buildAmount, 0, 1);
    }
}
```

- [ ] **Step 3: Find the kernel in `Biome.cs`**

Add the field by the other kernel handles (~line 75):

```csharp
        private int buildPermeabilityKernel;
```

In `FindKernels()` (~line 169, by `writeFieldKernel = ...`):

```csharp
            buildPermeabilityKernel = cs.FindKernel("BuildPermeabilityKernel");
```

- [ ] **Step 4: Add `BuildPermeability` to `Biome.cs`**

Modeled on `WriteField` (line 498). Add after it:

```csharp
        // Termite mound build: lower permeability at agent positions, probabilistically and
        // pulsing with neuron firing. Writes into fieldReadArray (same target as WriteField),
        // so it rides the field ping-pong. See permeability-mounds spec.
        public void BuildPermeability(ComputeBuffer agentPositions, int agentCount,
            ComputeBuffer firing, int neuronCount,
            float depositProb, float firingDepositProb, float firingThreshold,
            float buildAmount, int timeSeed, int simRezX, int simRezY)
        {
            if (cs == null || fieldReadArray == null || agentPositions == null || agentCount <= 0) return;
            cs.SetInt("agentCount", agentCount);
            cs.SetFloat("simToFieldX", (float)biomeRezX / Mathf.Max(1, simRezX));
            cs.SetFloat("simToFieldY", (float)biomeRezY / Mathf.Max(1, simRezY));
            cs.SetFloat("buildDepositProb", depositProb);
            cs.SetFloat("buildFiringDepositProb", firingDepositProb);
            cs.SetFloat("buildFiringThreshold", firingThreshold);
            cs.SetFloat("buildAmount", buildAmount);
            cs.SetInt("buildNeuronCount", firing != null ? neuronCount : 0);
            cs.SetInt("buildTimeSeed", timeSeed);
            if (firing != null) cs.SetBuffer(buildPermeabilityKernel, "buildFiring", firing);
            cs.SetBuffer(buildPermeabilityKernel, "agentPositions", agentPositions);
            cs.SetTexture(buildPermeabilityKernel, s_FieldWriteID, fieldReadArray);
            Dispatch(buildPermeabilityKernel, agentCount, 1, 1);
        }
```

Note: if `buildFiring` is left unbound when `firing == null`, guard by setting `buildNeuronCount = 0` (done above) so the kernel never indexes it; also bind a 1-element dummy buffer if the platform requires all `StructuredBuffer`s bound — if the Console warns about an unbound buffer, bind `firing ?? _dummyFiringBuffer` (a persistent 1-float `ComputeBuffer` allocated in `Allocate()`).

- [ ] **Step 5: Add termite build fields**

`TermiteSim.cs`, with the other serialized fields:

```csharp
        [Header("Mound build (permeability)")]
        [Tooltip("Permeability lowered per build event as termites construct walls/mounds. 0 = no building.")]
        public float wallBuildAmount = 0.02f;
        [Tooltip("Firing intensity above which a termite builds at the firing (burst) probability.")]
        [Range(0f, 1f)] public float wallBuildFiringThreshold = 0.1f;

        // Reuse the type's trail-deposit cadence as the build cadence (building pulses with firing).
        public float WallBuildProbability =>
            agentParams != null && agentParams.types.Count > 0 ? agentParams.types[0].depositProbability : 0.2f;
        public float WallBuildFiringProbability =>
            agentParams != null && agentParams.types.Count > 0 ? agentParams.types[0].firingDepositProbability : 0.3f;
```

- [ ] **Step 6: Call the build from `SimulationManager`**

In `SimulationManager.cs`, inside the write-back `for` loop over sims, after the umwelt-write block and before the loop closes (~line 327), add:

```csharp
                    // Termite mound build: probabilistic, firing-gated permeability lowering.
                    if (sim is TermiteSim termite && termite.wallBuildAmount > 0f)
                    {
                        biome.BuildPermeability(
                            posBuffer, agentCount,
                            neuronFiring != null ? neuronFiring.Buffer : null,
                            neuronFiring != null ? neuronFiring.NeuronCount : 0,
                            termite.WallBuildProbability, termite.WallBuildFiringProbability,
                            termite.wallBuildFiringThreshold, termite.wallBuildAmount,
                            _simStepCount, sim.rezX, sim.rezY);
                    }
```

- [ ] **Step 7: Remove the old one-shot dig**

In `UmweltTermite_Alt.asset`, delete the two lines of the `writes` entry:

```yaml
  - channel: 7
    amount: -0.75
```

(Permeability is now authored solely by `BuildPermeability`.)

- [ ] **Step 8: Compile-clean**

Focus Unity. Expected: **no compile errors.** Verify:

```bash
cd "/Users/toka/Developer/Graphics/EoC-biomes-compute" && grep -n "BuildPermeabilityKernel\|BuildPermeability(" "Assets/Workspace/11.0 Biomes/src/computes/Biome.compute" "Assets/Workspace/11.0 Biomes/src/components/core/Biome.cs" "Assets/Workspace/11.0 Biomes/src/components/core/SimulationManager.cs" && grep -n "channel: 7" "Assets/Workspace/11.2 SIGGRAPH Scene/assets/UmweltTermite_Alt.asset" || echo "  (no ch7 write remains — good)"
```

- [ ] **Step 9: Play-mode verify**

Play `Scene_SIGGRAPH`; watch permeability (debug grid ch7).
Expected: walls **accrete gradually** (no instant black holes), **persist**, and build **faster while the neuron firing is active** (scrub `/index` via `tools/osc_index_tester.py` and watch build rate rise). Set `wallBuildAmount = 0` → building stops.

- [ ] **Step 10: Commit**

```bash
cd "/Users/toka/Developer/Graphics/EoC-biomes-compute"
git add "Assets/Workspace/11.0 Biomes/src/computes/Biome.compute" \
        "Assets/Workspace/11.0 Biomes/src/components/core/Biome.cs" \
        "Assets/Workspace/11.0 Biomes/src/components/Sim/TermiteSim.cs" \
        "Assets/Workspace/11.0 Biomes/src/components/core/SimulationManager.cs" \
        "Assets/Workspace/11.2 SIGGRAPH Scene/assets/UmweltTermite_Alt.asset"
git commit -m "sim: probabilistic firing-gated termite mound build (replaces one-shot perm dig)"
```

---

### Task 3: Habitat confinement — wire the dead `preferredPermeability` gate

A cell outside a species' preferred permeability band repels + slows that species, confining each to its band.

**Files:**
- Modify: `Assets/Workspace/11.0 Biomes/src/computes/Biome.compute` (`ReadFieldKernel` ~402–424 + band/gain uniforms)
- Modify: `Assets/Workspace/11.0 Biomes/src/components/core/Biome.cs` (`BuildPerceptionTex` ~line 578: set band + gain uniforms; add `habitatAvoidGain`/`habitatSlowGain` serialized fields)

**Interfaces:**
- Consumes: `UmweltMapping.preferredPermeabilityMin`/`preferredPermeabilityMax` (existing, per sim).
- Produces: shader uniforms `habitatBandMin`, `habitatBandMax`, `habitatAvoidGain`, `habitatSlowGain`; `Biome.habitatAvoidGain`/`habitatSlowGain` (float fields).

- [ ] **Step 1: Add uniforms + gate to `ReadFieldKernel`**

`Biome.compute`, near the perception uniforms, add:

```hlsl
float habitatBandMin;    // this species' preferred permeability band (min)
float habitatBandMax;    // ... (max)
float habitatAvoidGain;  // out-of-band -> avoidance
float habitatSlowGain;   // out-of-band -> speed penalty
```

In `ReadFieldKernel`, after the read-entry loop and before writing `perceptionTex` (~line 415), add:

```hlsl
    // Habitat confinement: out-of-band permeability repels + slows this species (steer back
    // toward its termite-grown territory). Bands overlap across species -> soft borders.
    float habPerm = fieldRead.SampleLevel(sampler_fieldRead, float3(uv, CH_PERMEABILITY), 0);
    float outOfBand = max(0.0, habitatBandMin - habPerm) + max(0.0, habPerm - habitatBandMax);
    avoidance += outOfBand * habitatAvoidGain;
    speedMod  *= saturate(1.0 - outOfBand * habitatSlowGain);
```

(`uv` and `sampler_fieldRead` are already in scope in this kernel — confirm the local UV variable name matches what the existing samples use; reuse it.)

- [ ] **Step 2: Add Biome tuning fields**

`Biome.cs`, with the other serialized fields:

```csharp
        [Header("Habitat confinement")]
        [Tooltip("Strength of steer-back when an agent is outside its preferred permeability band.")]
        [Range(0f, 8f)] public float habitatAvoidGain = 2f;
        [Tooltip("Speed penalty when an agent is outside its band (1 unit out-of-band -> this fraction slower).")]
        [Range(0f, 8f)] public float habitatSlowGain = 3f;
```

Add property IDs by the others (~line 93):

```csharp
        private static readonly int s_HabitatBandMinID  = Shader.PropertyToID("habitatBandMin");
        private static readonly int s_HabitatBandMaxID  = Shader.PropertyToID("habitatBandMax");
        private static readonly int s_HabitatAvoidGainID = Shader.PropertyToID("habitatAvoidGain");
        private static readonly int s_HabitatSlowGainID  = Shader.PropertyToID("habitatSlowGain");
```

- [ ] **Step 3: Set the uniforms per species in `BuildPerceptionTex`**

`Biome.cs` `BuildPerceptionTex(sim)` (~line 578) — before dispatching `readFieldKernel`, add (the method receives the sim, which exposes `umwelt`):

```csharp
            cs.SetFloat(s_HabitatBandMinID, sim.umwelt != null ? sim.umwelt.preferredPermeabilityMin : 0f);
            cs.SetFloat(s_HabitatBandMaxID, sim.umwelt != null ? sim.umwelt.preferredPermeabilityMax : 1f);
            cs.SetFloat(s_HabitatAvoidGainID, habitatAvoidGain);
            cs.SetFloat(s_HabitatSlowGainID, habitatSlowGain);
```

(If `BuildPerceptionTex`'s parameter isn't named `sim` / doesn't expose `umwelt`, adapt to the actual accessor — the method is already per-sim, so the band source is whatever sim it builds for.)

- [ ] **Step 4: Compile-clean**

Focus Unity. Expected: **no compile errors.**

```bash
cd "/Users/toka/Developer/Graphics/EoC-biomes-compute" && grep -n "habitatBandMin\|habitatAvoidGain" "Assets/Workspace/11.0 Biomes/src/computes/Biome.compute" "Assets/Workspace/11.0 Biomes/src/components/core/Biome.cs"
```

- [ ] **Step 5: Play-mode verify**

Play `Scene_SIGGRAPH`. Let termites build for a bit (so low-perm walls exist).
Expected: visible **segregation** — Boids stay in open (high-perm) areas, Physarum tracks the wall edges (mid), Termites stay in/near their walls (low). At the very start (uniform-open) only Boids are "home"; as walls form, Physarum/Termite territory appears (succession). Tune `habitatAvoidGain`/`habitatSlowGain` live so agents are confined but not frozen.

- [ ] **Step 6: Commit**

```bash
cd "/Users/toka/Developer/Graphics/EoC-biomes-compute"
git add "Assets/Workspace/11.0 Biomes/src/computes/Biome.compute" \
        "Assets/Workspace/11.0 Biomes/src/components/core/Biome.cs"
git commit -m "biome: confine species to preferred-permeability bands (wire the dead habitat gate)"
```

---

### Task 4: Termite-owned mounds — `ResetTermites` melts them

Resetting termites clears permeability back to open, freeing the confined species; other per-family resets leave it alone.

**Files:**
- Modify: `Assets/Workspace/11.0 Biomes/src/components/core/Biome.cs` (add `ClearPermeability()`)
- Modify: `Assets/Workspace/11.0 Biomes/src/components/core/SimulationManager.cs` (`ResetTermites` ~line 512)

**Interfaces:**
- Consumes: `initPermeabilityKernel` (existing), `s_PermOpenBaselineID` (Task 1).
- Produces: `Biome.ClearPermeability()`.

- [ ] **Step 1: Add `ClearPermeability` to `Biome.cs`**

Models the reset init dispatch (line 229–232):

```csharp
        // Reset permeability (the termite-built mounds) to uniform-open in both ping-pong
        // buffers, without touching any other channel. Called when termites reset.
        public void ClearPermeability()
        {
            if (cs == null || fieldReadArray == null) return;
            cs.SetFloat(s_PermOpenBaselineID, fieldConfig.permeabilityOpenBaseline);
            cs.SetTexture(initPermeabilityKernel, s_FieldWriteID, fieldWriteArray);
            Dispatch(initPermeabilityKernel, biomeRezX, biomeRezY, 1);
            cs.SetTexture(initPermeabilityKernel, s_FieldWriteID, fieldReadArray);
            Dispatch(initPermeabilityKernel, biomeRezX, biomeRezY, 1);
        }
```

- [ ] **Step 2: Melt mounds on `ResetTermites`**

`SimulationManager.cs`, replace the `ResetTermites` one-liner (~line 512):

```csharp
        [Button("Reset Termites Only")] public void ResetTermites() { ResetSimsOfType<TermiteSim>(); biome?.ClearPermeability(); }
```

(Leave `ResetPhysarum`/`ResetBoids` unchanged — they must NOT clear permeability.)

- [ ] **Step 3: Compile-clean**

Focus Unity. Expected: **no compile errors.**

- [ ] **Step 4: Play-mode verify**

Play; let walls build and species segregate. Trigger `ResetTermites` (inspector button or OSC `/sim_resetTermites`).
Expected: walls **vanish** (permeability → uniform-open), Physarum/Boids **spill out** of their former territories, and Physarum/Boid/biome state otherwise keeps running (only termites + mounds reset). Confirm `ResetPhysarum`/`ResetBoids` do **not** clear the walls.

- [ ] **Step 5: Commit**

```bash
cd "/Users/toka/Developer/Graphics/EoC-biomes-compute"
git add "Assets/Workspace/11.0 Biomes/src/components/core/Biome.cs" \
        "Assets/Workspace/11.0 Biomes/src/components/core/SimulationManager.cs"
git commit -m "sim: ResetTermites clears permeability (melts mounds, frees confined species)"
```

---

### Task 5: Composite mound overlay

Paint the built walls onto the final composite, independent of the additive sim blend — modeled on the existing post-composite `NeuronRingKernel`.

**Files:**
- Modify: `Assets/Workspace/11.0 Biomes/src/computes/SimulationManager.compute` (add `MoundOverlayKernel` + uniforms, after the ring kernel ~line 118)
- Modify: `Assets/Workspace/11.0 Biomes/src/components/core/SimulationManager.cs` (serialized fields; dispatch after the ring pass ~line 442; needs `biome.FieldReadArray`)

**Interfaces:**
- Consumes: `Biome.FieldReadArray` (existing getter, `Biome.cs:111`); `BiomeChannel.Permeability` (=7); `compositeOut` (existing).
- Produces: `SimulationManager.moundOverlayStrength` (float), `moundColor` (Color).

- [ ] **Step 1: Add the overlay kernel**

`SimulationManager.compute`, declare the kernel (with the other `#pragma kernel` lines) and add after `NeuronRingKernel` (~line 118):

```hlsl
#pragma kernel MoundOverlayKernel

Texture2DArray<float> permField;
SamplerState sampler_permField;
int   permChannel;         // BiomeChannel.Permeability
float permOpenBaselineOv;  // "open" value; wall-ness = (baseline - perm)/baseline
float moundStrength;
float4 moundColor;

[numthreads(8, 8, 1)]
void MoundOverlayKernel(uint3 id : SV_DISPATCHTHREADID) {
    uint w, h; compositeOut.GetDimensions(w, h);
    if (id.x >= w || id.y >= h) return;
    float2 uv = (float2(id.xy) + 0.5) / float2(w, h);
    float perm = permField.SampleLevel(sampler_permField, float3(uv, permChannel), 0);
    float wall = saturate((permOpenBaselineOv - perm) / max(1e-4, permOpenBaselineOv));  // 0 open, 1 solid
    float4 c = compositeOut[id.xy];
    c.rgb = lerp(c.rgb, moundColor.rgb, wall * moundStrength);
    compositeOut[id.xy] = c;
}
```

(Confirm `compositeOut` is the same `RWTexture2D` the ring kernel writes; reuse its declaration.)

- [ ] **Step 2: Add manager fields + property IDs**

`SimulationManager.cs`:

```csharp
        [Header("Mound overlay")]
        [Tooltip("How strongly termite-built walls are painted over the composite (0 = off).")]
        [Range(0f, 1f)] public float moundOverlayStrength = 0.5f;
        [Tooltip("Colour of the painted mounds/walls.")]
        public Color moundColor = new(0.25f, 0.18f, 0.12f, 1f);
        private int moundOverlayKernel = -1;
```

Find the kernel where the other manager kernels are found (add `moundOverlayKernel = compositeCS.FindKernel("MoundOverlayKernel");` alongside the ring kernel lookup).

- [ ] **Step 3: Dispatch after the ring pass**

`SimulationManager.cs` `Render()`, after the neuron-ring dispatch block (~line 442) and gated on strength + a valid biome, add (model the bind/dispatch on the ring pass):

```csharp
            if (moundOverlayStrength > 0f && biome != null && biome.FieldReadArray != null)
            {
                compositeCS.SetTexture(moundOverlayKernel, "permField", biome.FieldReadArray);
                compositeCS.SetTexture(moundOverlayKernel, "compositeOut", compositeOutTex);
                compositeCS.SetInt("permChannel", BiomeChannel.Permeability);
                compositeCS.SetFloat("permOpenBaselineOv", biome.OpenBaseline);
                compositeCS.SetFloat("moundStrength", moundOverlayStrength);
                compositeCS.SetVector("moundColor", moundColor);
                int gx = Mathf.CeilToInt(rezX / 8f), gy = Mathf.CeilToInt(rezY / 8f);
                compositeCS.Dispatch(moundOverlayKernel, gx, gy, 1);
            }
```

(Use the actual composite compute-shader field name and the actual composite-output texture name from `Render()`; `compositeCS`/`compositeOutTex` are placeholders for whatever the ring pass uses.)

- [ ] **Step 4: Expose the open baseline on `Biome`**

`Biome.cs`, add a getter (used by the overlay for wall-ness):

```csharp
        public float OpenBaseline => fieldConfig != null ? fieldConfig.permeabilityOpenBaseline : 0.9f;
```

- [ ] **Step 5: Compile-clean**

Focus Unity. Expected: **no compile errors.**

```bash
cd "/Users/toka/Developer/Graphics/EoC-biomes-compute" && grep -n "MoundOverlayKernel\|moundOverlayStrength" "Assets/Workspace/11.0 Biomes/src/computes/SimulationManager.compute" "Assets/Workspace/11.0 Biomes/src/components/core/SimulationManager.cs"
```

- [ ] **Step 6: Play-mode verify**

Play; let termites build. Expected: the walls appear as **earthy/dark structure painted over the composite**, independent of trail brightness. `moundOverlayStrength = 0` hides them; raising it deepens them. Tune `moundColor` to taste.

- [ ] **Step 7: Commit**

```bash
cd "/Users/toka/Developer/Graphics/EoC-biomes-compute"
git add "Assets/Workspace/11.0 Biomes/src/computes/SimulationManager.compute" \
        "Assets/Workspace/11.0 Biomes/src/components/core/SimulationManager.cs" \
        "Assets/Workspace/11.0 Biomes/src/components/core/Biome.cs"
git commit -m "render: composite mound overlay paints termite-built walls"
```

---

### Task 6 (optional): Per-run seed bootstrap

Only if the uniform-open start reads too slow (no termite/physarum habitat until walls form). Seeds a few faint, per-run-varied low-perm blobs at init so habitat exists immediately — NOT the old repetitive fBM.

**Files:**
- Modify: `Assets/Workspace/11.0 Biomes/src/computes/Biome.compute` (`InitPermeabilityKernel` + seed uniforms)
- Modify: `Assets/Workspace/11.0 Biomes/src/components/core/Biome.cs` (`seedMounds` toggle + `seedRandom` set per Reset)

**Interfaces:**
- Consumes: `permOpenBaseline` (Task 1).
- Produces: shader uniforms `float seedStrength`, `float2 seedRandom`; `Biome.seedMounds` (bool).

- [ ] **Step 1: Add seed to the init kernel**

`Biome.compute`, uniforms + `InitPermeabilityKernel`:

```hlsl
float  seedStrength;   // 0 = pure uniform-open (default)
float2 seedRandom;     // per-run offset so the seed differs each Reset
```

```hlsl
void InitPermeabilityKernel(uint3 id : SV_DISPATCHTHREADID) {
    if (id.x >= rezX || id.y >= rezY) return;
    float perm = permOpenBaseline;
    if (seedStrength > 0.0) {
        float2 uv = (float2(id.xy) + 0.5) / float2(rezX, rezY);
        // A few soft low-perm blobs, offset per run (not the old tiling fBM).
        float n = FractalSimplexNoise((uv + seedRandom) * 2.0);
        perm = lerp(perm, perm * (1.0 - seedStrength), smoothstep(0.55, 0.8, n));
    }
    fieldWrite[uint3(id.xy, CH_PERMEABILITY)] = perm;
}
```

- [ ] **Step 2: Toggle + per-run offset in `Biome.cs`**

Add:

```csharp
        [Tooltip("Seed a few faint low-perm blobs at init so termite/physarum habitat exists immediately (per-run varied, not the old noise). 0 = pure uniform-open.")]
        [Range(0f, 1f)] public float seedMounds = 0f;
```

Before the init dispatch (~line 229), set the uniforms (per-run offset from a stable-but-varying source, e.g. `Time.realtimeSinceStartup`, since `Random`/`Time` at edit is acceptable here):

```csharp
            cs.SetFloat("seedStrength", seedMounds);
            cs.SetVector("seedRandom", new Vector4(_seedOffset.x, _seedOffset.y, 0, 0));
```

Compute `_seedOffset` once per Reset (`private Vector2 _seedOffset;` set in `Reset()` from `UnityEngine.Random.value`).

- [ ] **Step 3: Compile + play-mode verify**

Focus Unity; set `seedMounds = 0.3`, Reset. Expected: a few soft dark patches at start (differs each Reset); `seedMounds = 0` → pure uniform-open. No repetitive tiling.

- [ ] **Step 4: Commit**

```bash
cd "/Users/toka/Developer/Graphics/EoC-biomes-compute"
git add "Assets/Workspace/11.0 Biomes/src/computes/Biome.compute" \
        "Assets/Workspace/11.0 Biomes/src/components/core/Biome.cs"
git commit -m "biome: optional per-run seed to bootstrap mound habitat"
```

---

## Post-implementation
- **Docs:** flip `docs/ROADMAP.md` "Permeability mounds" In-Design → Shipped; add a `docs/sessions/` entry via the `eoc-docs` skill; update `INTEGRATION_DESIGN.md` (permeability-as-topography / habitat notes) before merging the branch to `main`.
- **Merge:** open a PR from `feat/permeability-mounds` → `main` once play-mode tuning holds.

## Self-Review
- **Spec coverage:** persistence spine (Task 1) ✓; probabilistic firing-gated build (Task 2) ✓; habitat confinement via authored bands (Task 3) ✓; ResetTermites melt (Task 4) ✓; composite overlay (Task 5) ✓; optional seed (Task 6) ✓; keep-temp-perm-at-~0 (Task 1 Step 6) ✓; near-zero decay (Task 1 Step 6) ✓; reuse deposit/firing probabilities (Task 2 Step 5) ✓. Non-goals (curvature, humidity-scaffold, discrete types) correctly excluded.
- **Placeholder scan:** all code steps show full HLSL/C#; the three "confirm the actual name" notes (perception UV var, composite texture/CS field names, BuildPerceptionTex param) are integration-point checks against existing code, not missing content — each names the exact existing element to match.
- **Type consistency:** `BuildPermeability(...)` signature identical in Task 2 Interfaces + Biome.cs method + manager call; `permOpenBaseline`/`permeabilityOpenBaseline`/`OpenBaseline` used consistently (shader uniform / config field / getter); `wallBuildAmount`, `WallBuildProbability`, `WallBuildFiringProbability`, `wallBuildFiringThreshold` consistent across TermiteSim + manager; `ClearPermeability()` single name; `habitatBandMin/Max`/`habitatAvoidGain`/`habitatSlowGain` consistent shader↔C#.
