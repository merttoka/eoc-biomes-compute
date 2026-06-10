---
status: closed
date: 2026-06-09
tags: [session, performance, gpu, exhibition, m4]
related: [[../../Assets/Workspace/11.0 Biomes/docs/PERFORMANCE]], [[2026-06-09-osc-neuron-firing]]
---
# Second performance pass on 11.0 Biomes (M4 exhibition)

Follow-up deep dive after the first pass (ARGBHalf, simResolutionScale, fused write-back,
parallel prefix-sum, biome stepEvery). All new changes default-preserving; full analysis +
numbers in [[../../Assets/Workspace/11.0 Biomes/docs/PERFORMANCE|PERFORMANCE.md]] §4b/§5.
Branch: `claude/biomes-11-performance-3aog3h`.

## Shipped
- **`SimulationManager.perceptionResScale`** (default 1, rec. 0.25) — perception tex was
  built per sim at full sim res but is pure bilinear upsampling of the 320×180 biome field;
  all sims read it by UV. `src/components/core/{SimulationManager,SimulationBase}.cs`.
- **Firing-ring overlay compaction** — `NeuronRingKernel` looped all 131 neurons per output
  pixel (~543 M iters/frame); now CPU compacts to active neurons (via new
  `NeuronFiringSource.ScaledValues`/`PositionsCPU`), quiet frames skip the dispatch.
- **`renderPersistence` exposed** (was hardcoded `*= 0.9` in all three RenderKernels) —
  the "fewer agents, same density" lever. `SimulationBase.cs` + 3 `.compute`.
- **Boid neighbour loop coalesced** — new `ReorderAgentsKernel` copies agents into
  spatial-hash cell order post-scatter; Move inner loop streams contiguous records.
  Bit-identical results. `BoidSim.{cs,compute}`.
- **Boid duplicate perception fetch removed** (posAhead sampled twice).
- **Eat-loop skip when `eatAmount == 0`** (physarum + boid WriteTrails) — was
  (typeCount−1) no-op RMWs per agent.
- **`SimulationManager.metabolismEvery`** (default 1, rec. 2–4) — heat/O₂ write-back
  decimated, amount scaled by N (flux-conserving).
- **Boid `agentsCount` cap 20 k → 250 k** (100 k target wasn't settable).
- PERFORMANCE.md: §4b second-pass changes, sharper boid neighbour-cost math
  (scanned ≈ 9·r²·N²/area — quadratic in N; keep under ~30 M), scene re-audit, updated
  recommendations (trail-layer float4 packing now top remaining item, ~2× physarum).

## Decided
- Trail-layer packing (RHalf array → one ARGBHalf, typeCount ≤ 4) **not** implemented
  without an editor session to validate — documented as PERFORMANCE.md §5.1 with a full
  sketch. Not ADR-worthy; revisit in Unity.
- Ring overlay compaction chosen over tiled dispatch — CPU already owns decayed values,
  exact visuals, simpler.

## Open / next session
1. Validate in Unity: Reset + Play, A/B `perceptionResScale` 1 vs 0.25, boid reorder at
   10 k → 100 k, persistence slider.
2. Scene_CURRENTS still has `showDebugGrid: 1` — set 0 for the show.
3. Implement trail-layer float4 packing (PERFORMANCE.md §5.1) — biggest remaining lever.
4. Bring-up per PERFORMANCE.md §10; budgets unchanged (base M4: ~2 M physarum @60,
   3–4 M @30; 10 M only realistic on M4 Pro @30).
