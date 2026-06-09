# OSC-Driven Shared Neuron Firing — Design Spec

**Date:** 2026-06-08
**Workspace:** `Assets/Workspace/11.0 Biomes`
**Goal:** Replace the termite firing system's internal sequential playhead with an
**external OSC-driven frame index**, and promote "neuron firing" from a
termite-only effect into a **shared signal that excites all three sims**
(termite, physarum, boid). Firing becomes a first-class external input — matching
the system's stated intent as *"a driven art system fed by external inputs … that
responds expressively to external forcing"* (`docs/RESEARCH_BRIEF.md` §4.4).

---

## 1. Motivation

Today the 131-neuron firing series plays back on an internal clock locked to the
sim step, and only termites react. We want:

1. **External playhead** — another patch (organoid readout / sequencer / live
   neural source) sends a *frame index* over OSC; that index selects which row of
   the precomputed firing blob is shown, instead of the auto-incrementing counter.
   The `.f16` file stays as the firing-value source; OSC only chooses *when/which*.
2. **Shared excitation** — a firing neuron lights up the agents *physically sitting
   at that neuron's location* across all three biomes, giving one coherent neural
   signal a body in every species.

---

## 2. Current state (what exists)

### 2.1 Firing blob
- `Assets/StreamingAssets/biomes11/termite_firing.f16` (~47 MB, LFS).
  Header `TFR1` + `uint32 neuronCount=131` + `uint32 frameCount=180000` + then
  `frameCount × 131` float16, row-major (frame → neuron).
- Loaded once in `TermiteSim.LoadFiringBlob()` (`TermiteSim.cs:312-347`) into a flat
  `ushort[] _firingHalf`.

### 2.2 Firing playback (the closed loop we're opening)
`TermiteSim.UploadFiring()` (`TermiteSim.cs:187-215`):
- `_currentFrame++` each step, wrap/clamp via `loopFiring` (`:212-214`) — **the
  sequential playhead we replace.**
- decode `baseIdx = _currentFrame * 131` → `_frameScratch[131]` (`:201-203`).
- per agent: `_firingScratch[i] = _frameScratch[i % 131] >= firingThreshold` (`:204-205`).
- `firingBuffer.SetData(_firingScratch)` (`:206`), bound to `moveAgents` +
  `writeTrails` kernels (`:209-210`). `firingThreshold` default `0.1`.

### 2.3 Firing effect (shader)
`TermiteSim.compute`: `:162` speed boost (`firing → fireMul × moveSpeed`),
`WriteTrailsKernel:193-196` bright deposit (`firingDepositAmount`).
Per-type GPU struct `TermiteTypeParams`: `firingSpeedMul` (2.0),
`firingDepositAmount` (3.0), `firingDepositProbability` (0.3).

### 2.4 Neuron-position seeding (already in termite & physarum)
Both expose `public TextAsset labelsPositionsCsv` (`TermiteSim.cs:32`,
`PhysarumSim.cs:33-34`), parse it → `neuronPositionsBuffer`, bind
`neuronPositions`/`neuronCount`/`neuronScale` to their `resetAgentsKernel`, and
seed `agent i → neuron i % neuronCount` (`TermiteSim.cs:122-149`). The asset
`Assets/Workspace/11.0 Biomes/data/labels_positions.csv` has **131 rows**
(`backbone_labels, xy_norm_0, xy_norm_1`, normalized) — row *k* ↔ blob neuron *k*.
**Boid does NOT seed from neurons** — `BoidSim.compute ResetAgentsKernel:83-89`
random-scatters.

### 2.5 OSC + broadcast plumbing (the templates we reuse)
- `OSCMapping.cs` — OscJack `OscServer(port)`, `MessageDispatcher.AddCallback(addr,
  (addr,data)=>…)`; existing addresses route to `sim.SetParameter` / `BiomeInjector`.
- **Thread-safe input pattern** = `BiomeInjector.SetValue` (`BiomeInjector.cs:83-106`):
  OSC callback (receive thread) sets a plain field + `dirty` flag; main thread
  consumes. We follow this, not the riskier direct-call path.
- **Shared-signal-to-all-sims pattern** = `SimulationManager` fetches one
  `ExternalTextureReceiver.OutputTexture` and assigns it to every sim's
  `externalInfluenceTex` (`SimulationManager.cs:122-130`).
- Prior art to mine: `10.0 Metaesthetica/src/.../NeuronFiringDriver.cs` +
  `computes/NeuronSplat.compute`.

---

## 3. Design overview — producer + consumer

```
  OSC /index <int>
        │ (receive thread: set _targetFrame + _dirty)
        ▼
 ┌─────────────────────────┐   per step (main thread)
 │  NeuronFiringSource      │   • consume _targetFrame → _currentFrame (hold if none)
 │  (component on manager)  │   • _intensity: 1 on new index, else decay → 0
 │  owns: blob, frame,      │   • decode row × _intensity → NeuronFiringBuffer[131]
 │         decay            │
 └──────────┬──────────────┘
            │ SimulationManager broadcasts NeuronFiringBuffer + count
            ▼  (mirrors externalInfluenceTex broadcast)
 ┌─────────────────────────────────────────────┐
 │  SimulationBase (shared consumer)            │
 │  • labelsPositionsCsv + neuron seeding       │  ← hoisted from termite/physarum
 │  • neuronFiring buffer ref + firingThreshold │
 │  • BindNeuronPositions(k) / BindNeuronFiring(k)
 └───────┬─────────────┬─────────────┬─────────┘
   Termite          Physarum         Boid
   read firing      read firing      ADD seeding + read firing
   (refactor)       (add effect)     (add effect)
```

**Unified neuron rule (all three):** agent's neuron `= id.x % neuronCount` — the
*same* modulo that seeds its position. Read `neuronFiring[neuronIdx]`, compare to
`firingThreshold`, excite. Because CSV row *k* = blob neuron *k* = agent spawn site,
a firing neuron excites exactly the agents standing at it, in every sim.

---

## 4. `NeuronFiringSource` (new component)

`Assets/Workspace/11.0 Biomes/src/components/network/NeuronFiringSource.cs`,
`MonoBehaviour`, referenced by `SimulationManager` (like `BiomeInjector`).
It is the **single owner** of the blob — it cannot live in `SimulationBase`
(per-sim) without loading 47 MB three times.

**Serialized:** the blob's `StreamingAssets` filename (reuse termite's existing
`firingBlobFile` string field + `LoadFiringBlob` byte-reader, moved here — it is a
binary blob, **not** a `TextAsset`); `float firingDecaySeconds = 0.5f`.

**State:** `ushort[] _firingHalf`, `int _neuronCount`, `int _frameCount`;
`volatile int _targetFrame`; `volatile bool _dirty`; `int _currentFrame`;
`float _intensity`; `float[] _row` (size `_neuronCount`); `ComputeBuffer
NeuronFiringBuffer` (size `_neuronCount`, `float`).

**API (thread-safe, OSC-facing):** `void SetFrame(int frame)` → `_targetFrame =
clamp(frame, 0, _frameCount-1); _dirty = true;`

**Public (consumer-facing):** `ComputeBuffer Buffer`, `int NeuronCount`.

**`Initialize()`** (called from `SimulationManager.Reset()`): load blob, alloc buffer.

**`UpdateFiring(float dt)`** (called once per step, early — alongside
`external.UpdateInput()`):
1. if `_dirty`: `_currentFrame = _targetFrame; _intensity = 1; _dirty = false;`
   then decode `_firingHalf[_currentFrame*_neuronCount + n] → _row[n]`.
2. else: `_intensity = max(0, _intensity - dt / firingDecaySeconds)`.
3. upload `_row[n] * _intensity → NeuronFiringBuffer`.

> Decode only happens on a new index (cheap); the per-step upload is 131 floats
> (≈524 B). No auto-advance — `_currentFrame` only moves on OSC (hold), and the
> envelope fades it to quiet when silent.

---

## 5. `SimulationBase` changes (the "move to base" hoist)

Hoist the neuron machinery out of termite/physarum into the shared base so all
three inherit it and termite is no longer special:

- **Fields:** `public TextAsset labelsPositionsCsv;`
  `protected ComputeBuffer neuronPositionsBuffer, dummyNeuronBuffer;`
  `public ComputeBuffer neuronFiring; public int neuronFiringCount;`
  `public float firingThreshold = 0.1f;`
- **Helpers (moved from termite):** `ParseCsvFloat2`, `LooksNormalized01`,
  `protected int BuildNeuronPositions(int resetKernel, Vector2 scale)` (parse CSV,
  create+bind `neuronPositions`, set `neuronCount`/`neuronScale`, return count),
  `protected void BindNeuronFiring(int kernel)` (bind `neuronFiring` +
  `neuronFiringCount`).
- **Manager broadcast:** `SimulationManager.Step()` sets each sim's `neuronFiring =
  neuronFiringSource.Buffer; neuronFiringCount = neuronFiringSource.NeuronCount;`
  right where it broadcasts `externalInfluenceTex` (`:122-130`).

Shared HLSL include `src/computes/includes/neuron_firing.hlsl`:
```hlsl
StructuredBuffer<float> neuronFiring; int neuronFiringCount;
float NeuronFireValue(uint agentId, int neuronCount) {
    if (neuronFiringCount <= 0) return 0;
    uint nIdx = (neuronCount > 0) ? (agentId % (uint)neuronCount) : agentId;
    return neuronFiring[nIdx % (uint)neuronFiringCount];
}
```
Each sim `#include`s it and gates effects on `NeuronFireValue(id.x, neuronCount) >= firingThreshold`.

---

## 6. Per-sim changes

| Sim | seeding | firing read | effect | params to add |
|---|---|---|---|---|
| **Termite** | already | refactor: drop `_firingScratch`/`firingBuffer`/`_currentFrame`/blob load; read shared `neuronFiring` via `NeuronFireValue` in-shader | unchanged (2× speed + bright trail) | none (reuse) |
| **Physarum** | already | add `BindNeuronFiring` + in-shader read | 2× speed + brighter deposit | `firingSpeedMul`, `firingDepositAmount` on `PhysarumTypeParams` (C# struct + HLSL + `PhysarumParams.cs` + ranges) |
| **Boid** | **ADD** (mirror termite reset: `labelsPositionsCsv`, `BuildNeuronPositions`, update `ResetAgentsKernel:83-89` to seed from `neuronPositions` when `neuronCount>0`, else keep scatter) | add `BindNeuronFiring` + in-shader read | speed burst (`maxSpeed × firingSpeedMul`) + brighter deposit | `firingSpeedMul`, `firingDepositAmount` on `BoidTypeParams` (C# struct + HLSL + `BoidParams.cs` + ranges) |

**Termite refactor detail:** `LoadFiringBlob` and `UploadFiring` are removed
(moved/owned by `NeuronFiringSource`). Termite's `moveAgents`/`writeTrails` shaders
switch from `StructuredBuffer<uint> firing` (per-agent) to the shared
`neuronFiring` float buffer + `NeuronFireValue`. Effect params and thresholds are
unchanged; only the *source* of firing moves. Termite keeps `firingDepositProbability`.

**GPU struct sync:** adding fields to `PhysarumTypeParams`/`BoidTypeParams` requires
matching the C# `[StructLayout]` struct and the HLSL struct field-for-field
(stride must agree) — same pattern as the existing termite firing fields.

---

## 7. OSC contract (`OSCMapping.cs`)

Single address, integer frame number:

```
/index <int>   →  neuronFiringSource.SetFrame( frame )
```

- Read the arg as int via OscJack `data.GetElementAsInt(0)`; if the sender emits a
  float, fall back to `(int)round(data.GetElementAsFloat(0))`. (All existing
  handlers use `GetElementAsFloat` — confirm `GetElementAsInt` is available in this
  osc-jack version during implementation; the float-cast fallback is the safe path.)
- Registered in `OSCMapping` alongside existing `AddCallback`s; needs a serialized
  reference to `NeuronFiringSource` (via `SimulationManager` or direct).
- `SetFrame` is thread-safe (field + dirty flag); the main-thread `UpdateFiring`
  consumes it. **No normalized-phase address** — int frame only, per decision.
- Out-of-range frames are clamped in `SetFrame`.

---

## 8. Idle behavior — hold + decay

- New index → `_intensity = 1`, hold `_currentFrame` (no auto-advance).
- No index → `_intensity` ramps to 0 over `firingDecaySeconds` (serialized,
  default 0.5 s). Firing values are scaled by `_intensity`, so neurons drop below
  `firingThreshold` and the system goes quiet rather than freezing "all-on."
- A streaming patch (e.g. 30–60 fps of `/index`) keeps `_intensity ≈ 1` and
  animates smoothly.

---

## 9. Files touched

**New**
- `src/components/network/NeuronFiringSource.cs`
- `src/computes/includes/neuron_firing.hlsl`

**Edit**
- `src/components/core/SimulationBase.cs` — hoist seeding + firing consumption
- `src/components/core/SimulationManager.cs` — own `NeuronFiringSource`, init + per-step `UpdateFiring` + broadcast
- `src/components/network/OSCMapping.cs` — `/index` handler
- `src/components/Sim/TermiteSim.cs` + `computes/TermiteSim.compute` — drop blob/playback, read shared buffer
- `src/components/Sim/PhysarumSim.cs` + `computes/PhysarumSim.compute` + `src/params/PhysarumParams.cs` — firing effect + params
- `src/components/Sim/BoidSim.cs` + `computes/BoidSim.compute` + `src/params/BoidParams.cs` — neuron seeding + firing effect + params

**Scene/asset wiring (inspector, not code)**
- Add `NeuronFiringSource` component, assign blob + wire into `SimulationManager`.
- Assign `labels_positions.csv` to Boid's `labelsPositionsCsv`.
- Set `firingSpeedMul` / `firingDepositAmount` defaults on physarum & boid presets.

---

## 10. Non-goals / risks

- **Non-goal:** biome-field (indirect/chemotaxis) coupling, graded (non-threshold)
  response, multi-blob switching, sending firing back out over OSC. Direct,
  threshold-based excitation only.
- **Risk — termite regression:** moving the blob out of `TermiteSim` and switching
  its shader from a per-agent uint buffer to the shared float buffer is the one
  behavior-touching refactor. Mitigation: keep `firingThreshold` and all effect
  params identical; verify termites look unchanged (firing cadence now external)
  before/after at a fixed `/index` sweep.
- **Risk — GPU struct stride drift:** adding params to physarum/boid type structs
  must stay byte-aligned with HLSL. Mitigation: mirror the existing termite firing
  fields exactly; verify no validation/upload errors on play.
- **Assumption:** boid uses the same 131-row `labels_positions.csv` so its neuron
  indices align with the blob. `NeuronFireValue` modulo-clamps if a different CSV
  is assigned (graceful, but spatial alignment is then lost).

---

## 11. Open questions

✅ No unresolved questions.
