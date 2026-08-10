---
status: draft
date: 2026-08-10
tags: [spec, sim, cellular-automata, biome, events, engine-core]
related: [[../../adr/0011-field-native-sims-derive-simulationbase]], [[2026-07-23-cellular-automata-sims-design]], [[../../ARCHITECTURE]]
---

# Event-driven CA: bursts seeded from the biome, resolution off the master

## Problem

The CA sims run continuously at a resolution chained to the master output, composite as
constant layers, and seed from abstract figures (line / rect / circle / random) that have
nothing to do with the ecosystem they sit inside. Three consequences:

1. **The look moves when the output resolution moves.** `cellResolutionScale` is a *fraction*
   of master, so 1080p → 4K doubles the grid and halves apparent cell size. The rule is
   unchanged; the picture is not.
2. **The CA is decorative rather than coupled.** It publishes *into* the biome, but nothing
   flows the other way — the automaton never starts from what the ecosystem is actually doing.
3. **It runs when nothing is happening.** A continuously-evolving lattice cannot punctuate.

### What this is not

Not a performance fix. Measured on the 11.3 sandbox at 3840×2160 (Play mode, GPU-synced):

| | ms/step |
|---|---|
| `Physarum.Step()` | 4.217 |
| `Boid.Step()` | 3.014 |
| `Termite.Step()` | 0.843 |
| **`LookupCA.Step()`** | **0.190** |
| composite (all sims) | 1.273 |

`mgr.Step()` measured 9.257 ms with all sims, 9.214 ms without LookupCA and 9.303 ms with no
CAs at all — the no-CA case came out *slower*, so the CA's contribution is below the noise
floor. Within LookupCA, `rule ≈ 0.011 ms, publish ≈ 0.007 ms, render ≈ 0.182 ms`: the rule is
free and 96 % of the cost is the render pass, which `stepEvery` does not decimate. Going idle
between bursts therefore saves ≈ 0.19 ms of a 9.26 ms budget. **Do the work for the look and
the coupling; the compute saving is a rounding error.**

## Goals

- The CA's on-screen scale is independent of output resolution.
- A burst can be seeded from any biome channel, so the automaton grows out of the ecosystem.
- Bursts are events: triggered, sustained while firing continues, then faded.
- A faded burst leaves a trace the biome's own dynamics erode, spread and carry.
- The sim dispatches nothing at all when idle.

## Non-goals

- Per-neuron local blooms. A channel seed is a whole-grid raster, so bursts are global.
- Changing `CyclicCASim` / `LookupCASim` rule shaders beyond their seed path.
- Touching the agent sims or `SimulationManager`'s composite.
- Reworking `IParamSet` / MIDI binding. New fields are sim-level, not rule parameters.

## 1. Absolute cell resolution

Replace `cellResolutionScale` (fraction) on `FieldSimulationBase` with an absolute height:

```csharp
[Range(64, 2048)] public int cellRezHeight = 540;

protected int CellRezY => Mathf.Max(8, cellRezHeight);
protected int CellRezX => Mathf.Max(8, Mathf.RoundToInt(cellRezHeight * (float)rezX / Mathf.Max(1, rezY)));
```

Aspect comes from the master, so an 11.84:1 canvas still gets a correctly-shaped grid. The
composite already UV-samples every layer, so a small grid upscales for free. `NeedsAllocation()`
already keys on cell rez and needs no change.

**Migration.** `cellResolutionScale` will not deserialize into the new field, so both existing
components must be set by hand to preserve the current look exactly:

| Component | old scale | at rezY 2160 | new `cellRezHeight` |
|---|---|---|---|
| `11.3 / LookupCA` | 0.25 | 540 | **540** |
| `11.3 / CyclicCA` | 0.35 | 756 | **756** |

## 2. Seeding from a biome channel

New on `FieldSimulationBase` — deliberately *not* in the params assets, because only
`LookupCAParams` has an `InitMode` and `CyclicCAParams` has none; a shared placement covers
both CAs with one code path.

```csharp
public bool seedFromChannel = false;
[BiomeChannelField] public int seedChannel = BiomeChannel.Pheromone0;
[Range(0f, 1f)] public float seedThreshold = 0.5f;
```

Source is `publishTarget` — a CA seeding from a field it does not publish into is a
configuration with no use, and reusing the reference keeps one Biome per sim.

`Biome.FieldReadArray` is already public, so no new plumbing. A shared helper in
`cellular_common.hlsl` samples it **by UV, not by index** — the biome is 640×360 while the CA
grid is 960×540, and they are never guaranteed to match. This is the same rescale
`CouplingGate` already performs:

```hlsl
Texture2DArray<float> seedField;
int  seedChannelIndex;
float seedThreshold;
int  seedFromChannel;

bool SeededByChannel(uint2 id)
{
    if (seedFromChannel == 0) return false;
    float2 uv = (float2(id) + 0.5) / float2((float)cellRezX, (float)cellRezY);
    float v = seedField.SampleLevel(sampler_point_clamp, float3(uv, seedChannelIndex), 0);
    return v >= seedThreshold;
}
```

`sampler_point_clamp` is used because the coupling path already relies on it, so it is known to
be available in this include. The biome is coarser than the CA grid, so a point sample gives
blocky seed boundaries; if that reads badly, switching to a linear sampler is a one-line change
and smooths the threshold contour.

Each `ResetStateKernel` swaps only its `inside` test: `seedFromChannel` on → `SeededByChannel(id)`,
otherwise the existing `InitMode` figure. **The live-state hash is untouched**, so a seeded cell
picks its state exactly as today and stays non-quiescent — `table[0]` still keeps empty space
empty, and thresholding keeps the seed sparse enough to grow rather than boil.

## 3. Burst lifecycle

```
Idle ──TriggerBurst()──> Running ──age >= burstRuleSteps──> Fading ──envelope==0──> Idle
         ^                  │                                   │
         └──── retrigger: reset age, keep state ────────────────┘
```

```csharp
public bool burstEnabled = true;
public int burstSustainSteps = 240;   // sim steps, NOT rule steps
public int fadeInSteps       = 30;    // sim steps
public int fadeOutSteps      = 90;    // sim steps
[Range(0f, 1f)] public float burstFiringThreshold = 0.35f;
```

All three durations are counted in **sim steps**, not rule steps. `stepEvery` decimates only the
rule, so counting the burst in rule steps would make its wall-clock length depend on `stepEvery`
— change the pace and the burst silently changes duration.

- **Trigger.** `public void TriggerBurst()` plus `[Button("Trigger burst")]`. When
  `burstEnabled`, a rising edge of `NeuronFiringSource.Intensity` past `burstFiringThreshold`
  fires one. `Intensity` is already a public CPU-side float, so this needs no GPU readback.
- **Retrigger extends, it does not restart.** A trigger during `Running` or `Fading` resets the
  age and re-attacks the envelope but does **not** re-seed. Sustained firing therefore sustains
  a single evolving lattice; sparse firing gives short blips. Only a trigger from `Idle` re-seeds.
- **Envelope.** `_envelope` ramps 0→1 over `fadeInSteps`, holds while `age < burstSustainSteps`,
  ramps 1→0 over `fadeOutSteps`.

  It is bound as its **own uniform** (`outputEnvelope`) and multiplied into the render kernel's
  final colour. Two things it deliberately is *not*:
  - **not `compositeWeight`** — writing a serialized field every frame would dirty the scene and
    fight the Inspector;
  - **not `caParams.brightness`** — that is the live runtime clone, and scaling it would corrupt
    a MIDI/OSC-bindable rule parameter, drift on every reset, and be indistinguishable from a
    user edit.

  `SimulationManager` is untouched either way.
- **Idle costs nothing.** `Step()` returns before dispatching rule, render *or* publish. On the
  transition into `Idle`, `outTex` is cleared once so no stale frame lingers in the composite.

## 4. The trace outlives the burst

The CA publishes at **full gain** while a burst is alive — the envelope fades the *picture*, not
the deposit, or the trace would fade with it and there would be nothing to leave behind. On
going idle the sim stops publishing entirely and hands the channel to the PDE.

**This requires a config change.** `Excitability` and `Substrate` are currently
`diffuseRate 0, decayRate 0, advectedByFlow false, relaxRate 0` in both `BiomeFieldConfig.cs`
and the scene assets, precisely so the CA owns the channel and the PDE leaves it alone. With
every rate at zero a deposit does not erode — it freezes permanently, and agents would steer
around an invisible wall forever. The existing tooltip anticipates this: *"leave the PDE off it
… unless you want the pattern to bleed."*

Starting values, pitched against the existing channels (lower `diffuseRate` = more spread,
cf. `Dispersal` 0.9 vs `Nutrient` 0.995):

| | diffuseRate | decayRate | advectedByFlow | relaxRate |
|---|---|---|---|---|
| `Excitability` | 0.96 | 0.004 | **true** | 0 |
| `Substrate` | 0.96 | 0.004 | **true** | 0 |

`decayRate 0.004` is roughly a 3 s half-life at 60 Hz — between `Pheromone` (0.002, ~6 s) and
`Waste` (0.001). Tune on the render.

Changed in `BiomeFieldConfig.cs` defaults **and** in the `BiomeFieldConfig_Homeostatic.asset`
of 11.1, 11.2 and 11.3, so the three scenes do not drift.

## 5. Testing

EditMode (no GPU):

- `CellRezX/Y` from `cellRezHeight` across aspects, including 11.84:1 and the `Max(8, …)` floor.
- Envelope across attack / hold / release, including `fadeInSteps = 0` and `fadeOutSteps = 0`.
- State machine: idle→running→fading→idle; retrigger from `Running` and from `Fading` resets age
  without re-seeding; retrigger from `Idle` re-seeds.
- Rising-edge detection: no retrigger while `Intensity` stays above threshold.

GPU-only, verified on the render: channel seeding, the trace's erosion, and that an idle sim
dispatches nothing (re-run `Biomes > Perf > Probe CA Cost` and confirm idle ≈ 0 ms).

## Risks

| Risk | Mitigation |
|---|---|
| `cellResolutionScale` removal silently resets both scenes to the default height | Migration table above; set both by hand and eyeball against the current look before committing. |
| Non-zero rates on Excitability/Substrate change existing behaviour | Those channels are currently written only by the CAs, and nothing on `ca-dev` maps them in an `UmweltMapping` yet, so nothing reads them today. |
| A burst that never ends because firing sits above threshold | By design — "extend" means sustained firing sustains the CA. `burstEnabled = false` plus manual `TriggerBurst()` is the escape hatch. |
| Seeding from a near-empty channel produces a dead burst | `seedThreshold` is per-sim; a channel with no signal above it seeds nothing and the burst fades out harmlessly. |
| Idle sim leaves a stale frame composited | `outTex` cleared once on the transition into `Idle`. |

## Resolved decisions

1. **Bounded burst, then idle** — each burst has an authored length and the sim goes fully idle
   after it; not always-running-with-gated-visibility, and not run-until-settled. Bounded per
   burst, but extendable while firing continues (decision 5).
2. **Firing threshold + public `TriggerBurst()`** — automatic by default, overridable.
3. **Threshold-to-region seed mapping** — sparse, connected seeds that grow; not quantize (boils).
4. **Absolute cell resolution** — replaces `cellResolutionScale` outright.
5. **Retrigger extends** — resets the clock, keeps the lattice.
6. **Trace persists and erodes via the channel's own PDE** — decay + diffuse + advect.
