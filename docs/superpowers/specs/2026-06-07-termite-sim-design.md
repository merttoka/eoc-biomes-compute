# Termite Simulation — Design Spec

**Date:** 2026-06-07
**Workspace:** `Assets/Workspace/11.0 Biomes`
**Goal:** Port the Processing termite sim (`PDE_Nefeli_Termites`) into the 11.0 Biomes
Unity compute pipeline as a first-class `SimulationBase`, matching the
Physarum/Boids pattern (one `.cs` component + one `.compute` shader), and
integrating with the shared Biome, external input, and MIDI/OSC modulation.

---

## 1. Source behavior (what we're porting)

The Processing sketch is **not** the classic wood-chip automaton. It is a
**pheromone-stigmergy swarm** coupled to **neuron firing data**:

- Each termite senses a single pheromone field in 3 directions (forward, ±45°),
  turns toward the strongest (±15° steps + small random wiggle), moves, and
  deposits pheromone. Structurally near-identical to the existing Physarum kernel.
- 131 termites mirror 131 neurons. Per-frame CSV "firing" data (`z` value ≥ 0.1)
  makes a termite move 2× faster and lay bright dotted high-intensity trails
  (~10.0 vs the normal additive 0.3 capped at 1.0).
- Slow decay (`*0.9998`/frame), no spatial diffusion in the original; rendered as
  a blue→cyan→white heatmap with firing trails appearing white.

### Design decisions (from brainstorming)

| Decision | Choice |
|---|---|
| Neuron firing | **Optional/toggleable**, CSV-faithful. Agent count free; agent `i` → neuron `i % 131`. |
| Biome integration | **Full Umwelt** — read perception texture, write deposits to a biome channel. |
| Mound mechanic | **"Coordinates + accrete"** — private pheromone trail drives coordination (original look); permeability accretes mounds underneath via Umwelt. |
| Firing source | **CSV** (`normalized_neuron_data.csv`), modulo-131 agent→neuron mapping. |
| Init positions | Like Physarum — from `labels_positions.csv` if assigned, else random scatter. |
| Default agent count | `131 * 100 = 13100`. |
| Types | One type now; multi-type capable like Physarum/Boids. |

---

## 2. Files to create

| File | Purpose |
|---|---|
| `src/components/Sim/TermiteSim.cs` | `MonoBehaviour : SimulationBase` component |
| `src/computes/TermiteSim.compute` | 6-kernel compute shader |
| `src/computes/includes/termite_type_params.hlsl` | GPU type-params struct + buffer decl |
| `src/params/TermiteParams.cs` | `ScriptableObject, IParamSet` (per-type params + ranges) |
| `data/labels_positions.csv` (imported, 4 KB) | Init positions (copied from termite `/data`) |
| `tools/firing_csv_to_f16.py` | Offline: 729 MB firing CSV → float16 blob |
| `Assets/StreamingAssets/biomes11/termite_firing.f16` (~47 MB, LFS) | Preprocessed firing series (loaded at runtime) |

Editor-created assets (not code):
- A `TermiteParams` preset asset (one type, sensible defaults).
- An `UmweltMapping` asset for termites (reads/writes below).

`SimulationManager` needs **no code change** — it iterates `List<SimulationBase>`
generically. Termite is wired into the scene's sim list via the inspector.

---

## 3. Component: `TermiteSim.cs`

Mirrors `PhysarumSim.cs` structure exactly.

```csharp
public class TermiteSim : SimulationBase
{
    public override string SimName => "Termite";

    [Header("Agents")]
    [Range(1024, 4000000)] public int agentsCount = 131 * 100; // 13100

    [Header("Parameters")]
    public TermiteParams paramsSO;     // preset
    public TermiteParams agentParams;  // runtime clone
    public override IParamSet LiveParamSet => agentParams;

    [Header("Init Positions CSV (like Physarum)")]
    public TextAsset labelsPositionsCsv;        // labels_positions.csv
    public bool csvCoordinatesAreNormalized = false;
    public Vector2 spawnScale = new(0.8f, 0.9f);

    [Header("Firing (optional, CSV-driven)")]
    public bool enableFiring = false;
    public TextAsset firingCsv;                 // normalized_neuron_data.csv
    public int neuronCount = 131;
    [Range(0f,1f)] public float firingThreshold = 0.1f;
    public bool loopFiring = true;

    public UmweltMapping umwelt;                // assigned in inspector
}
```

### GPU type-params struct (12 floats, `LayoutKind.Sequential`)

```csharp
[StructLayout(LayoutKind.Sequential)]
struct TermiteTypeParamsGPU
{
    public float senseAngle, senseDistance, turnAngle, moveSpeed; // angles → radians on upload
    public float firingSpeedMul;
    public float depositAmount, firingDepositAmount;
    public float depositProbability, firingDepositProbability;
    public float diffuseRate;
    public float hue, saturation;
}
```

### Buffers (allocated in `InitBuffers`)
- `readAgentsBuffer` / `writeAgentsBuffer` — `Agent { float2 position; float2 direction; uint typeId; }` = **20 bytes**, byte-identical to Physarum/Boids → `Biome.WriteFieldKernel` compatible.
- `typeParamsBuffer` — `CreateBuffer(8, sizeof(TermiteTypeParamsGPU))`.
- `firingBuffer` — `StructuredBuffer<uint>` sized `agentsCount`, only when `enableFiring`. A 1-element dummy + `firingEnabled` int flag when disabled (mirrors Physarum's `dummyNeuronBuffer`).
- Init-position buffer parsed from `labelsPositionsCsv` (reuse Physarum's `ParseCsvFloat2`, `LooksNormalized01`); dummy buffer when absent.

### Firing upload (per `GPUStep`, only when enabled)
- Parse `firingCsv` **once** on reset into `float[frames][neuronCount]`, reading column `n*3 + 2` (the `z`) for neuron `n` (393 cols / 3 = 131).
- Each step: advance `currentFrame` (loop or clamp), build `uint[agentsCount]` where `firing[i] = row[i % neuronCount] >= firingThreshold ? 1 : 0`, `SetData` into `firingBuffer`.
- Bind `firingBuffer` + `firingEnabled` to move & writeTrails kernels.

### Lifecycle (inherited pattern)
- `Reset()` → clone `paramsSO`, `base.Reset()`.
- `GPUReset()` → upload type params, reset trail arrays, parse positions, dispatch `ResetAgentsKernel`, swap.
- `GPUStep()` → upload type params, `BindPerceptionTex`, upload firing, dispatch Move → Diffuse → WriteTrails, swap agent buffers.
- `Render()` → dispatch `RenderKernel`, push `outTex` to `outputMat`.

### Parameter control
`ModulatableParams = { moveSpeed, senseAngle, turnAngle, senseDistance, depositAmount, diffuseRate, hue, saturation }`.
`SetParameter` / `SetParameterDelta` / `GetParameter` switch over those (same shape as Physarum, ranges via `agentParams.GetRange`).

---

## 4. Compute: `TermiteSim.compute`

Same kernel set + numthreads as Physarum.

```hlsl
#pragma kernel ResetTextureKernel   // [8,8,1]    clear trail array
#pragma kernel ResetAgentsKernel    // [1024,1,1] init pos (CSV or random) + random dir
#pragma kernel MoveAgentsKernel     // [1024,1,1] sense+turn+move
#pragma kernel WriteTrailsKernel    // [1024,1,1] deposit to private trail
#pragma kernel DiffuseTextureKernel // [8,8,1]    diffuse + slow decay
#pragma kernel RenderKernel         // [8,8,1]    heatmap composite

#include "includes/random.hlsl"
#include "includes/color.hlsl"
#include "includes/termite_type_params.hlsl"

struct Agent { float2 position; float2 direction; uint typeId; };
StructuredBuffer<uint> firing;   // 0/1 per agent (dummy when disabled)
uint firingEnabled;
```

**MoveAgentsKernel** — read `agentsIn[id]`; sample private trail at 3 sensors
(fwd, ±`senseAngle`, distance `senseDistance`); also sample `perceptionTex`
(chemotaxis R / speed G / avoidance B — biome influence, incl. permeability/mounds
+ external input); decide turn (strongest sensor → keep/±`turnAngle`, else random)
+ `random(-0.05,0.05)` wiggle; `speed = moveSpeed * (firingEnabled && firing[id] ? firingSpeedMul : 1) * perceptionSpeed`; advance position; toroidal wrap; write `agentsOut[id]`.

**WriteTrailsKernel** — at agent position, deposit to its type's private trail
layer + total layer. Normal: `min(trail + depositAmount, 1)` with prob
`depositProbability`. Firing: write `firingDepositAmount` (~10) with prob
`firingDepositProbability` (~0.3) → bright dotted trails. RNG via `random.hlsl`
seeded by `id + time`.

**DiffuseTextureKernel** — 3×3 blur blended by `diffuseRate` + slow multiplicative
decay (configurable; default ≈ Physarum behavior, slower than 0.9998-per-frame-equivalent tuned to framerate).

**RenderKernel** — map trail intensity to the blue→cyan→white ramp via
`color.hlsl`; `v > 1` (firing) → white. Apply type `hue`/`saturation` for
multi-type runs.

`termite_type_params.hlsl` declares the matching `struct TermiteTypeParams { ... }`
(same 12 floats, order-matched) + `StructuredBuffer<TermiteTypeParams> typeParams; uint typeCount;`.

---

## 5. Biome integration (UmweltMapping asset)

| Direction | Channel | Effect | Notes |
|---|---|---|---|
| **Write** | `CH_PERMEABILITY` | deposit (+amount) | Mounds accrete where termites walk. |
| **Write** | `CH_PHEROMONE0` *(optional)* | deposit | Lets other sims sense termite activity. |
| **Read** | `CH_PERMEABILITY` | Avoidance (+SpeedPenalty) | Termites avoid/slow on their own mounds → deposition concentrates at edges → emergent walls/pillars. |
| **Read** | `CH_PERMEABILITY` | habitat min/max *(optional)* | Preference shaping. |

This is the "construction" half. The private trail (in-shader, not biome) is the
"coordination" half. External input flows in automatically: `SimulationManager`
assigns `externalInfluenceTex` and calls `biome.BuildPerceptionTex(...)` for every
sim each frame, so termites react to Spout/NDI/video with no extra wiring.

> v1 keeps mound deposition uniform (Umwelt fixed amount). Scaling mound rate by
> firing/local-pheromone is a noted future enhancement (would need a custom write
> path, since `WriteFieldKernel` is firing-agnostic).

---

## 6. Parameters: `TermiteParams.cs`

`ScriptableObject, IParamSet` with `List<TermiteType> types` and per-field
`ParamRange` bounds + `GetRange(name)`, `RandomizeParams()`, `RandomizeColors()`
— mirroring `PhysarumParams`.

`TermiteType` fields: `senseAngle(=45°), senseDistance(≈20), turnAngle(=15°),
moveSpeed(≈0.5), firingSpeedMul(=2), depositAmount(=0.3), firingDepositAmount(=10),
depositProbability(=0.2), firingDepositProbability(=0.3), diffuseRate, hue, saturation`.

---

## 7. Scene wiring (manual, inspector)

1. Add a `TermiteSim` component to a GameObject under the sim hierarchy.
2. Assign: `paramsSO` (Termite preset), `umwelt` (Termite mapping), `outputMat`,
   `labelsPositionsCsv`, and (optional) `firingCsv` + `enableFiring`.
3. Add the `TermiteSim` to `SimulationManager.simulations`.
4. Confirm it appears in the debug grid / channel labels like the other sims.

---

## 8. Out of scope (v1)

- Firing- or pheromone-scaled mound deposition (uniform for now).
- Spatial-hash neighbor queries (termites are stigmergic, not flocking — not needed).
- Editor tooling beyond what `ParamsEditor` already provides generically.

---

## 9. Risks / GPU porting notes

- **Shared trail writes**: multiple agents may hit the same texel. Match the
  existing Physarum approach (write to ping-ponged write-array; tolerate benign
  races as Physarum does) rather than atomics, for consistency. Revisit only if
  visual artifacts appear.
- **Firing data size**: the source CSV is **729 MB** (180k rows × 393 cols) — far too
  large to import as a Unity `TextAsset` (729 MB managed string + baked into builds +
  ~70 M main-thread `float.TryParse` in `Reset()`). **Resolved** by offline
  preprocessing (`tools/firing_csv_to_f16.py`) into a ~47 MB float16 blob holding only
  the 131 `z` columns, loaded once from `StreamingAssets` via `File.ReadAllBytes`. Per
  frame: decode 131 halves + upload a small `uint[agentsCount]`. See plan Task 0.5.
- **Agent/biome struct lock-in**: do **not** add fields to `Agent` — firing lives
  in its own buffer to preserve the 20-byte layout `WriteFieldKernel` expects.
- **Decay tuning**: original decay is per-frame at Processing's framerate; tune
  `diffuseRate`/decay to the Biomes pipeline so trails persist similarly.

---

## Unresolved questions

✅ No unresolved questions.
