---
status: closed
date: 2026-10-06
tags: [session, permeability, mounds, rendering, show]
related: [[../ARCHITECTURE]], [[../adr/0016-mound-detail-render-mirror]], [[../adr/0010-permeability-agent-built-topography]]
---
# Mound detail layer — termite walls drawn at sim resolution

Branch `mound-detail`. Trigger: on the 11.3 projection the termite trails read as big blocks.
What shows is the mound overlay — ch7 at 551×124 under a 4800×1080 output, ~8.7 px per cell —
not the termite sim's own trail texture, which is already 1:1 with the output.

## Shipped
- `Biome.compute`: `BuildPermeabilityKernel` also draws each build event into `permDetail`
  (Gaussian brush at the exact position, line-matched peak, torus wrap, `permDetailOn` gate);
  `RelaxPermDetailKernel` heals by the coarse after/before wall ratio around the interact pass;
  `SyncPermDetailKernel` copies ch7 in on allocation and resets.
- `Biome.cs`: `SetPermeabilityDetail(on, σ, gain)` + `PermeabilityDetail`; RFloat layer allocated
  at the termite sim's rez on the first build while on (a 1×1 dummy is bound while off), relaxed
  after the interact pass, synced in `GPUReset` and `ClearPermeability`, released when off.
- `SimulationManager`: `moundDetail` (default off), `moundDetailWidth` (σ, 1.5 px),
  `moundDetailGain` (1) under *Mound overlay*; pushed to the biome every `Step`, like keep-out;
  the composite binds the layer (or a dummy) with `moundDetailOn`.
- Tests: 24 new — `PermeabilityDetailTests` (kernels), `MoundCompositeTests` (composite kernel),
  `BiomeMoundDetailTests` (Biome lifecycle), `MoundDetailOverlayTests` (manager → biome →
  composite); guards mutation-checked. Suite 167: 166 pass, 1 pre-existing failure (Open #3).
- Headless A/B in the bench clone: `Scene_BraveNewWork`, 3600 steps, `wallBuildAmount` ×10 to
  stand in for ~10 min of building. Coarse walls are 9–18 px blocks with stair-step edges; the
  detail draws thin smooth paths and loops (lit wall pixels 107.7k → 58.8k). No errors.
- `tools/osc_index_tester.py`: PEP 723 header, so `uv run` resolves `python-osc` on the show PC.

## Decided
- Render-only mirror, ratio-coupled healing, RFloat storage
  ([[../adr/0016-mound-detail-render-mirror|ADR-0016]]).
- Rejected: raising `biomeRez` (1024 cap, per-cell physics changes everywhere); supersampling the
  termite sim (already 1:1); cubic upsampling of ch7 (no blocks, no detail) — kept as a fallback.
- Default off: 11.1, 11.2 and 11.3 render as before until `moundDetail` is ticked.
- Found while testing: this Mac's GPU truncates float→half texture stores (a line of 32 builds
  lowered a half texel by 90 ulps where the float sum is 80).

## Open / next session
1. Show PC: tick `moundDetail` on the 11.3 main `SimulationManager`, tune `moundDetailWidth`
   (1.5–2 smooths the beading between build events) and `moundDetailGain` live, save the scene.
2. ch7 relax stalls in RHalf short of open ground — walls persist until `ResetTermites`. Decide
   whether that is the intended look ([[../ROADMAP]]).
3. Pre-existing red: `PalettePresetTests.ShowPalette_Reproduces(Brave New Work, Physarum)` — the
   uncommitted `11.3/assets/PhysarumParams.asset` hue edits (type 0: 0.109 → 0.02). Re-harvest
   the show palette or revert.
