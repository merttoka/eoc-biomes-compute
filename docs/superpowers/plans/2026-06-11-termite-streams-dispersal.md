# Termite Ballistic Streams, Visible Trails, Firing Shockwaves & Dispersal Channel — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn EoC termites into fixed-heading ballistic streams with visible trails, make neuron firing a dramatic expanding shockwave that physically scatters agents, via a new firing-driven Dispersal biome channel.

**Architecture:** Four coupled features in `Assets/Workspace/11.0 Biomes`. Termites keep an immutable per-neuron heading and only bend when a Dispersal field pushes them (perception-gradient flee), then re-form. A new biome channel (`Dispersal`, index 10) is injected by firing neurons (and OSC), read as negative chemotaxis by all sims through the existing Umwelt→perception pipeline. The neuron-ring overlay gains an expanding-radius shockwave + core flash.

**Tech Stack:** Unity URP, HLSL compute shaders, C# MonoBehaviour/ScriptableObject. No unit-test harness — verification is "compiles clean + observe in Play mode". Branch: `feat/termite-streams-dispersal` (already created).

**Reference (stay true where noted):** `/Users/toka/Developer/Graphics/PDE_Nefeli_Termites`. Deliberate divergence: the reference re-steers via chemotaxis every frame; we remove that. Spec: `docs/superpowers/specs/2026-06-11-termite-biome-features-design.md`.

**Verification conventions (every task):**
- "Compiles": Unity recompiles with no errors in the Console (C#) and no shader-compilation errors on the `.compute` asset (select it; the inspector shows errors).
- "Play": enter Play mode on `Assets/Workspace/11.0 Biomes/scene/Scene_CURRENTS.unity` and observe the stated outcome.
- Commit after each task. Branch is already feature-isolated, so commit directly to it.

---

## File Structure (what each task touches)

- `src/computes/TermiteSim.compute` — per-neuron seed (F1), perception-gradient flee + immutable heading (F1/F4), render brightness (F2).
- `src/components/Sim/TermiteSim.cs` — `dispersalResponse` uniform bind (F4).
- `assets/TermiteParams.asset` — trail tuning (F2).
- `src/components/core/BiomeFieldConfig.cs` — `Dispersal` channel constant/name/default (F4).
- `assets/BiomeFieldConfig_Homeostatic.asset` — serialized Dispersal channel settings (F4).
- `src/computes/Biome.compute` — `CH_COUNT` 10→11 (F4).
- `src/components/network/ExternalTextureSender.cs` — `ChannelNames` add "Dispersal" (F4).
- `assets/UmweltTermite.asset` — strip to dispersal-flee + permeability-speed (F1/F4).
- `assets/UmweltBoid.asset`, `assets/UmweltPhysarum.asset` — add dispersal-flee read (F4).
- `src/components/network/BiomeInjector.cs` — firing-driven dispersal stamps (F4).
- `src/computes/SimulationManager.compute` — expanding shockwave + core flash (F3).
- `src/components/core/SimulationManager.cs` — shockwave uniforms (F3).

---

## Phase F1 — Termite per-group turn angles (curvy streams)

> **REVISED & SHIPPED (commit `96a8bef`).** Tasks 1-2 below describe the original
> *ballistic / straight-line* approach, which was implemented then reverted after
> Unity verification. The shipped behavior instead: keep `SensorTurns` chemotaxis
> (curvy/wavy), give each `i % neuronCount` group its own fixed random turn-angle
> magnitude via a new `turnAngleSpread` uniform (default 0.8), keep the per-neuron
> heading seed, and drop `agentsCount` to 131 (1:1 with neurons — the main perf
> lever). Tasks 1-2 are retained for history; the live code reflects the revision.

### Task 1: Per-neuron heading seed

**Files:**
- Modify: `src/computes/TermiteSim.compute:73`

- [ ] **Step 1: Change the heading seed to be per-neuron**

In `ResetAgentsKernel`, replace line 73:
```hlsl
    float ang0 = Hash1u(id.x * 747796405u + time) * 6.28318530718;
```
with:
```hlsl
    // Per-neuron heading: agents sharing a neuron index get ONE fixed heading →
    // coherent directional streams (one per neuron). Falls back to per-agent when
    // no neuron CSV is wired.
    uint headingSeed = (neuronCount > 0) ? (id.x % neuronCount) : id.x;
    float ang0 = Hash1u(headingSeed * 747796405u + time) * 6.28318530718;
```

- [ ] **Step 2: Verify it compiles**

Select `TermiteSim.compute` in the Project window; Console shows no shader errors.

- [ ] **Step 3: Commit**

```bash
git add "Assets/Workspace/11.0 Biomes/src/computes/TermiteSim.compute"
git commit -m "feat(termite): per-neuron heading seed for coherent streams"
```

---

### Task 2: Immutable heading + remove chemotaxis steering (keep wiggle)

This replaces the chemotaxis `SensorTurns` call with the agent's fixed heading, keeps the ±0.05 wiggle, and preserves the heading across frames by storing a unit vector in `a.direction` (never overwriting it with `heading*speed`). The dispersal-flee term is added in Phase F4 — for now termites fly straight.

**Files:**
- Modify: `src/computes/TermiteSim.compute` (`MoveAgentsKernel`, lines 144-175)

- [ ] **Step 1: Replace `MoveAgentsKernel` body**

Replace the whole kernel (lines 144-175) with:
```hlsl
[numthreads(1024, 1, 1)]
void MoveAgentsKernel(uint3 id : SV_DISPATCHTHREADID) {
    if (id.x >= agentsCount) return;

    Agent a = agentsIn[id.x];
    TermiteTypeParams p = typeParams[a.typeId];

    // Fixed assigned heading (immutable for life). Ballistic — no pheromone chemotaxis.
    float2 h0 = normalize(a.direction);
    float2 dir = h0;

    // --- dispersal flee inserted here in Phase F4 ---

    // Organic wiggle (~±0.05 rad), non-accumulating so the heading stays fixed.
    float w = (Hash1u(id.x * 668265263u + time * 2246822519u) - 0.5) * 0.1;
    dir = RotateVectorBy(dir, w);

    // Biome speed multiplier (permeability, perception G) + firing boost.
    float2 uv = a.position / float2((float)rezX, (float)rezY);
    float speedMult = perceptionTex.SampleLevel(sampler_perceptionTex, uv, 0).g;
    float fireMul = IsFiring(id.x, neuronCount) ? p.firingSpeedMul : 1.0;
    float effectiveSpeed = p.moveSpeed * speedMult * fireMul;

    a.position += dir * effectiveSpeed;

    // Toroidal wrapping
    float fRezX = (float)rezX;
    float fRezY = (float)rezY;
    if (a.position.x < 0) a.position.x += fRezX;
    if (a.position.x >= fRezX) a.position.x -= fRezX;
    if (a.position.y < 0) a.position.y += fRezY;
    if (a.position.y >= fRezY) a.position.y -= fRezY;

    // Keep the immutable heading (unit). Do NOT store dir*speed.
    a.direction = h0;
    agentsOut[id.x] = a;
}
```

- [ ] **Step 2: Delete the now-unused `SensorTurns` function**

Remove the entire `SensorTurns` function (lines 88-142). Keep `RotateVectorBy` (still used). The `trailRead` global stays declared (used by Diffuse/Render).

- [ ] **Step 3: Verify it compiles**

Select `TermiteSim.compute`; no shader errors. (The `s_TrailReadID` bind to `moveAgentsKernel` in `TermiteSim.cs:124` is now a harmless no-op; leave it.)

- [ ] **Step 4: Play — observe ballistic streams**

Enter Play mode. Expected: termites travel in straight lines along ~131 distinct directions (coherent streams), with slight shimmer from the wiggle. They no longer clump into pheromone-following paths. Trails will look faint — fixed in Phase F2.

- [ ] **Step 5: Commit**

```bash
git add "Assets/Workspace/11.0 Biomes/src/computes/TermiteSim.compute"
git commit -m "feat(termite): ballistic fixed-heading movement, drop chemotaxis steering"
```

---

## Phase F2 — Visible termite trails

### Task 3: Render brightness to full + trail tuning

**Files:**
- Modify: `src/computes/TermiteSim.compute:240` (`RenderKernel`)
- Modify: `assets/TermiteParams.asset` (diffuseRate, depositAmount, depositProbability)
- Inspect: termite `compositeWeight` in `Scene_CURRENTS.unity` (via Inspector, not file edit)

- [ ] **Step 1: Lift render brightness**

In `RenderKernel`, replace line 240:
```hlsl
        float4 c = hsb2rgb(float3(p.hue, p.saturation * (1.0 - white), 0.8 * baseB), baseB);
```
with:
```hlsl
        float4 c = hsb2rgb(float3(p.hue, p.saturation * (1.0 - white), baseB), baseB);
```

- [ ] **Step 2: Verify it compiles**

Select `TermiteSim.compute`; no errors.

- [ ] **Step 3: Tune trail params in the asset**

Open `Assets/Workspace/11.0 Biomes/assets/TermiteParams.asset` in the Inspector (or edit YAML). On the termite agent type, set:
- `diffuseRate`: `0.99` (slow fade / crisp persistent streaks — was ~0.97)
- `depositAmount`: `0.5` (was 0.3)
- `depositProbability`: `0.4` (was 0.2)

These are starting values; the range allows live tuning.

- [ ] **Step 4: Confirm the termite composite weight is non-zero**

In the scene, select the termite `SimulationBase`-derived component and confirm `compositeWeight` > 0 (default 1). Give termites a hue distinct from Boids/Physarum (`hue` on the type). If it was 0, set it to 1.

- [ ] **Step 5: Play — observe visible streams**

Enter Play mode. Expected: the 131 termite streams now read clearly as colored lines in the composite, distinct from the permeability mounds and the other sims.

- [ ] **Step 6: Commit**

```bash
git add "Assets/Workspace/11.0 Biomes/src/computes/TermiteSim.compute" "Assets/Workspace/11.0 Biomes/assets/TermiteParams.asset" "Assets/Workspace/11.0 Biomes/scene/Scene_CURRENTS.unity"
git commit -m "feat(termite): visible trails — full render brightness + crisp-streak tuning"
```

---

## Phase F4 — Dispersal (agitation) biome channel

*(F4 before F3 because the shockwave wiring in F3 depends on the channel existing.)*

### Task 4: Declare the Dispersal channel (C# + shader + sender + asset)

**Files:**
- Modify: `src/components/core/BiomeFieldConfig.cs`
- Modify: `src/computes/Biome.compute:29`
- Modify: `src/components/network/ExternalTextureSender.cs:42-44`
- Modify: `assets/BiomeFieldConfig_Homeostatic.asset`

- [ ] **Step 1: Add the channel constant, name, and default settings**

In `BiomeFieldConfig.cs`, change the channel block (lines 18-28):
```csharp
        public const int FlowX      = 8;
        public const int FlowY      = 9;
        public const int Count      = 10;
```
to:
```csharp
        public const int FlowX      = 8;
        public const int FlowY      = 9;
        public const int Dispersal  = 10;  // transient agitation: scatters all sims
        public const int Count      = 11;
```
and the `Names` array (lines 24-28):
```csharp
        public static readonly string[] Names =
        {
            "Nutrient", "Pheromone_0", "Pheromone_1", "Pheromone_2", "Oxygen",
            "Temperature", "Waste", "Permeability", "Flow_X", "Flow_Y",
        };
```
to:
```csharp
        public static readonly string[] Names =
        {
            "Nutrient", "Pheromone_0", "Pheromone_1", "Pheromone_2", "Oxygen",
            "Temperature", "Waste", "Permeability", "Flow_X", "Flow_Y", "Dispersal",
        };
```

- [ ] **Step 2: Add the default channel settings entry**

In the `channels` initializer, after the `Flow_Y` line (line 69), add:
```csharp
            new() { name = "Dispersal",      diffuseRate = 0.9f,   decayRate = 0.12f,  advectedByFlow = false, initialValue = 0f,   relaxRate = 0f },
```
(High `decayRate` = "rapid dispersal": pulses fade fast. Some diffusion gives a smooth gradient to flee down.)

- [ ] **Step 3: Bump `CH_COUNT` in the shader**

In `Biome.compute`, line 29:
```hlsl
#define CH_COUNT        10
```
→
```hlsl
#define CH_COUNT        11
```

- [ ] **Step 4: Keep the Syphon channel-name list in sync**

In `ExternalTextureSender.cs`, lines 42-44:
```csharp
    private static readonly string[] ChannelNames = {
        "Nutrient", "Pheromone_0", "Pheromone_1", "Pheromone_2", "Oxygen",
        "Temperature", "Waste", "Permeability", "Flow_X", "Flow_Y" };
```
→
```csharp
    private static readonly string[] ChannelNames = {
        "Nutrient", "Pheromone_0", "Pheromone_1", "Pheromone_2", "Oxygen",
        "Temperature", "Waste", "Permeability", "Flow_X", "Flow_Y", "Dispersal" };
```

- [ ] **Step 5: Add the channel to the serialized config asset**

The scene uses `assets/BiomeFieldConfig_Homeostatic.asset`, whose serialized `channels` list (10 entries) overrides the C# defaults. Open it in the Inspector: set the `channels` list size 10→11, and on the new element set `name=Dispersal`, `diffuseRate=0.9`, `decayRate=0.12`, `advectedByFlow=false`, `initialValue=0`, `relaxRate=0`.

(If editing YAML directly, append a matching list entry after `Flow_Y` with those fields and the same indentation.)

- [ ] **Step 6: Verify compile + no array mismatch**

Console clean (C#), `Biome.compute` no shader errors. Enter Play briefly: no "channel count" or texture-array index errors logged. The field array now allocates 11 layers automatically (`Biome.cs:117` uses `BiomeChannel.Count`).

- [ ] **Step 7: Commit**

```bash
git add "Assets/Workspace/11.0 Biomes/src/components/core/BiomeFieldConfig.cs" "Assets/Workspace/11.0 Biomes/src/computes/Biome.compute" "Assets/Workspace/11.0 Biomes/src/components/network/ExternalTextureSender.cs" "Assets/Workspace/11.0 Biomes/assets/BiomeFieldConfig_Homeostatic.asset"
git commit -m "feat(biome): add Dispersal channel (index 10), fast-decay agitation field"
```

---

### Task 5: Route Dispersal into perception (Umwelt edits)

Make all three sims flee dispersal via the existing `ReadFieldKernel` (negative-weight chemotaxis lowers perception R where dispersal is high). Strip termites to dispersal-flee + permeability-speed only (so they stay ballistic).

**Files:**
- Modify: `assets/UmweltTermite.asset`
- Modify: `assets/UmweltBoid.asset`
- Modify: `assets/UmweltPhysarum.asset`

Channel/effect encoding (from `UmweltMapping.cs`): `effect` 0=Chemotaxis, 1=SpeedPenalty, 2=Avoidance. Dispersal channel index = 10.

- [ ] **Step 1: Termite — APPEND a dispersal-flee read (revised)**

> **Revised by the F1 change:** termites kept their `SensorTurns` chemotaxis (for
> curvy behavior), so do NOT strip their reads. Keep all existing reads and just
> append the dispersal read — same as Boids/Physarum.

In `UmweltTermite.asset` `reads`, append:
```yaml
  - channel: 10
    weight: -1
    effect: 0
```
Leave the other reads and all `writes` unchanged.

- [ ] **Step 2: Boid — add a dispersal-flee read**

In `UmweltBoid.asset` `reads`, append:
```yaml
  - channel: 10
    weight: -1
    effect: 0
```

- [ ] **Step 3: Physarum — add a dispersal-flee read**

In `UmweltPhysarum.asset` `reads`, append:
```yaml
  - channel: 10
    weight: -1
    effect: 0
```

- [ ] **Step 4: Play — baseline unchanged**

With no dispersal injected yet, perception R sits at neutral 0.5 where dispersal=0, so all three sims behave exactly as before. Confirm nothing regressed.

- [ ] **Step 5: Commit**

```bash
git add "Assets/Workspace/11.0 Biomes/assets/UmweltTermite.asset" "Assets/Workspace/11.0 Biomes/assets/UmweltBoid.asset" "Assets/Workspace/11.0 Biomes/assets/UmweltPhysarum.asset"
git commit -m "feat(umwelt): all three sims flee Dispersal via negative-chemotaxis read"
```

---

### Task 6: ~~Termite perception-gradient flee~~ — DROPPED by the F1 revision

> **Obsolete.** Termites kept their `SensorTurns` (which already samples
> `perceptionTex.r` at its 3 sensors), so the dispersal-flee read added to
> `UmweltTermite` in Task 5 makes them flee through the SAME path as Boids and
> Physarum. No dedicated gradient-flee term, no `dispersalResponse` uniform, no
> shader change. Skip this task entirely.
>
> Smoke test (still worth doing, after Task 7): inject a manual Dispersal `Source`
> at center; the curvy streams should bend away from it and re-form once it decays.

---

### Task 7: Firing-driven dispersal injection

Append per-firing-neuron stamps into the Dispersal channel through the existing `InjectSources` pipeline. Strength scales with firing intensity `f` from a small baseline; radius expands as `f` fades (tracks the shockwave ring in F3).

**Files:**
- Modify: `src/components/network/BiomeInjector.cs`

- [ ] **Step 1: Add the firing-dispersal config + source ref**

In `BiomeInjector.cs`, after the `sources` list field (near the top of the class), add:
```csharp
        [Header("Firing-driven dispersal")]
        [Tooltip("Neuron firing source; each firing neuron injects a Dispersal pulse at its location.")]
        public NeuronFiringSource firingSource;
        public bool firingDispersalEnabled = true;
        [BiomeChannelField] public int dispersalChannel = BiomeChannel.Dispersal;
        [Tooltip("Match the sims' spawnScale so pulses land on agent clusters.")]
        public Vector2 firingSpawnScale = new Vector2(0.8f, 0.9f);
        [Range(0.001f, 0.5f)] public float dispersalRadius = 0.05f;
        [Tooltip("Radius grows by this fraction as the firing intensity fades.")]
        [Range(0f, 4f)] public float dispersalExpandGain = 1.5f;
        [Range(0.25f, 6f)] public float dispersalFalloff = 1.5f;
        [Tooltip("Min stamp amount for a barely-firing neuron.")]
        [Range(0f, 1f)] public float dispersalBaseline = 0.05f;
        [Tooltip("Max stamp amount for a full-intensity firing neuron.")]
        [Range(0f, 1f)] public float dispersalAmount = 0.6f;
        [Range(0f, 1f)] public float dispersalFireThreshold = 0.1f;
```

- [ ] **Step 2: Append firing stamps inside `Inject`**

In `Inject(Biome biome)`, the method currently counts manual sources into `n`, fills `_scratch[0..k)`, then uploads. Replace the upload tail (lines 181-188):
```csharp
        if (_buffer == null || _buffer.count < k)
        {
            _buffer?.Release();
            _buffer = new ComputeBuffer(Mathf.Max(k, 4), StampStride);
        }
        _buffer.SetData(_scratch, 0, 0, k);
        biome.InjectSources(_buffer, k);
    }
```
with:
```csharp
        // Firing-driven dispersal pulses: one stamp per firing neuron, strength scaled
        // by firing intensity, radius expanding as it fades (tracks the shockwave ring).
        if (firingDispersalEnabled && firingSource != null)
        {
            var scaled = firingSource.ScaledValues;
            var posCPU = firingSource.PositionsCPU;
            int cap = (scaled != null && posCPU != null) ? Mathf.Min(scaled.Length, posCPU.Count) : 0;
            for (int i = 0; i < cap; i++)
            {
                float f = scaled[i];
                if (f < dispersalFireThreshold) continue;
                float fc = Mathf.Clamp01(f);
                Vector2 np = posCPU[i];
                // Match agent placement: normalized * spawnScale, centered.
                Vector2 uv = new Vector2(
                    np.x * firingSpawnScale.x + (1f - firingSpawnScale.x) * 0.5f,
                    np.y * firingSpawnScale.y + (1f - firingSpawnScale.y) * 0.5f);

                if (k >= _scratch.Length) GrowScratch(k + 1);
                _scratch[k++] = new Stamp
                {
                    uv = uv,
                    radius = dispersalRadius * (1f + dispersalExpandGain * (1f - fc)),
                    falloff = dispersalFalloff,
                    channel = Mathf.Clamp(dispersalChannel, 0, BiomeChannel.Count - 1),
                    amount = dispersalBaseline + (dispersalAmount - dispersalBaseline) * fc,
                    mode = (int)BlendMode.MaxToward,
                    pad = 0f,
                };
            }
        }

        if (k == 0) return;
        if (_buffer == null || _buffer.count < k)
        {
            _buffer?.Release();
            _buffer = new ComputeBuffer(Mathf.Max(k, 4), StampStride);
        }
        _buffer.SetData(_scratch, 0, 0, k);
        biome.InjectSources(_buffer, k);
    }

    // Grow the stamp scratch array, preserving existing entries.
    private void GrowScratch(int needed)
    {
        int newLen = Mathf.Max(needed, _scratch != null ? _scratch.Length * 2 : 8);
        var grown = new Stamp[newLen];
        if (_scratch != null) System.Array.Copy(_scratch, grown, _scratch.Length);
        _scratch = grown;
    }
```

Note: the early `if (n == 0) return;` guard at the top of `Inject` (line ~151) must NOT short-circuit when there are firing stamps but no manual sources. Change that guard from `if (n == 0) return;` to:
```csharp
        if (n == 0 && !(firingDispersalEnabled && firingSource != null)) return;
        if (_scratch == null || _scratch.Length < Mathf.Max(n, 1)) _scratch = new Stamp[Mathf.Max(n, 8)];
```
(Replacing the original `if (n == 0) return;` and the original `if (_scratch == null || _scratch.Length < n) _scratch = new Stamp[n];` lines.)

- [ ] **Step 3: Assign the firing source in the scene**

Select the `BiomeInjector` in `Scene_CURRENTS.unity`; set `firingSource` to the `NeuronFiringSource` component. Set `firingSpawnScale` equal to the sims' `spawnScale` (0.8, 0.9).

- [ ] **Step 4: Verify compile**

Console clean.

- [ ] **Step 5: Play — firing scatters agents**

Enter Play and drive neuron firing (the scene's usual `/index` OSC or test control). Expected: when a neuron fires, agents near its location blast radially outward; termite streams bow out then re-form as the pulse decays. **Alignment check:** the scatter origin should coincide with where that neuron's agents cluster. If the scatter is offset (e.g. vertically mirrored), flip Y on `uv.y` (`uv.y = 1f - uv.y`) — `PositionsCPU` is y-flipped for the composite overlay and the biome field may use the opposite convention.

- [ ] **Step 6: Commit**

```bash
git add "Assets/Workspace/11.0 Biomes/src/components/network/BiomeInjector.cs" "Assets/Workspace/11.0 Biomes/scene/Scene_CURRENTS.unity"
git commit -m "feat(injector): firing-driven Dispersal pulses (intensity-scaled, expanding)"
```

---

### Task 8: Global OSC dispersal source (documentation step)

The global OSC path needs no code — it's a normal `BiomeInjector.Source` targeting the Dispersal channel.

**Files:** none (scene/inspector only)

- [ ] **Step 1: Add an OSC dispersal source**

In the scene's `BiomeInjector`, add a `Source`: `name = "dispersal"`, `channel = Dispersal (10)`, `mode = Additive`, `radius = 0.4` (wide), `falloff = 1`, `gain = 0.1`, `fieldUV (0.5,0.5)`. Drive it via OSC `/inject/dispersal <0..1>` (or its `oscAddress` override).

- [ ] **Step 2: Play — OSC floods agitation**

Send the OSC value; the whole field agitates and sims scatter broadly. Confirm, then set its `value` back to 0.

- [ ] **Step 3: Commit (if scene changed)**

```bash
git add "Assets/Workspace/11.0 Biomes/scene/Scene_CURRENTS.unity"
git commit -m "chore(scene): add global OSC dispersal injector source"
```

---

## Phase F3 — Neuron firing as expanding shockwave + core flash

### Task 9: Expanding-radius shockwave ring + core flash

**Files:**
- Modify: `src/computes/SimulationManager.compute` (`NeuronRingKernel`, lines 87-111, + 2 uniforms)
- Modify: `src/components/core/SimulationManager.cs` (2 fields + 2 property IDs + 2 SetFloat)

- [ ] **Step 1: Add the two uniforms to the shader**

In `SimulationManager.compute`, after `float ringStrength;` (line 43) add:
```hlsl
float  ringExpandGain;    // radius grows by this fraction as firing fades
float  ringCoreStrength;  // bright center flash at onset
```

- [ ] **Step 2: Rewrite the ring accumulation**

In `NeuronRingKernel`, replace the loop body (lines 96-106):
```hlsl
    for (int k = 0; k < ringCount; k++) {
        float f = ringFiring[k];
        if (f < ringThreshold) continue;
        // Same transform the sims use to place agents: normalized * spawnScale, centered.
        float2 np = ringPositions[k] * ringSpawnScale + (1.0 - ringSpawnScale) * 0.5;
        float2 npix = np * rez;
        float d = distance(px, npix);
        float r = ringRadius * (0.6 + 0.4 * saturate(f));      // radius pulses with intensity
        float band = exp(-((d - r) * (d - r)) / (2.0 * t * t)); // gaussian ring
        add += ringColor.rgb * band * saturate(f) * ringStrength;
    }
```
with:
```hlsl
    for (int k = 0; k < ringCount; k++) {
        float f = ringFiring[k];
        if (f < ringThreshold) continue;
        float fs = saturate(f);
        // Same transform the sims use to place agents: normalized * spawnScale, centered.
        float2 np = ringPositions[k] * ringSpawnScale + (1.0 - ringSpawnScale) * 0.5;
        float2 npix = np * rez;
        float d = distance(px, npix);
        // Expanding shockwave: radius grows as the firing intensity fades (1 - fs).
        float r = ringRadius * (1.0 + ringExpandGain * (1.0 - fs));
        float band = exp(-((d - r) * (d - r)) / (2.0 * t * t));  // gaussian ring
        add += ringColor.rgb * band * fs * ringStrength;
        // Bright core flash at onset (strong only while fs is high).
        float core = exp(-(d * d) / (2.0 * t * t));
        add += ringColor.rgb * core * fs * fs * ringCoreStrength;
    }
```

- [ ] **Step 3: Add C# fields + property IDs + binds**

In `SimulationManager.cs`, near the other `m_Ring*` fields add:
```csharp
        [Range(0f, 6f)]  public float m_RingExpandGain = 2f;
        [Range(0f, 4f)]  public float m_RingCoreStrength = 1.5f;
```
Near the other `s_Ring*` `Shader.PropertyToID` definitions add:
```csharp
        static readonly int s_RingExpandGainID = Shader.PropertyToID("ringExpandGain");
        static readonly int s_RingCoreStrengthID = Shader.PropertyToID("ringCoreStrength");
```
In the ring-dispatch block (after `compositeCS.SetFloat(s_RingStrengthID, m_RingStrength);`) add:
```csharp
            compositeCS.SetFloat(s_RingExpandGainID, m_RingExpandGain);
            compositeCS.SetFloat(s_RingCoreStrengthID, m_RingCoreStrength);
```

- [ ] **Step 4: Verify compile**

`SimulationManager.compute` no shader errors; Console clean.

- [ ] **Step 5: Play — observe shockwaves**

Drive firing. Expected: each firing neuron emits a bright core flash that expands into an outward-traveling ring as it fades, spatially coincident with the agent scatter from Task 7. Tune `m_RingExpandGain` / `m_RingCoreStrength` / `m_RingRadius` live.

- [ ] **Step 6: Commit**

```bash
git add "Assets/Workspace/11.0 Biomes/src/computes/SimulationManager.compute" "Assets/Workspace/11.0 Biomes/src/components/core/SimulationManager.cs"
git commit -m "feat(firing): expanding shockwave ring + core flash overlay"
```

---

## Final integration pass

### Task 10: End-to-end review + docs

**Files:**
- Modify: `README.md`, `docs/ARCHITECTURE.md` (per project conventions)

- [ ] **Step 1: Full Play-mode review**

Confirm together: ballistic termite streams (F1) with visible trails (F2); firing produces coincident shockwave ring + radial agent scatter (F3+F4); termite streams re-form after a pulse; OSC global dispersal scatters broadly; no Console errors; FPS acceptable.

- [ ] **Step 2: Update README + ARCHITECTURE**

Add a short section: termite ballistic-stream model (divergence from reference), the Dispersal channel (index 10, firing- and OSC-driven), and the shockwave overlay. Note humidity and ARGBHalf packing are deferred; `externalInfluenceTex` keyword gate deferred.

- [ ] **Step 3: Commit**

```bash
git add README.md docs/ARCHITECTURE.md
git commit -m "docs: termite ballistic streams, Dispersal channel, firing shockwaves"
```

- [ ] **Step 4: Push the branch**

```bash
git push -u origin feat/termite-streams-dispersal
```

---

## Notes / deferred (out of scope)
- Humidity channel — deferred.
- ARGBHalf trail packing — shelved (~2-3% FPS, not worth the 11.0-wide churn).
- `externalInfluenceTex` `multi_compile` keyword gate — deferred to its own pass.
- Speed burst uses existing `firingSpeedMul` (firing agents); a dispersal-magnitude speed boost for non-firing agents in a pulse is a possible later refinement (SpeedPenalty effect can only attenuate, so it would need a new effect type).
