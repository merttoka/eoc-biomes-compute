# Termite Simulation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a `TermiteSim` (one `.cs` + one `.compute`) to the 11.0 Biomes pipeline — a neuron-coupled pheromone-stigmergy swarm that builds permeability mounds via the Biome, matching the Physarum/Boids pattern.

**Architecture:** `TermiteSim : SimulationBase`. Reuses inherited trail-array / perception / dispatch / Umwelt plumbing. The compute shader is Physarum's 6-kernel set minus "eat", plus an optional per-agent `firing` buffer (CPU-uploaded each frame from `normalized_neuron_data.csv`, agent `i` → neuron `i % 131`) that doubles speed and lays bright "firing" trails. Mounds accrete in `CH_PERMEABILITY` purely through the existing `Biome.WriteFieldKernel` + `UmweltMapping` — no new GPU code.

**Tech Stack:** Unity (HDRP/compute), HLSL compute shaders, C# MonoBehaviour/ScriptableObject. No automated test runner — verification is **Unity Console (clean compile)** + **Play-mode visual observation**.

---

## Verification model (read first)

There is no `pytest`/`dotnet test` harness in this project. Every task is verified by:
1. **Compile check** — Unity auto-recompiles on focus. Open **Window ▸ General ▸ Console**, clear it, refocus Unity (or `Assets ▸ Refresh` / `Ctrl+R`). Expect **zero red errors**. Shader errors appear when the `.compute` is selected in the Project window (Inspector shows compile status) or on first dispatch.
2. **Play-mode observation** — enter Play, look at the assigned `outputMat` / debug grid for the described visual.

Commits must use **explicit file paths** (never `git add -A`) so in-progress external-texture work on this branch is not swept in.

---

## File structure

| File | Responsibility |
|---|---|
| `Assets/Workspace/11.0 Biomes/src/params/TermiteParams.cs` | Per-type params + MIDI/OSC ranges (`ScriptableObject, IParamSet`) |
| `Assets/Workspace/11.0 Biomes/src/computes/includes/termite_type_params.hlsl` | GPU `TermiteTypeParams` struct + buffer decl (must byte-match C#) |
| `Assets/Workspace/11.0 Biomes/src/computes/TermiteSim.compute` | 6 kernels: ResetTexture/ResetAgents/MoveAgents/WriteTrails/DiffuseTexture/Render |
| `Assets/Workspace/11.0 Biomes/src/components/Sim/TermiteSim.cs` | Component: buffers, lifecycle, param control, CSV position init, firing upload |
| `Assets/Workspace/11.0 Biomes/data/labels_positions.csv` | Imported init-position data (TextAsset, 4 KB) |
| `tools/firing_csv_to_f16.py` | Offline preprocessor: 729 MB CSV → compact float16 binary |
| `Assets/StreamingAssets/biomes11/termite_firing.f16` | Preprocessed firing series (~47 MB, float16, via Git LFS) |

> **StreamingAssets location:** must be `Assets/StreamingAssets/...` at the **project
> root** — that's the only path `Application.streamingAssetsPath` resolves to. A
> `StreamingAssets` folder nested under `11.0 Biomes/` is NOT recognized. Namespaced as
> `biomes11/` since the folder is shared across workspaces.

> **Why preprocess (critical):** the source `normalized_neuron_data.csv` is **729 MB**
> (180,001 rows × 393 cols). Loading it as a Unity `TextAsset` would hold a 729 MB
> managed string + bake it into builds, and parsing ~70 M `float.TryParse` on the main
> thread in `Reset()` would freeze for seconds-to-minutes. Only the 131 `z` columns are
> used. We preprocess once into a flat float16 series (~47 MB) loaded from
> `StreamingAssets` via `File.ReadAllBytes` — no per-frame parsing, small memory, and
> live-tunable `firingThreshold` (float16 retained, not pre-thresholded).
>
> **Future:** factor the loader as a reusable `Float16Series` so Physarum/Boid can adopt
> the same binary-asset path for any large external series (out of scope here).

Editor-created assets (Task 7): a `TermiteParams` preset + an `UmweltMapping` asset.

---

## Task 0: Branch setup

**Files:** none (git only)

- [ ] **Step 1: Create a feature branch when ready to execute**

> The other in-progress work lives on `feat/external-texture-share`. Per repo convention (main = production-ready, experiments on branches), branch off `main`:

```bash
git fetch origin
git switch -c feat/termite-sim origin/main
```

If the external-texture work must merge first, wait for it, then branch off the updated `main`. Do **not** execute the rest of this plan on `feat/external-texture-share`.

- [ ] **Step 2: Confirm clean start**

Run: `git status`
Expected: on `feat/termite-sim`, working tree clean.

---

## Task 0.5: Preprocess firing CSV → float16 binary

**Files:**
- Create: `tools/firing_csv_to_f16.py`
- Produces: `Assets/Workspace/11.0 Biomes/StreamingAssets/termite_firing.f16`

Extracts the 131 `z` columns (`col n*3 + 2`) from the 729 MB CSV and writes a compact
flat float16 binary with a tiny header. Streamed line-by-line — never loads the whole
file into memory.

- [ ] **Step 1: Write the preprocessor (use a venv)**

```bash
python3 -m venv /tmp/firing-venv && source /tmp/firing-venv/bin/activate
pip install numpy
```

Create `tools/firing_csv_to_f16.py`:

```python
#!/usr/bin/env python3
"""Convert normalized_neuron_data.csv (frames x 3*neurons) to a compact float16 blob.

Output layout (little-endian):
  magic  : 4 bytes  b"TFR1"
  uint32 : neuronCount        (z-columns = ncols // 3)
  uint32 : frameCount
  data   : frameCount * neuronCount  float16   (row-major: frame, then neuron)

Only the z column of each (x,y,z) triple is kept: z = col[n*3 + 2].
"""
import sys, struct
import numpy as np

def main(src, dst):
    neuron_count = None
    frames = 0
    with open(src, "r") as f, open(dst, "wb") as out:
        out.write(b"TFR1")
        out.write(struct.pack("<II", 0, 0))  # placeholder header, rewritten at end
        for line in f:
            line = line.strip()
            if not line:
                continue
            parts = line.split(",")
            # header / malformed rows: any z fails float() -> skip
            try:
                zs = [float(parts[i * 3 + 2]) for i in range(len(parts) // 3)]
            except (ValueError, IndexError):
                continue
            if neuron_count is None:
                neuron_count = len(zs)
            elif len(zs) != neuron_count:
                continue  # ragged row, skip
            np.asarray(zs, dtype=np.float16).tofile(out)
            frames += 1
        out.seek(4)
        out.write(struct.pack("<II", neuron_count or 0, frames))
    print(f"wrote {dst}: neuronCount={neuron_count}, frames={frames}")

if __name__ == "__main__":
    if len(sys.argv) != 3:
        sys.exit("usage: firing_csv_to_f16.py <src.csv> <dst.f16>")
    main(sys.argv[1], sys.argv[2])
```

- [ ] **Step 2: Run it**

```bash
mkdir -p "Assets/StreamingAssets/biomes11"
python tools/firing_csv_to_f16.py \
  /Users/toka/Developer/Graphics/PDE_Nefeli_Termites/data/normalized_neuron_data.csv \
  "Assets/StreamingAssets/biomes11/termite_firing.f16"
```

Expected output: `wrote ...: neuronCount=131, frames=180000` (±1 for the header row).
Resulting file ≈ 180000 × 131 × 2 bytes ≈ **47 MB**.

- [ ] **Step 3: Track the binary via Git LFS (47 MB)**

```bash
git lfs install
git lfs track "*.f16"
git add .gitattributes "Assets/StreamingAssets/biomes11/termite_firing.f16"
git add tools/firing_csv_to_f16.py
git commit -m "feat(termite): preprocess firing CSV to float16 blob (LFS)"
```

> If Git LFS is unavailable, alternative: gitignore `*.f16`, commit only the script, and
> regenerate the blob per machine from the source CSV. The 729 MB source CSV is **never**
> committed either way.

---

## Task 1: TermiteParams ScriptableObject

**Files:**
- Create: `Assets/Workspace/11.0 Biomes/src/params/TermiteParams.cs`

- [ ] **Step 1: Write `TermiteParams.cs`**

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Biomes
{
    [Serializable]
    public class TermiteAgentType
    {
        public float senseAngle = 45f;        // degrees (→ radians on upload)
        public float senseDistance = 20f;
        public float turnAngle = 15f;         // degrees (→ radians on upload)
        public float moveSpeed = 0.5f;
        public float firingSpeedMul = 2f;
        public float depositAmount = 0.3f;
        public float firingDepositAmount = 3f; // > 1 → rendered toward white
        public float depositProbability = 0.2f;
        public float firingDepositProbability = 0.3f;
        public float diffuseRate = 0.97f;
        public float hue = 0.6f;              // blue-ish default
        public float saturation = 0.7f;
    }

    [CreateAssetMenu(fileName = "TermiteParams", menuName = "Biomes/TermiteParams")]
    public class TermiteParams : ScriptableObject, IParamSet
    {
        [Range(1, 8)] public int typeCount = 1;
        public List<TermiteAgentType> types = new() { new TermiteAgentType() };

        [Header("MIDI/OSC Ranges (min/max for 0-1 mapping)")]
        public List<ParamRange> ranges = new()
        {
            new("moveSpeed",                0.05f, 10f),
            new("senseAngle",               0.1f,  180f),
            new("turnAngle",                0.1f,  180f),
            new("senseDistance",            0.1f,  200f),
            new("depositAmount",            0.01f, 1f),
            new("firingDepositAmount",      1f,    4f),
            new("depositProbability",       0f,    1f),
            new("firingDepositProbability", 0f,    1f),
            new("firingSpeedMul",           1f,    5f),
            new("diffuseRate",              0.9f,  1f),
            new("hue",                      0f,    1f),
            new("saturation",               0f,    1f),
        };

        public (float min, float max) GetRange(string paramName)
            => ParamRangeUtil.GetRange(ranges, paramName);

        public int TypeCount => types.Count;

        public float GetValue(string name, int typeIndex)
        {
            if (typeIndex < 0 || typeIndex >= types.Count) return 0f;
            var t = types[typeIndex];
            return name switch
            {
                "moveSpeed"                => t.moveSpeed,
                "senseAngle"               => t.senseAngle,
                "turnAngle"                => t.turnAngle,
                "senseDistance"            => t.senseDistance,
                "depositAmount"            => t.depositAmount,
                "firingDepositAmount"      => t.firingDepositAmount,
                "depositProbability"       => t.depositProbability,
                "firingDepositProbability" => t.firingDepositProbability,
                "firingSpeedMul"           => t.firingSpeedMul,
                "diffuseRate"              => t.diffuseRate,
                "hue"                      => t.hue,
                "saturation"               => t.saturation,
                _ => 0f,
            };
        }

        public void SetValue(string name, int typeIndex, float raw)
        {
            if (typeIndex < 0 || typeIndex >= types.Count) return;
            var t = types[typeIndex];
            switch (name)
            {
                case "moveSpeed":                t.moveSpeed = raw; break;
                case "senseAngle":               t.senseAngle = raw; break;
                case "turnAngle":                t.turnAngle = raw; break;
                case "senseDistance":            t.senseDistance = raw; break;
                case "depositAmount":            t.depositAmount = raw; break;
                case "firingDepositAmount":      t.firingDepositAmount = raw; break;
                case "depositProbability":       t.depositProbability = raw; break;
                case "firingDepositProbability": t.firingDepositProbability = raw; break;
                case "firingSpeedMul":           t.firingSpeedMul = raw; break;
                case "diffuseRate":              t.diffuseRate = raw; break;
                case "hue":                      t.hue = raw; break;
                case "saturation":               t.saturation = raw; break;
            }
        }

        public void SyncTypesList()
        {
            while (types.Count < typeCount) types.Add(new TermiteAgentType());
            while (types.Count > typeCount) types.RemoveAt(types.Count - 1);
        }

        public void ResetToDefaults()
        {
            typeCount = 1;
            types.Clear();
            types.Add(new TermiteAgentType());
        }

        public void RandomizeParams()
        {
            foreach (var t in types)
            {
                var r = GetRange("senseAngle");    t.senseAngle    = UnityEngine.Random.Range(r.min, r.max);
                r = GetRange("senseDistance");     t.senseDistance = UnityEngine.Random.Range(r.min, r.max);
                r = GetRange("turnAngle");         t.turnAngle     = UnityEngine.Random.Range(r.min, r.max);
                r = GetRange("moveSpeed");         t.moveSpeed     = UnityEngine.Random.Range(r.min, r.max);
            }
        }

        public void RandomizeColors()
        {
            var palette = ColorPalette.GenerateHS(types.Count);
            for (int i = 0; i < types.Count && i < palette.Count; i++)
            {
                types[i].hue = palette[i].hue;
                types[i].saturation = palette[i].saturation;
            }
        }
    }
}
```

- [ ] **Step 2: Compile check**

In Unity: clear Console, refocus to recompile.
Expected: no errors. `Assets ▸ Create ▸ Biomes ▸ TermiteParams` menu item now exists (don't create the asset yet).

> If `IParamSet` requires members not implemented, open `src/params/IParamSet.cs` and match its signature exactly (mirror what `PhysarumParams` implements).

- [ ] **Step 3: Commit**

```bash
git add "Assets/Workspace/11.0 Biomes/src/params/TermiteParams.cs"
git commit -m "feat(termite): add TermiteParams scriptable object"
```

---

## Task 2: GPU type-params include

**Files:**
- Create: `Assets/Workspace/11.0 Biomes/src/computes/includes/termite_type_params.hlsl`

The field order MUST exactly match `TermiteTypeParamsGPU` in Task 4 (`LayoutKind.Sequential`).

- [ ] **Step 1: Write `termite_type_params.hlsl`**

```hlsl
struct TermiteTypeParams {
    float senseAngle;
    float senseDistance;
    float turnAngle;
    float moveSpeed;
    float firingSpeedMul;
    float depositAmount;
    float firingDepositAmount;
    float depositProbability;
    float firingDepositProbability;
    float diffuseRate;
    float hue;
    float saturation;
};  // 48 bytes (12 floats)
StructuredBuffer<TermiteTypeParams> typeParams;
uint typeCount;
```

- [ ] **Step 2: Commit**

```bash
git add "Assets/Workspace/11.0 Biomes/src/computes/includes/termite_type_params.hlsl"
git commit -m "feat(termite): add termite_type_params hlsl include"
```

---

## Task 3: Compute shader

**Files:**
- Create: `Assets/Workspace/11.0 Biomes/src/computes/TermiteSim.compute`

- [ ] **Step 1: Write `TermiteSim.compute`**

```hlsl
// Termite stigmergy simulation: pheromone coordination + optional neuron firing.
// Mirrors PhysarumSim.compute; adds per-agent firing, drops the "eat" step.

#pragma kernel ResetTextureKernel
#pragma kernel ResetAgentsKernel
#pragma kernel MoveAgentsKernel
#pragma kernel WriteTrailsKernel
#pragma kernel DiffuseTextureKernel
#pragma kernel RenderKernel

#define TRAIL_MAX 4.0   // firing trails exceed 1.0; clamp ceiling for diffuse/firing

// Trail texture array: layers 0..typeCount-1 = per-type, layer typeCount = total
SamplerState sampler_trailRead;
Texture2DArray<float> trailRead;
RWTexture2DArray<float> trailWrite;
RWTexture2D<float4> outTex;

// Biome perception: R=chemotaxis, G=speed multiplier, B=avoidance
Texture2D<float4> perceptionTex;
SamplerState sampler_perceptionTex;

struct Agent {
    float2 position;
    float2 direction;
    uint typeId;
};
uint agentsCount;
StructuredBuffer<Agent> agentsIn;
RWStructuredBuffer<Agent> agentsOut;

// Optional firing state (per agent, 0/1). firingEnabled gates it.
StructuredBuffer<uint> firing;
uint firingEnabled;

// Optional external initialization positions (e.g., neuron coordinates from CSV)
StructuredBuffer<float2> neuronPositions;
uint neuronCount;
float2 neuronScale;

uint rezX;
uint rezY;
uint time;

#include "includes/random.hlsl"
#include "includes/termite_type_params.hlsl"

// ════════════════════════════════════════════════
// RESET
// ════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void ResetTextureKernel(uint3 id : SV_DISPATCHTHREADID) {
    if (id.x >= rezX || id.y >= rezY) return;
    for (uint t = 0; t <= typeCount; t++) {
        trailWrite[uint3(id.xy, t)] = 0;
    }
}

[numthreads(1024, 1, 1)]
void ResetAgentsKernel(uint3 id : SV_DISPATCHTHREADID) {
    if (id.x >= agentsCount) return;

    Agent a;
    if (neuronCount > 0) {
        uint idx = id.x % neuronCount;
        float2 p = neuronPositions[idx] * neuronScale + float2(rezX * (1.0 - neuronScale.x) * 0.5, rezY * (1.0 - neuronScale.y) * 0.5);
        p.x = clamp(p.x, 0.0, (float)rezX - 1.0);
        p.y = clamp(p.y, 0.0, (float)rezY - 1.0);
        a.position = p;
    } else {
        float2 c = Random2(id.x * .0001 + time * .001);
        a.position = float2(c.x * (float)rezX, c.y * (float)rezY);
    }

    a.direction = RandomDirection2(id.xy * .001 + sin(time));
    a.typeId = (id.x * typeCount) / agentsCount;
    agentsOut[id.x] = a;
}

// ════════════════════════════════════════════════
// SENSOR + MOVEMENT (with biome perception)
// ════════════════════════════════════════════════
float2 RotateVectorBy(float2 vec, float angle) {
    float x = vec.x * cos(angle) - vec.y * sin(angle);
    float y = vec.x * sin(angle) + vec.y * cos(angle);
    return float2(x, y);
}

float2 SensorTurns(uint3 id, Agent a, TermiteTypeParams p) {
    float2 direction = normalize(a.direction);
    float r = p.senseDistance;
    float ang = p.senseAngle;
    float tang = p.turnAngle;

    float2 leftSensor = RotateVectorBy(direction, -ang) * r;
    float2 middleSensor = direction * r;
    float2 rightSensor = RotateVectorBy(direction, ang) * r;

    float2 leftCoord = a.position + leftSensor;
    float2 middleCoord = a.position + middleSensor;
    float2 rightCoord = a.position + rightSensor;

    float3 leftUV = float3(leftCoord.x / (float)rezX, leftCoord.y / (float)rezY, 0);
    float3 middleUV = float3(middleCoord.x / (float)rezX, middleCoord.y / (float)rezY, 0);
    float3 rightUV = float3(rightCoord.x / (float)rezX, rightCoord.y / (float)rezY, 0);

    // Own-type trail (intra-species coordination)
    float myLeft = trailRead.SampleLevel(sampler_trailRead, float3(leftUV.xy, a.typeId), 0);
    float totLeft = trailRead.SampleLevel(sampler_trailRead, float3(leftUV.xy, typeCount), 0);
    float leftLevel = myLeft - (totLeft - myLeft);

    float myMiddle = trailRead.SampleLevel(sampler_trailRead, float3(middleUV.xy, a.typeId), 0);
    float totMiddle = trailRead.SampleLevel(sampler_trailRead, float3(middleUV.xy, typeCount), 0);
    float middleLevel = myMiddle - (totMiddle - myMiddle);

    float myRight = trailRead.SampleLevel(sampler_trailRead, float3(rightUV.xy, a.typeId), 0);
    float totRight = trailRead.SampleLevel(sampler_trailRead, float3(rightUV.xy, typeCount), 0);
    float rightLevel = myRight - (totRight - myRight);

    // Biome perception R = chemotaxis (0.5 neutral). Mound avoidance enters here
    // via a negative-weight read on CH_PERMEABILITY in the UmweltMapping.
    float biomeLeft = perceptionTex.SampleLevel(sampler_perceptionTex, leftUV.xy, 0).r;
    float biomeMiddle = perceptionTex.SampleLevel(sampler_perceptionTex, middleUV.xy, 0).r;
    float biomeRight = perceptionTex.SampleLevel(sampler_perceptionTex, rightUV.xy, 0).r;
    leftLevel   += (biomeLeft - 0.5) * 2.0;
    middleLevel += (biomeMiddle - 0.5) * 2.0;
    rightLevel  += (biomeRight - 0.5) * 2.0;

    float2 d = direction;
    if (middleLevel > leftLevel && middleLevel > rightLevel) {
        d = middleSensor;
    } else if (middleLevel < leftLevel && middleLevel < rightLevel) {
        int sign = Random2(id.xy * .01 + sin(time) * .01).x <= 0.5 ? 1 : -1;
        d = RotateVectorBy(d, sign * tang);
    } else if (leftLevel < rightLevel) {
        d = RotateVectorBy(d, tang);
    } else if (leftLevel > rightLevel) {
        d = RotateVectorBy(d, -tang);
    } else {
        d = middleSensor;
    }
    return normalize(d);
}

[numthreads(1024, 1, 1)]
void MoveAgentsKernel(uint3 id : SV_DISPATCHTHREADID) {
    if (id.x >= agentsCount) return;

    Agent a = agentsIn[id.x];
    TermiteTypeParams p = typeParams[a.typeId];

    float2 direction = SensorTurns(id, a, p);

    // Organic wiggle (~±0.05 rad), matching the original sketch
    float w = (Random1(float2(id.x, time) * 0.07 + 3.0) - 0.5) * 0.1;
    direction = RotateVectorBy(direction, w);

    // Biome speed multiplier (permeability) + firing boost
    float2 uv = a.position / float2((float)rezX, (float)rezY);
    float speedMult = perceptionTex.SampleLevel(sampler_perceptionTex, uv, 0).g;
    float fireMul = (firingEnabled != 0 && firing[id.x] != 0) ? p.firingSpeedMul : 1.0;
    float effectiveSpeed = p.moveSpeed * speedMult * fireMul;

    a.direction = direction * effectiveSpeed;
    a.position += a.direction;

    // Toroidal wrapping
    float fRezX = (float)rezX;
    float fRezY = (float)rezY;
    if (a.position.x < 0) a.position.x += fRezX;
    if (a.position.x >= fRezX) a.position.x -= fRezX;
    if (a.position.y < 0) a.position.y += fRezY;
    if (a.position.y >= fRezY) a.position.y -= fRezY;

    agentsOut[id.x] = a;
}

// ════════════════════════════════════════════════
// TRAILS
// ════════════════════════════════════════════════
[numthreads(1024, 1, 1)]
void WriteTrailsKernel(uint3 id : SV_DISPATCHTHREADID) {
    if (id.x >= agentsCount) return;

    Agent a = agentsOut[id.x];
    TermiteTypeParams p = typeParams[a.typeId];
    uint2 pos = uint2(round(a.position));

    bool isFiring = (firingEnabled != 0) && (firing[id.x] != 0);
    float rnd = Random1(float2(id.x, time) * 0.013 + 7.0);

    if (isFiring) {
        if (rnd < p.firingDepositProbability) {
            float v = trailWrite[uint3(pos, a.typeId)];
            trailWrite[uint3(pos, a.typeId)] = clamp(max(v, p.firingDepositAmount), 0, TRAIL_MAX);
        }
    } else {
        if (rnd < p.depositProbability) {
            float own = trailWrite[uint3(pos, a.typeId)];
            trailWrite[uint3(pos, a.typeId)] = clamp(own + p.depositAmount, 0, 1.0);
        }
    }
}

[numthreads(8, 8, 1)]
void DiffuseTextureKernel(uint3 id : SV_DISPATCHTHREADID) {
    if (id.x >= rezX || id.y >= rezY) return;

    float total = 0;
    for (uint t = 0; t < typeCount; t++) {
        float avg = 0;
        for (int dx = -1; dx <= 1; dx++) {
            for (int dy = -1; dy <= 1; dy++) {
                float3 coord = float3((id.x + dx) / (float)rezX, (id.y + dy) / (float)rezY, t);
                avg += trailRead.SampleLevel(sampler_trailRead, coord, 0);
            }
        }
        avg /= 9.0;
        float diffused = clamp(avg * typeParams[t].diffuseRate, 0, TRAIL_MAX);
        trailWrite[uint3(id.xy, t)] = diffused;
        total += diffused;
    }
    trailWrite[uint3(id.xy, typeCount)] = total;
}

// ════════════════════════════════════════════════
// RENDER
// ════════════════════════════════════════════════
#include "includes/color.hlsl"

[numthreads(8, 8, 1)]
void RenderKernel(uint3 id : SV_DISPATCHTHREADID) {
    if (id.x >= rezX || id.y >= rezY) return;

    float4 color = 0;
    for (uint t = 0; t < typeCount; t++) {
        float val = trailRead[uint3(id.xy, t)];
        TermiteTypeParams p = typeParams[t];
        float baseB = saturate(val);
        float white = saturate(val - 1.0);   // firing (>1) pushes toward white
        float4 c = hsb2rgb(float3(p.hue, p.saturation * (1.0 - white), 0.8 * baseB), baseB);
        c.rgb = lerp(c.rgb, float3(1, 1, 1), white);
        color += c / typeCount;
    }
    float4 current = outTex[id.xy];
    current += color;
    current *= 0.9;
    outTex[id.xy] = saturate(current);
}
```

- [ ] **Step 2: Shader compile check**

In Unity Project window, select `TermiteSim.compute`. In the Inspector, confirm it lists the 6 kernels with **no compile errors** (red text). Clear the Console; expect no shader import errors.

> Common failure: include path. The `#include "includes/..."` is relative to the `.compute` file — `termite_type_params.hlsl` and `random.hlsl`/`color.hlsl` must sit in `computes/includes/` (they do; Task 2 placed the new one there).

- [ ] **Step 3: Commit**

```bash
git add "Assets/Workspace/11.0 Biomes/src/computes/TermiteSim.compute"
git commit -m "feat(termite): add TermiteSim compute shader"
```

---

## Task 4: TermiteSim component

**Files:**
- Create: `Assets/Workspace/11.0 Biomes/src/components/Sim/TermiteSim.cs`

- [ ] **Step 1: Write `TermiteSim.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using UnityEngine;
using EasyButtons;

namespace Biomes
{
    public class TermiteSim : SimulationBase
    {
        public override string SimName => "Termite";

        private static readonly IReadOnlyList<string> s_ModulatableParams = new[]
            { "moveSpeed", "senseAngle", "turnAngle", "senseDistance",
              "depositAmount", "diffuseRate", "hue", "saturation" };
        public override IReadOnlyList<string> ModulatableParams => s_ModulatableParams;

        [Header("Agents")]
        [Range(1024, 4000000)] public int agentsCount = 131 * 100; // 13100
        private ComputeBuffer readAgentsBuffer;
        private ComputeBuffer writeAgentsBuffer;

        [Header("Parameters (assign preset, runtime clone appears on Play)")]
        public TermiteParams paramsSO;
        [Header("Runtime Parameters (live tweaking)")]
        public TermiteParams agentParams;
        public override IParamSet LiveParamSet => agentParams;

        [Header("Init Positions CSV (like Physarum)")]
        public TextAsset labelsPositionsCsv;
        public bool csvCoordinatesAreNormalized = false;
        [Tooltip("How much of the canvas agents fill (0-1). (1,1)=full canvas")]
        public Vector2 spawnScale = new Vector2(0.8f, 0.9f);
        private ComputeBuffer neuronPositionsBuffer;
        private ComputeBuffer dummyNeuronBuffer;

        [Header("Firing (optional, float16 blob in StreamingAssets)")]
        public bool enableFiring = false;
        [Tooltip("Path under Assets/StreamingAssets, produced by tools/firing_csv_to_f16.py")]
        public string firingBlobFile = "biomes11/termite_firing.f16";
        [Range(0f, 1f)] public float firingThreshold = 0.1f;
        public bool loopFiring = true;
        private ushort[] _firingHalf;               // flat float16 bits: frame*_neuronZCount + neuron
        private int _frameCount;
        private int _neuronZCount;
        private int _currentFrame;
        private float[] _frameScratch;              // decoded current-frame z values
        private uint[] _firingScratch;              // agentsCount, uploaded per step
        private ComputeBuffer firingBuffer;
        private ComputeBuffer dummyFiringBuffer;

        private static readonly int s_NeuronPositionsID = Shader.PropertyToID("neuronPositions");
        private static readonly int s_NeuronCountID = Shader.PropertyToID("neuronCount");
        private static readonly int s_NeuronScaleID = Shader.PropertyToID("neuronScale");
        private static readonly int s_FiringID = Shader.PropertyToID("firing");
        private static readonly int s_FiringEnabledID = Shader.PropertyToID("firingEnabled");

        protected override int TypeCount => agentParams != null ? agentParams.types.Count : 1;

        private ComputeBuffer typeParamsBuffer;
        private TermiteTypeParamsGPU[] _typeParamsCache;

        #region GPU struct
        [StructLayout(LayoutKind.Sequential)]
        struct TermiteTypeParamsGPU
        {
            public float senseAngle, senseDistance, turnAngle, moveSpeed;
            public float firingSpeedMul;
            public float depositAmount, firingDepositAmount;
            public float depositProbability, firingDepositProbability;
            public float diffuseRate, hue, saturation;
        }
        #endregion

        public override ComputeBuffer GetAgentPositionBuffer() => readAgentsBuffer;
        public override int GetAgentCount() => agentsCount;

        public override void Reset()
        {
            agentParams = paramsSO != null
                ? Instantiate(paramsSO)
                : ScriptableObject.CreateInstance<TermiteParams>();
            LoadFiringBlob();
            base.Reset();
        }

        protected override void InitBuffers()
        {
            // Agent: float2 position + float2 direction + uint typeId = 20 bytes
            readAgentsBuffer = gpu.CreateBuffer(agentsCount, sizeof(float) * 4 + sizeof(uint));
            writeAgentsBuffer = gpu.CreateBuffer(agentsCount, sizeof(float) * 4 + sizeof(uint));
            typeParamsBuffer = gpu.CreateBuffer(8, Marshal.SizeOf<TermiteTypeParamsGPU>());

            dummyNeuronBuffer = gpu.CreateBuffer(1, sizeof(float) * 2);
            dummyNeuronBuffer.SetData(new Vector2[1] { Vector2.zero });

            dummyFiringBuffer = gpu.CreateBuffer(1, sizeof(uint));
            dummyFiringBuffer.SetData(new uint[1] { 0u });

            bool firingActive = enableFiring && _firingHalf != null && _frameCount > 0;
            if (firingActive)
            {
                firingBuffer = gpu.CreateBuffer(agentsCount, sizeof(uint));
                _firingScratch = new uint[agentsCount];
            }
        }

        protected override void GPUReset()
        {
            cs.SetInt(s_RezXID, rezX);
            cs.SetInt(s_RezYID, rezY);
            cs.SetInt(s_TimeID, Time.frameCount);
            UploadTypeParams();
            ResetTrailArrays();

            cs.SetInt(s_AgentsCountID, agentsCount);
            cs.SetBuffer(resetAgentsKernel, s_AgentsOutID, writeAgentsBuffer);

            // Init positions from CSV (like Physarum) or random scatter
            int neuronCount = 0;
            if (labelsPositionsCsv != null && !string.IsNullOrEmpty(labelsPositionsCsv.text))
            {
                var positions = ParseCsvFloat2(labelsPositionsCsv.text);
                if (csvCoordinatesAreNormalized || LooksNormalized01(positions))
                {
                    for (int i = 0; i < positions.Count; i++)
                    {
                        var p = positions[i];
                        p.x *= rezX; p.y *= rezY;
                        positions[i] = p;
                    }
                }
                neuronCount = positions.Count;
                if (neuronCount > 0)
                {
                    neuronPositionsBuffer = gpu.CreateBuffer(neuronCount, sizeof(float) * 2);
                    neuronPositionsBuffer.SetData(positions);
                    cs.SetBuffer(resetAgentsKernel, s_NeuronPositionsID, neuronPositionsBuffer);
                }
                else cs.SetBuffer(resetAgentsKernel, s_NeuronPositionsID, dummyNeuronBuffer);
            }
            else cs.SetBuffer(resetAgentsKernel, s_NeuronPositionsID, dummyNeuronBuffer);

            cs.SetInt(s_NeuronCountID, neuronCount);
            cs.SetVector(s_NeuronScaleID, new Vector4(spawnScale.x, spawnScale.y, 0, 0));

            Dispatch(resetAgentsKernel, agentsCount, 1, 1);
            (readAgentsBuffer, writeAgentsBuffer) = (writeAgentsBuffer, readAgentsBuffer);

            _currentFrame = 0;
        }

        private void UploadTypeParams()
        {
            int count = agentParams.types.Count;
            if (_typeParamsCache == null || _typeParamsCache.Length != count)
                _typeParamsCache = new TermiteTypeParamsGPU[count];
            for (int i = 0; i < count; i++)
            {
                var t = agentParams.types[i];
                _typeParamsCache[i] = new TermiteTypeParamsGPU
                {
                    senseAngle = t.senseAngle * Mathf.Deg2Rad,
                    senseDistance = t.senseDistance,
                    turnAngle = t.turnAngle * Mathf.Deg2Rad,
                    moveSpeed = t.moveSpeed,
                    firingSpeedMul = t.firingSpeedMul,
                    depositAmount = t.depositAmount,
                    firingDepositAmount = t.firingDepositAmount,
                    depositProbability = t.depositProbability,
                    firingDepositProbability = t.firingDepositProbability,
                    diffuseRate = t.diffuseRate,
                    hue = t.hue,
                    saturation = t.saturation,
                };
            }
            typeParamsBuffer.SetData(_typeParamsCache);
            cs.SetInt(s_TypeCountID, count);

            int[] kernels = { moveAgentsKernel, writeTrailsKernel, diffuseTextureKernel, renderKernel };
            foreach (int k in kernels)
                cs.SetBuffer(k, s_TypeParamsID, typeParamsBuffer);
        }

        private void UploadFiring()
        {
            bool firingActive = enableFiring && firingBuffer != null
                                && _firingHalf != null && _frameCount > 0 && _neuronZCount > 0;

            if (!firingActive)
            {
                cs.SetInt(s_FiringEnabledID, 0);
                cs.SetBuffer(moveAgentsKernel, s_FiringID, dummyFiringBuffer);
                cs.SetBuffer(writeTrailsKernel, s_FiringID, dummyFiringBuffer);
                return;
            }

            // Decode the current frame's float16 z-values, then threshold per agent.
            int baseIdx = _currentFrame * _neuronZCount;
            for (int n = 0; n < _neuronZCount; n++)
                _frameScratch[n] = Mathf.HalfToFloat(_firingHalf[baseIdx + n]);
            for (int i = 0; i < agentsCount; i++)
                _firingScratch[i] = _frameScratch[i % _neuronZCount] >= firingThreshold ? 1u : 0u;
            firingBuffer.SetData(_firingScratch);

            cs.SetInt(s_FiringEnabledID, 1);
            cs.SetBuffer(moveAgentsKernel, s_FiringID, firingBuffer);
            cs.SetBuffer(writeTrailsKernel, s_FiringID, firingBuffer);

            _currentFrame++;
            if (_currentFrame >= _frameCount)
                _currentFrame = loopFiring ? 0 : _frameCount - 1;
        }

        protected override void GPUStep()
        {
            UploadTypeParams();
            BindPerceptionTex(moveAgentsKernel);
            UploadFiring();

            cs.SetInt(s_AgentsCountID, agentsCount);
            cs.SetTexture(moveAgentsKernel, s_TrailReadID, trailReadArray);
            cs.SetBuffer(moveAgentsKernel, s_AgentsInID, readAgentsBuffer);
            cs.SetBuffer(moveAgentsKernel, s_AgentsOutID, writeAgentsBuffer);
            Dispatch(moveAgentsKernel, agentsCount, 1, 1);

            cs.SetTexture(diffuseTextureKernel, s_TrailReadID, trailReadArray);
            cs.SetTexture(diffuseTextureKernel, s_TrailWriteID, trailWriteArray);
            Dispatch(diffuseTextureKernel, rezX, rezY, 1);

            cs.SetInt(s_AgentsCountID, agentsCount);
            cs.SetBuffer(writeTrailsKernel, s_AgentsOutID, writeAgentsBuffer);
            cs.SetTexture(writeTrailsKernel, s_TrailWriteID, trailWriteArray);
            Dispatch(writeTrailsKernel, agentsCount, 1, 1);

            (readAgentsBuffer, writeAgentsBuffer) = (writeAgentsBuffer, readAgentsBuffer);
        }

        protected override void Render()
        {
            cs.SetTexture(renderKernel, s_TrailReadID, trailReadArray);
            cs.SetTexture(renderKernel, s_OutTexID, outTex);
            Dispatch(renderKernel, rezX, rezY, 1);
            if (outputMat != null)
                outputMat.SetTexture("_UnlitColorMap", outTex);
        }

        #region Parameter Control
        private float R(string p, float v) { var (mn, mx) = agentParams.GetRange(p); return MapAndClamp(v, mn, mx); }
        private float D(string p, float f, float d) { var (mn, mx) = agentParams.GetRange(p); return ClampDelta(f, d, mn, mx); }

        public override void SetParameter(string paramName, int index, float value)
        {
            if (index < 0 || index >= agentParams.types.Count) return;
            var t = agentParams.types[index];
            switch (paramName)
            {
                case "moveSpeed":     t.moveSpeed     = R(paramName, value); break;
                case "senseAngle":    t.senseAngle    = R(paramName, value); break;
                case "turnAngle":     t.turnAngle     = R(paramName, value); break;
                case "senseDistance": t.senseDistance = R(paramName, value); break;
                case "depositAmount": t.depositAmount = R(paramName, value); break;
                case "diffuseRate":   t.diffuseRate   = R(paramName, value); break;
                case "hue":           t.hue           = R(paramName, value); break;
                case "saturation":    t.saturation    = R(paramName, value); break;
            }
        }

        public override void SetParameterDelta(string paramName, int index, float delta)
        {
            if (index < 0 || index >= agentParams.types.Count) return;
            var t = agentParams.types[index];
            switch (paramName)
            {
                case "moveSpeed":     t.moveSpeed     = D(paramName, t.moveSpeed, delta); break;
                case "senseAngle":    t.senseAngle    = D(paramName, t.senseAngle, delta); break;
                case "turnAngle":     t.turnAngle     = D(paramName, t.turnAngle, delta); break;
                case "senseDistance": t.senseDistance = D(paramName, t.senseDistance, delta); break;
                case "depositAmount": t.depositAmount = D(paramName, t.depositAmount, delta); break;
                case "diffuseRate":   t.diffuseRate   = D(paramName, t.diffuseRate, delta); break;
                case "hue":           t.hue           = D(paramName, t.hue, delta); break;
                case "saturation":    t.saturation    = D(paramName, t.saturation, delta); break;
            }
        }

        public override float GetParameter(string paramName, int index)
        {
            if (index < 0 || index >= agentParams.types.Count) return 0f;
            var t = agentParams.types[index];
            return paramName switch
            {
                "moveSpeed"     => t.moveSpeed,
                "senseAngle"    => t.senseAngle,
                "turnAngle"     => t.turnAngle,
                "senseDistance" => t.senseDistance,
                "depositAmount" => t.depositAmount,
                "diffuseRate"   => t.diffuseRate,
                "hue"           => t.hue,
                "saturation"    => t.saturation,
                _ => 0f,
            };
        }
        #endregion

        [Button] public void RandomizeParams() => agentParams?.RandomizeParams();
        [Button] public void RandomizeColors() => agentParams?.RandomizeColors();

        #region CSV / blob parsing
        // Loads the float16 firing blob written by tools/firing_csv_to_f16.py.
        // Layout: "TFR1" magic, uint32 neuronCount, uint32 frameCount, then
        // frameCount*neuronCount float16 (row-major frame→neuron). ~47 MB, read once.
        private void LoadFiringBlob()
        {
            _firingHalf = null; _frameCount = 0; _neuronZCount = 0; _frameScratch = null;
            if (!enableFiring || string.IsNullOrEmpty(firingBlobFile)) return;

            string path = System.IO.Path.Combine(Application.streamingAssetsPath, firingBlobFile);
            if (!System.IO.File.Exists(path))
            {
                Debug.LogWarning($"TermiteSim: firing blob not found at {path} (run tools/firing_csv_to_f16.py)");
                return;
            }

            using var br = new System.IO.BinaryReader(System.IO.File.OpenRead(path));
            var magic = br.ReadBytes(4);
            if (magic.Length < 4 || magic[0] != (byte)'T' || magic[1] != (byte)'F'
                || magic[2] != (byte)'R' || magic[3] != (byte)'1')
            {
                Debug.LogWarning("TermiteSim: firing blob has bad magic; ignoring");
                return;
            }
            _neuronZCount = (int)br.ReadUInt32();
            _frameCount   = (int)br.ReadUInt32();
            long count = (long)_frameCount * _neuronZCount;
            if (count <= 0 || count > int.MaxValue / 2)
            {
                Debug.LogWarning($"TermiteSim: firing blob size out of range ({_frameCount}x{_neuronZCount})");
                _frameCount = 0; _neuronZCount = 0;
                return;
            }
            var bytes = br.ReadBytes((int)(count * 2));
            _firingHalf = new ushort[count];
            System.Buffer.BlockCopy(bytes, 0, _firingHalf, 0, bytes.Length);
            _frameScratch = new float[_neuronZCount];
        }

        private static List<Vector2> ParseCsvFloat2(string csv)
        {
            var list = new List<Vector2>();
            var lines = csv.Split('\n');
            var inv = CultureInfo.InvariantCulture;
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;
                var parts = line.Split(',');
                if (parts.Length < 3) continue;
                if (float.TryParse(parts[1], NumberStyles.Float, inv, out float x) &&
                    float.TryParse(parts[2], NumberStyles.Float, inv, out float y))
                    list.Add(new Vector2(x, (1 - y)));
            }
            return list;
        }

        private static bool LooksNormalized01(List<Vector2> points)
        {
            if (points == null || points.Count == 0) return false;
            float maxX = float.MinValue, maxY = float.MinValue;
            float minX = float.MaxValue, minY = float.MaxValue;
            int sampleCount = Mathf.Min(points.Count, 2048);
            for (int i = 0; i < sampleCount; i++)
            {
                var p = points[i];
                if (float.IsNaN(p.x) || float.IsNaN(p.y)) continue;
                maxX = Mathf.Max(maxX, p.x); maxY = Mathf.Max(maxY, p.y);
                minX = Mathf.Min(minX, p.x); minY = Mathf.Min(minY, p.y);
            }
            return (minX >= -0.01f && maxX <= 1.01f && minY >= -0.01f && maxY <= 1.01f);
        }
        #endregion
    }
}
```

- [ ] **Step 2: Compile check**

In Unity: clear Console, refocus to recompile.
Expected: no errors. The struct field order in `TermiteTypeParamsGPU` matches `termite_type_params.hlsl` exactly (12 floats, same order) — confirm by eye.

- [ ] **Step 3: Commit**

```bash
git add "Assets/Workspace/11.0 Biomes/src/components/Sim/TermiteSim.cs"
git commit -m "feat(termite): add TermiteSim component"
```

---

## Task 5: Import positions CSV

**Files:**
- Create: `Assets/Workspace/11.0 Biomes/data/labels_positions.csv`

> Firing data is **not** imported here — it's the StreamingAssets float16 blob from
> Task 0.5. Only the tiny (4 KB) positions CSV is a TextAsset. The 729 MB source CSV is
> never copied into `Assets/` (it would become a baked TextAsset + bloat builds).

- [ ] **Step 1: Copy the positions CSV from the Processing project**

```bash
mkdir -p "Assets/Workspace/11.0 Biomes/data"
cp /Users/toka/Developer/Graphics/PDE_Nefeli_Termites/data/labels_positions.csv \
   "Assets/Workspace/11.0 Biomes/data/labels_positions.csv"
```

- [ ] **Step 2: Verify Unity imports it as a TextAsset**

Refocus Unity. Select `11.0 Biomes/data/labels_positions.csv`.
Expected: Inspector shows it as a **TextAsset** (132 rows: header + 131 neurons).

- [ ] **Step 3: Commit**

```bash
git add "Assets/Workspace/11.0 Biomes/data/labels_positions.csv" \
        "Assets/Workspace/11.0 Biomes/data/labels_positions.csv.meta"
git commit -m "feat(termite): import neuron positions CSV"
```

> Run after Unity has generated the `.meta`. Add the `data/` folder's `.meta` too if
> Unity created one.

---

## Task 6: Create assets + scene wiring + first run

**Files:** none (Unity editor assets + scene; `.asset`/`.unity` changes)

- [ ] **Step 1: Create the TermiteParams preset**

`Assets ▸ Create ▸ Biomes ▸ TermiteParams`, name it `TermiteParams_Default`, place under `11.0 Biomes/params/` (or wherever Physarum's preset lives). Leave defaults (1 type, blue hue).

- [ ] **Step 2: Create the Termite UmweltMapping**

`Assets ▸ Create ▸ Biomes ▸ UmweltMapping`, name it `Umwelt_Termite`. Configure:
- **Writes:** add one entry → `channel = 6` (CH_PERMEABILITY), `amount = 0.01` (mound accretion). Optionally add `channel = 1` (CH_PHEROMONE0), `amount = 0.01` so other sims sense termites.
- **Reads:** add one entry → `channel = 6` (CH_PERMEABILITY), `effect = Chemotaxis`, `weight = -0.6` (avoid own mounds → wall-following). Optionally add a second read on `channel = 6`, `effect = SpeedPenalty`, `weight = 1` (slow on dense mounds).

> Channel indices come from the biome channel enum (NUTRIENT 0, PHEROMONE0 1, PHEROMONE1 2, OXYGEN 3, TEMPERATURE 4, WASTE 5, PERMEABILITY 6, FLOW_X 7, FLOW_Y 8). Confirm against `BiomeFieldConfig`/`Biome.cs` if unsure.

- [ ] **Step 3: Add the TermiteSim component to the scene**

In `TestScene.unity`, add a GameObject `TermiteSim` (or reuse the sim hierarchy). Add the `TermiteSim` component. Assign:
- `cs` = `TermiteSim.compute`
- `outputMat` = a display material (copy how Physarum's is set up)
- `paramsSO` = `TermiteParams_Default`
- `umwelt` = `Umwelt_Termite`
- `labelsPositionsCsv` = `labels_positions.csv`
- `agentsCount` = 13100 (default)
- Leave `enableFiring` **off** for the first run.

- [ ] **Step 4: Register with SimulationManager**

Select the `SimulationManager` GameObject. Add the `TermiteSim` to its `simulations` list.

- [ ] **Step 5: First Play-mode run (no firing)**

Enter Play mode.
Expected: termites spawn from the neuron layout, wander, and form Physarum-like pheromone trails in blue; over time the biome's permeability channel accumulates faint mounds along trafficked paths (check the debug grid's permeability channel). No console errors.

> If output is black: confirm `outputMat` uses `_UnlitColorMap` (the property `Render()` sets) and is shown somewhere in the scene/debug grid.
> If everything clumps to one spot or NaNs: check `spawnScale` and that `senseDistance`/`moveSpeed` are sane.

- [ ] **Step 6: Enable firing**

Stop Play. Ensure Task 0.5 produced `StreamingAssets/termite_firing.f16`. On `TermiteSim`: set `enableFiring = true`, `firingBlobFile = "termite_firing.f16"` (default), `firingThreshold = 0.1`, `loopFiring = true`. Enter Play.
Expected: a subset of termites (those whose neuron `i % 131` is firing this frame) move ~2× faster and lay bright **white** dotted trails; the firing set changes over time as the blob advances one frame per sim-step. No multi-second hitch on entering Play (blob loads in well under a second vs. the old CSV parse).

- [ ] **Step 7: Commit scene + assets**

```bash
git add "Assets/Workspace/11.0 Biomes/TestScene.unity" \
        "Assets/Workspace/11.0 Biomes/params/TermiteParams_Default.asset" \
        "Assets/Workspace/11.0 Biomes/params/TermiteParams_Default.asset.meta" \
        "Assets/Workspace/11.0 Biomes/params/Umwelt_Termite.asset" \
        "Assets/Workspace/11.0 Biomes/params/Umwelt_Termite.asset.meta"
git commit -m "feat(termite): wire TermiteSim into scene with params + umwelt assets"
```

> Adjust the `.asset` paths to wherever you actually saved them. Stage only termite-related files — leave the in-progress external-texture changes alone.

---

## Task 7: Tune, verify integration, document

**Files:**
- Modify: `README.md`
- Modify: `docs/ARCHITECTURE.md` (only when merging to main, per repo convention)

- [ ] **Step 1: Verify coexistence with other sims**

With Physarum/Boids also enabled in `SimulationManager`, enter Play. Confirm all run together, the debug grid shows termite output as its own channel/cell, and external input (if wired) influences termites via `perceptionTex` (move over a bright external region → behavior shifts). No errors, acceptable framerate.

- [ ] **Step 2: Tune mound emergence**

Iterate on `Umwelt_Termite`: increase the permeability **write** amount and the negative **read** weight until mounds form visible ridges/walls rather than a flat smear. Adjust `diffuseRate` (lower = sharper trails) and `depositProbability` to taste.

- [ ] **Step 3: Update README**

Add a Termite entry to the simulations section of `README.md` (mirror the Physarum/Boids description): what it is (neuron-coupled stigmergy + permeability mound-building), the CSV inputs, and the key inspector fields (`enableFiring`, `firingCsv`, `labelsPositionsCsv`, `agentsCount`).

- [ ] **Step 4: Commit docs**

```bash
git add README.md
git commit -m "docs: document termite simulation"
```

- [ ] **Step 5: Finish the branch**

Use the `superpowers:finishing-a-development-branch` skill to decide merge/PR. On merge to `main`, update `docs/ARCHITECTURE.md` (repo convention) and use the `eoc-docs` skill to log the session/ADR.

---

## Self-review

**Spec coverage:**
- Optional toggleable firing → Tasks 4 (`enableFiring`, `UploadFiring`, modulo `i % _neuronZCount`), 6 (Step 6). ✓
- Full Umwelt (read perception, write permeability) → kernel consumes `perceptionTex.R/.G` (Task 3); `Umwelt_Termite` writes CH_PERMEABILITY + negative read (Task 6). ✓
- Mound "coordinates + accrete" → private trail coordination (Task 3 SensorTurns) + biome permeability accretion via Umwelt (Task 6). ✓
- Firing source, modulo-131 → Task 0.5 preprocesses z (col `n*3+2`) → float16 blob; Task 4 `LoadFiringBlob` + `UploadFiring` (`i % _neuronZCount`). ✓
- Firing dataset viable (729 MB CSV not loaded at runtime) → Task 0.5 float16 blob (~47 MB) in StreamingAssets, read once. ✓
- Init like Physarum (CSV or random) → Task 3 ResetAgentsKernel + Task 4 GPUReset position parse. ✓
- Default count 131*100 → Task 4 (`agentsCount = 131 * 100`). ✓
- One type, multi-type capable → `TermiteParams.typeCount` + per-type trail layers/render loop. ✓
- One `.cs` + one `.compute` → Tasks 3, 4. ✓
- Works with other features / no manager change → Task 6 Step 4 (add to list), Task 7 Step 1. ✓

**Placeholder scan:** none — every code step has complete code; verification steps are concrete editor checks. ✓

**Type consistency:** `TermiteTypeParamsGPU` (C#, Task 4) ↔ `TermiteTypeParams` (HLSL, Task 2) — identical 12-float order. Shader uniforms `firing`/`firingEnabled`/`neuronPositions`/`neuronCount`/`neuronScale` ↔ C# property IDs in Task 4. Kernel names ↔ `SimulationBase.FindKernel` calls. ✓

---

## Unresolved questions

✅ No unresolved questions.
