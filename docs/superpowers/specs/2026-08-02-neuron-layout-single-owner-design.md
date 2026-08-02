---
status: draft
date: 2026-08-02
tags: [spec, refactor, neuron, layout, spawnscale, engine-core, defect]
related: [[../../ARCHITECTURE]], [[2026-08-02-shanghai-dac-11-3-design]], [[2026-06-08-osc-neuron-firing-design]], [[../../adr/0006-osc-neuron-firing]]
---

# Neuron Layout Has a Single Owner (Design)

## Goal

`spawnScale` — how the normalized neuron layout maps into the field — is declared independently in
three components and its mapping formula is written out in five places. Collapse it to **one owner
with one function**, so consumers read rather than re-declare.

This is a defect fix, not a cleanup. The hand-maintained invariant **has already failed** in two of
four scenes.

## The defect (audited 2026-08-02)

| Scene | sims `spawnScale` | `m_RingSpawnScale` | `firingSpawnScale` | |
|---|---|---|---|---|
| 11.1 CURRENTS | 0.4, 0.75 | 0.4, 0.75 | 0.4, 0.75 | agree |
| 11.2 SIGGRAPH | **0.5, 0.6** | 0.4, 0.75 | 0.4, 0.75 | **desynced** |
| 11.3 DAC | **0.5, 0.6** | 0.4, 0.75 | 0.4, 0.75 | **desynced** |
| 11.0 TestScene | 0.8, 0.9 | *(default)* | *(default)* | agree |

The sims were retuned `0.4,0.75` → `0.5,0.6`; the two hand-maintained copies never followed.

Mapping is `uv = np · scale + (1 − scale) · 0.5`. At `np = 1.0`: sims place the neuron at
`0.75`, rings at `0.70`. At `np = 0` : `0.25` vs `0.30`. So firing rings and dispersal stamps are
displaced up to **5 % of canvas width and 7.5 % of height** — zero error at centre, worst at the
edges. At the 9472 px DAC master that is **~474 px horizontally**.

CURRENTS agrees only because nobody retuned it. Both stale copies carry the tooltip *"Match the
sims' spawnScale"* — the code documents an invariant it cannot enforce.

## Why one owner, and which one

`spawnScale` describes **the neuron layout**, not any consumer of it. `NeuronFiringSource` already
owns everything else about that layout — `PositionsCPU`, `PositionsBuffer`, `NeuronCount`, and the
CSV the positions are parsed from. The scale belongs with them; it is currently the one property of
the layout that lives outside its owner.

The audit corroborates this: within every scene all three sims share one value. The field is already
*semantically* per-scene; it is only *mechanically* per-component.

Ground truth on migration is the **sims'** value, not the rings' — the sims determine where agents
actually spawn, so they define the layout. The rings and stamps are the things currently wrong.

## Components

### 1 · `NeuronFiringSource` gains the field and the function

```csharp
[Tooltip("How much of the canvas the neuron layout fills (0-1). Single source of truth — " +
         "rings, dispersal stamps and every sim read this.")]
public Vector2 spawnScale = new Vector2(0.8f, 0.9f);

public Vector2 SpawnScale => spawnScale;

/// Normalized neuron position -> normalized field UV.
public Vector2 NeuronToFieldUV(Vector2 np);

/// Normalized neuron position -> field pixel space.
public Vector2 NeuronToFieldPixels(Vector2 np, float rezX, float rezY);
```

Both functions are the single CPU definition. `NeuronToFieldUV` is what `BiomeInjector` needs;
`NeuronToFieldPixels` mirrors what the compute shaders do, and exists so CPU and GPU can be tested
against each other.

### 2 · `Includes/neuron_layout.hlsl` — one GPU definition

```hlsl
// Normalized neuron position -> field pixel space. Must match
// NeuronFiringSource.NeuronToFieldPixels exactly.
float2 NeuronToField(float2 np, float2 neuronScale, float2 rez)
{
    return np * neuronScale + rez * (1.0 - neuronScale) * 0.5;
}
```

Replaces the identical hand-written line in `PhysarumSim.compute:64`, `BoidSim.compute:104`,
`TermiteSim.compute:70`, and the ring equivalent in `SimulationManager.compute`.

Per [[../../adr/0005-includes-copied-verbatim|ADR-0005]] `Includes/` is copied verbatim, not
vendored — this file follows that convention.

### 3 · Consumers stop declaring, start reading

| Component | Was | Becomes |
|---|---|---|
| `SimulationBase` | `public Vector2 spawnScale` (line 68), serialized **per sim instance** | removed; reads the source |
| `SimulationManager` | `m_RingSpawnScale` (line 83) | removed; reads the source |
| `BiomeInjector` | `firingSpawnScale` (line 123) | removed; reads `firingSource.SpawnScale` |

`BiomeInjector` already holds `public NeuronFiringSource firingSource` (line 119), so it needs no new
wiring — it has been carrying a redundant copy of a value it could already reach.

`SimulationBase` holds only the firing `ComputeBuffer`, not the source. `SimulationManager` owns the
`NeuronFiringSource` reference and already drives the sims, so it **pushes** the scale down when it
uploads neuron data, rather than each sim acquiring its own reference. That keeps the dependency
direction as it is today.

`AppendNeuronPositionStamps` (`BiomeInjector.cs:388`) drops its inline arithmetic and calls
`firingSource.NeuronToFieldUV(np)`.

## Migration

Removing a serialized field loses its authored value, so ordering matters:

1. **Record** current per-scene sim values: CURRENTS `0.4,0.75`; SIGGRAPH `0.5,0.6`; DAC `0.5,0.6`;
   TestScene `0.8,0.9`.
2. Add `NeuronFiringSource.spawnScale` and set it per scene to the value from step 1 — **before**
   removing anything.
3. Remove the three old fields and repoint consumers.
4. Re-verify each scene renders with rings and stamps on the agents.

### This is a visible behaviour change, and it is the fix

In 11.2 SIGGRAPH and 11.3 DAC, firing rings and dispersal stamps **will move** — they are currently
in the wrong place. Anyone reviewing the before/after should read that motion as the defect being
corrected, not as a regression. 11.1 CURRENTS and TestScene should be pixel-identical; they are the
control.

## Files touched

- `src/components/network/NeuronFiringSource.cs` — field + `SpawnScale` + both mapping functions.
- **New** `src/computes/Includes/neuron_layout.hlsl`.
- `src/components/core/SimulationBase.cs` — remove field (line 68); scale arrives from the manager
  at line 443's upload site.
- `src/components/core/SimulationManager.cs` — remove `m_RingSpawnScale` (83), `s_RingSpawnScaleID`
  (139) reads from the source at line 478; push scale to sims.
- `src/components/network/BiomeInjector.cs` — remove `firingSpawnScale` (123); `NeuronToFieldUV` at
  398–399.
- `src/computes/PhysarumSim.compute`, `BoidSim.compute`, `TermiteSim.compute`,
  `SimulationManager.compute` — include the header, call `NeuronToField`.
- All four scenes — set `NeuronFiringSource.spawnScale`, per Migration.

## Non-goals

- **Per-sim spawn scale.** The audit shows no scene uses divergent values across sims. If a future
  piece wants one species offset from the layout, that is a per-sim *offset* on top of the shared
  layout, not a competing copy of it — a different feature, designed then.
- Renaming `spawnScale` → `neuronLayoutScale`. Clearer, but it churns every scene's serialized data
  for a cosmetic gain; the name stays.
- Touching the rest of `NeuronFiringSource` (blob loading, decay envelope, OSC drive).

## Risks

- **Serialized-data loss** if the field is removed before the new one is authored. Mitigated by the
  ordering in Migration; step 2 strictly precedes step 3.
- **CPU/GPU drift** is the failure this replaces — two definitions in two languages that must agree.
  Reduced from five definitions to two (one HLSL, one C#), and `NeuronToFieldPixels` exists
  specifically so an EditMode test can assert they match at the corners and centre.
- **`SimulationManager.compute`'s ring path** uses `ringSpawnScale` as a separate uniform name. Verify
  its formula is genuinely identical to the sims' before collapsing — the audit compared *values*,
  not the ring shader's arithmetic.

## Success criteria

1. `grep -ri 'spawnscale' src` returns exactly one declaration.
2. The mapping formula appears once in HLSL and once in C#.
3. EditMode test: `NeuronToFieldUV` and the HLSL `NeuronToField` agree at `np` = (0,0), (0.5,0.5),
   (1,1) for at least two distinct scales.
4. 11.1 CURRENTS and TestScene render unchanged (control).
5. 11.2 SIGGRAPH and 11.3 DAC show rings and stamps landing on agent clusters at the frame edges,
   where the error was largest.

## Follow-ups

- ADR recording that neuron layout has a single owner, once shipped.
- Audit the codebase for the same pattern — other values carrying a "match the X" tooltip are the
  same defect waiting to happen.
