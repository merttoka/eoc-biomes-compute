---
status: living
date: 2026-06-09
tags: [performance, gpu, exhibition, m4, physarum, boids, termites]
related: [[../../../../docs/ARCHITECTURE]], [[INTEGRATION_DESIGN]]
---
# 11.0 Biomes — Performance Deep Dive (M4 Mac mini exhibition)

> Target: **M4 Mac mini, 16 or 32 GB**, dual Full-HD output **3840×1080**, streaming
> to TouchDesigner over Syphon. Desired load: **10 M physarum, 100 k boids, 131 k
> termites**, three sims compositing into one 3840×1080 canvas, plus up to 10 biome
> channels + 3 full-res sim outputs over Syphon.
>
> **Headline:** memory is *not* the binding constraint — **GPU bandwidth and dispatch
> volume are**. 10 M physarum at full canvas resolution is ~3–4× over a 60 fps budget on
> a **base M4 (10-core GPU)**, ~2× over 30 fps. The single dominant cost is the physarum
> agent count; everything else is secondary. This doc quantifies each cost, gives concrete
> count/parameter budgets, and lists the code changes already made on
> `claude/biomes-11-performance-e6ooju` plus the ones left as recommendations.

---

## 0. Hardware reality check

"16 or 32 GB Mac mini" ⇒ **base M4** (the M4 Pro mini starts at 24 GB). That means the
**10-core GPU**: ~3.5–4.6 FP32 TFLOPS and, critically, **~120 GB/s unified memory
bandwidth**. The hot kernels here are texture-sampling and scatter-write bound, so the
**120 GB/s figure governs the frame time**, not the FLOPS.

Two more multipliers shrink the real budget below the nominal 16.6 ms/frame:
- **TouchDesigner shares the same GPU and the same unified memory.** Compositing 3–4 FHD
  Syphon inputs + TD's own processing is easily 3–5 ms of GPU and a few GB of RAM. Unity's
  effective budget is closer to **~10–12 ms/frame**.
- **HDRP** carries its own render-target overhead at 3840×1080 (~0.5–1 GB, a few ms) even
  though the visible output is produced by compute, not the camera.

If 60 fps is essential at high counts, the clean answer is **run TouchDesigner on a second
machine over NDI**, or **lock to 30 fps**. The user already expects sub-60; this doc treats
both 60 and 30 fps as explicit budgets.

---

## 1. Memory burden

Per-agent buffer = `float2 pos + float2 dir + uint typeId` = **20 B**, double-buffered ⇒
**40 B/agent**. Trail arrays are `RHalf` (2 B) × (typeCount+1) layers × 2 buffers. After
the ARGBHalf change in this branch, `outTex` / `perceptionTex` / composite are **8 B/px**
(were 16 B).

Sim canvas ≈ **4.2 M px** (2048² today, or 3840×1080 = 4.147 M). Types: physarum 4,
boid 4, termite 1.

| Resource | Physarum (10 M) | Boid (100 k) | Termite (131 k) |
|---|--:|--:|--:|
| Agent buffers (×2) | **400 MB** | 4 MB | 5 MB |
| Trail array (×2, RHalf) | 84 MB | 84 MB | 34 MB |
| outTex (ARGBHalf) | 34 MB | 34 MB | 34 MB |
| perceptionTex (ARGBHalf) | 34 MB | 34 MB | 34 MB |
| Spatial hash buffers | — | ~0.5 MB | — |
| **Subtotal** | **~552 MB** | **~157 MB** | **~107 MB** |

Plus: biome (960×270, 10 ch, ×2 + 10 debug textures) ≈ **31 MB**; composite ≈ **34 MB**;
neuron-firing blob (131 × 180 000 × float16) ≈ **47 MB resident**.

**Unity GPU/unified total ≈ 0.9–1.0 GB** for the sim stack (was ~1.2 GB before ARGBHalf —
the format change saves ~230 MB). Add HDRP + Unity managed heap (~1–1.5 GB) and the
TouchDesigner process, and a **16 GB** machine is *workable but tight*; **32 GB** is
comfortable. The 400 MB physarum buffer is the largest single item but is not a problem.

**Conclusion: do not cut agents for memory reasons — cut them for compute.**

---

## 2. GPU dispatch rates & compute calculations

All sims run the same per-step skeleton (`SimulationBase.Step`): Move → Diffuse →
WriteTrails → (swap) → Render, plus the manager's perception build and biome write-back.
Threadgroups: agent kernels `[numthreads(1024,1,1)]` (boid Move is 512); texture kernels
`[numthreads(8,8,1)]`. "Samples" below are bilinear `Texture2DArray` fetches (≈4 texel
reads each).

### Per **sim step**, at target counts and 4.2 M px:

**Physarum (10 M agents) — the elephant:**
- `MoveAgentsKernel`: 10 M threads, ~9 samples each (6 own/total trail + 3 perception) +
  trig ⇒ **~90 M samples**.
- `WriteTrailsKernel`: 10 M threads, scatter RMW into the 5-layer trail (deposit own + eat
  the other 3) ⇒ **~40 M incoherent read-modify-writes** (no atomics — lost updates are
  accepted, but the memory transactions still happen).
- `DiffuseTextureKernel`: 4.2 M px × 4 types × 9 ⇒ **~151 M samples**.
- `RenderKernel`: 4.2 M px × 4 types.
- **Biome write-back** (`SimulationManager.Step` step 3): physarum's `UmweltPhysarum` writes
  channels 1, 6, 0 **plus** metabolicHeat (>0) **plus** oxygenConsumption (>0) =
  **5 separate `WriteField` dispatches × 10 M = 50 M agent threads**, each a scatter RMW into
  the biome field. This is a *hidden* cost equal in magnitude to the move kernel.
- Perception build: 4.2 M px × 5 reads.

**Boid (100 k) — the second hotspot, for a subtle reason:**
- Spatial hash: `Clear`, `HashAndCount`, `PrefixSum`, `Scatter`. **`PrefixSumKernel` is
  `[numthreads(1,1,1)]` — a single GPU thread looping over every grid cell serially.** Low
  occupancy, pure latency.
- `MoveAgentsKernel`: 100 k threads × neighbour loop over a 3×3 cell block. **`cellSize` =
  the *max* interaction range across all boid types**, and `BoidParams` has
  `separationRange` up to ~492 px. A 492-px cell over a 2048² canvas ⇒ very few, very large
  cells ⇒ **hundreds of neighbours per boid**, each an uncoalesced agent read. Cost scales
  ~ N × (local density) and balloons with the large ranges. **This, not the count alone, is
  the boid risk.**

**Termite (131 k) — cheap:** same per-agent shape as physarum but 76× fewer agents. Move
~1.2 M samples; Diffuse 4.2 M × 1 × 9 = 38 M; biome write-back 6 × 131 k. Keep at 131 k.

**Biome PDE:** 4 passes (`GenerateFlow`, `Advect`, `Interact`, `Diffuse`), each over the
biome grid and each calling `CopyAllChannels` (10 layers). At 960×270 this is small
(~0.26 M px), but it runs every step and re-renders all 10 debug channels when
`showDebugGrid` is on.

### Rough bandwidth tally (physarum alone, per step)
Move ~90 M × ~8 B ≈ 0.72 GB · Diffuse ~151 M × ~8 B ≈ 1.2 GB · WriteTrails ~40 M × ~8 B ≈
0.32 GB · Biome-write ~50 M × ~8 B ≈ 0.4 GB ⇒ **≈ 2.6 GB of traffic for physarum per step.**
At 120 GB/s that is **~22 ms — physarum alone, before boid, termite, biome, composite, and
HDRP.** Hence the ~15–20 fps estimate for the 10 M target on a base M4.

---

## 3. Bottleneck ranking

1. **Physarum agent count (10 M).** Drives Move (90 M), WriteTrails (40 M scatter), and
   biome write-back (50 M). ~⅔ of the frame. **The #1 lever.**
2. **Per-pixel passes × canvas resolution.** Diffuse + Render + Perception across 3 sims at
   4.2 M px ≈ 340 M+ samples/step. **Resolution is a 4× lever** (half each dimension ⇒ ¼ the
   work) and is now decoupled from output res (see §4).
3. **Physarum biome write-back = 5 dispatches.** 50 M agent-threads of scatter; now
   collapsible to 1 dispatch via the optional fused write-back (§4 #6).
4. **Boid neighbour loop with oversized `cellSize`.** Cap interaction ranges to shrink cells.
   (The serial prefix-sum latency wall is now fixed — §4 #5.)
5. **Biome PDE + debug grid every frame.** Decimate the PDE (`Biome.stepEvery`); disable the
   debug grid for the show.

---

## 4. Code changes made on this branch

All are behaviour-preserving at equal resolution / default settings, and isolated:

1. **ARGBHalf for `outTex`, `perceptionTex`, composite** (`SimulationBase.cs`,
   `SimulationManager.cs`). Halves bandwidth on the per-pixel render/composite/Syphon path
   (the dominant traffic) and the perception read in every Move kernel; saves ~230 MB.
   Output is saturated 0..1, perception is 0..1 — half precision is ample.
2. **Composite samples sim outputs by UV** (`SimulationManager.compute`, inline
   `sampler_linear_clamp`), **driven by `SimulationManager.simResolutionScale` (0.1–1)**. Each
   sim renders at `scale × output res` (aspect preserved — both dims scale equally) while the
   composite/Syphon output stays full-res — a **~1/scale² cut** on
   Diffuse/Render/Perception/trail-memory while keeping agent counts. At scale 1 this samples
   texel centres and is identical to the old integer indexing.
   *(Also fixes a latent mismatch where sims at 2048² were read pixel-exact at 3840×1080.)*
3. **Cached perception read-entry buffer** (`Biome.cs`). `BuildPerceptionTex` was doing a
   `new ComputeBuffer(...)` + `Release()` every call — **~180 GPU buffer allocs/sec**
   (3 sims × 60 fps). Now one reusable buffer per biome, grown on demand, freed in `Release()`.
4. **Biome PDE decimation — `Biome.stepEvery`** (default 1 = no change). The cadence now lives
   on the **Biome** (`Biome.Step()` self-decimates) rather than the manager. Runs the PDE
   (4 array passes + debug render) once every N calls; the field is slow-changing so **2–4 is
   visually invisible**. Sim deposits still accumulate into the field every step.
5. **Parallel boid prefix-sum** (`BoidSim.compute`). The spatial-hash `PrefixSumKernel` was
   `[numthreads(1,1,1)]` (one thread, zero occupancy). Now a single-threadgroup 256-wide
   chunked Hillis–Steele scan with a serial carry across chunks — same semantics (exclusive
   scan → `cellOffsets`, zeroes `cellCounts` for the scatter), scales with grid-cell count.
   No C# change (still dispatched as one group).
6. **Fused biome write-back (optional, swappable)** — `BiomeWriteFused.compute` +
   `Biome.fusedWriteCS` + `SimulationManager.fusedWriteback` (default off). When on, all of a
   sim's `(channel, amount)` deposits apply in **one dispatch** (field pos computed once, loop
   channels) instead of one `WriteField` dispatch per channel: **physarum 5×N → 1×N agent
   threads**. Same non-atomic accumulate semantics; the per-channel path stays the untouched
   default, so it's risk-free to A/B.

### Operator knobs already in the project (use these for the show)
- **`SimulationManager.stepMod`** — step (and composite) every Nth frame. `stepMod = 2`
  runs the whole sim at 30 fps while the app/Syphon still present at 60 (same texture sent
  twice). Halves sim cost for a 30-fps-effective look.
- **`SimulationManager.stepsPerFrame`** — keep at 1.
- **`compositeWeight`** per sim — rebalance brightness after changing counts.

---

## 5. Recommended code changes (not yet applied — need a Unity build to verify)

> Two former items here are now implemented on this branch — **fuse physarum write-back**
> (§4 #6) and **parallel boid prefix-sum** (§4 #5).

1. **Expose the render persistence fade.** Both `RenderKernel`s hardcode `current *= 0.9`.
   Exposing it lets you raise persistence to compensate for fewer agents (trails linger and
   fill the canvas), preserving the dense look at a fraction of the count.
2. **Skip metabolic-heat / oxygen write-back when unused**, and consider writing them at a
   *decimated* cadence (they feed the slow biome PDE, which is itself decimated).

---

## 6. Scene/config audit (Scene_CURRENTS.unity)

- **Single `SimulationManager` rig.** (An earlier inactive second rig has been removed.) Keep
  the scene to one active rig during the show.
- **`showDebugGrid: 1`** ⇒ 10 `RenderChannelTo` dispatches + 10 quads/materials per biome per
  frame. **Set to 0 for the exhibition.**
- Current counts are *far* below target (physarum 300 k–500 k, boid 10 k, termite 13 100),
  so the target represents a **~20–30× physarum, ~10× boid, ~10× termite** increase. Validate
  incrementally.
- Sims run at 2048²; with change #2 you can drop them to 1920×1080 (or 1280×720) and keep the
  3840×1080 output.

---

## 7. Agent-count & resolution budgets

Estimates for the full 3-sim + biome + composite + Syphon pipeline. "Sim res" is the
internal canvas (now independent of the 3840×1080 output). Termite stays **131 k** in all
cases (cheap, and it's the neuron-coupled "memory" layer — 131 × 1000).

| Target | Physarum | Boid | Sim res | biomeStepEvery | Notes |
|---|--:|--:|---|--:|---|
| **Base M4, 60 fps** | 1.5–2.5 M | 30–50 k | 1920×1080 | 2–3 | + cap boid ranges; debug grid off; one rig |
| **Base M4, 30 fps** | 3–4 M | 60–80 k | 1920×1080 or 2048² | 2 | `stepMod = 2` for 60 fps presentation |
| **M4 Pro, 60 fps** | 4–6 M | 80–100 k | 1920×1080–3840×1080 | 1–2 | ~2× GPU, 273 GB/s |
| **M4 Pro, 30 fps** | 8–10 M | 100 k | 1920×1080 | 1 | the only place the 10 M wish is realistic |

**The 10 M physarum target is not reachable on a base M4 at 60 fps.** It is plausible on an
**M4 Pro at 30 fps** with reduced sim resolution. On the base M4, plan for **~2 M physarum @
60 fps** or **~3–4 M @ 30 fps** and use parameter balancing (below) to keep the density.

---

## 8. Parameter balancing — keep the look with fewer agents

On-screen density is the **trail field**, not the agent dots. Cutting physarum from 10 M to
2 M (5×) can look nearly identical if each agent contributes more and trails persist/spread:

**Physarum** (`assets/PhysarumParams.asset`, per type):
- **`depositAmount` ↑ ~3–5×** — each agent lays a stronger trail (fewer agents, same ink).
- **`diffuseRate` ↑ toward ~0.98–0.995** — trails spread and fill the gaps between sparser
  agents so the mat reads as continuous.
- **`senseDistance` / `moveSpeed` ↑ slightly** — sparser agents explore more canvas.
- **Render persistence ↑** (raise the hardcoded `0.9`, see §5.3) — coverage lingers.
- **`compositeWeight`** to restore overall brightness.
- Keep `eatAmount` modest so the denser deposits aren't immediately erased.

**Boid** (`assets/BoidParams.asset`):
- **Reduce count 100 k → ~40 k** and **cap `separationRange`/`alignmentRange`/
  `attractionRange` to ≤ 64–96 px.** This is a *double* win: it shrinks the spatial-hash
  cell size (smaller, denser cells = fewer neighbours scanned per boid) **and** keeps the
  flocking visually tight. The current ~492 px range is the main neighbour-loop blowup.
- **`depositAmount` ↑** to keep trail brightness with fewer boids.

**Termite** (`assets/TermiteParams.asset`): keep **131 k**. Tune `depositProbability` /
`firingDepositProbability` for mound density; this layer is cheap.

**Biome:** `biomeStepEvery = 2–4`; keep biome resolution low (960×270 is fine — it's read
through bilinear perception anyway).

---

## 9. Syphon / TouchDesigner streaming

Syphon shares GPU textures **zero-copy via IOSurface**, so **publishing the composite is
nearly free** on the Unity side. Costs to watch:
- **Each stream = one Klak sender.** 10 biome channels are tiny (960×270) — cheap to send,
  but TD still pays to ingest 14 textures. Send the **composite always**, biome channels as
  needed, and **only the sim outputs TD actually uses.**
- **Full-res sim outputs (3840×1080) are the expensive streams** for TD to ingest. Use
  `SendStream.resolutionScale` (already supported in `ExternalTextureSender`) to send sim/
  biome outputs at 0.5× when TD only needs them for colour/feedback.
- Klak publishes 8-bit BGRA; the ARGBHalf change doesn't affect what crosses Syphon.
- Biggest systemic win: **move TD to a second machine over NDI** so Unity owns the whole GPU.

---

## 10. Bring-up order for the show

1. One sim rig only; `showDebugGrid = 0`; cap boid ranges; `biomeStepEvery = 2`.
2. Sims at 1920×1080, composite/output 3840×1080 (relies on the UV-composite change).
3. Bring physarum up gradually (500 k → 1 M → 2 M …), watching frame time; balance
   `depositAmount`/`diffuseRate`/persistence at each step.
4. Decide 60 vs 30 fps (`stepMod`) based on measured headroom with TouchDesigner running.
5. If targeting the full 10 M, do it on an M4 Pro at 30 fps with reduced sim resolution.
