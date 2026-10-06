---
status: accepted
date: 2026-10-06
tags: [adr, permeability, mounds, rendering, precision]
related: [[../ARCHITECTURE]], [[0010-permeability-agent-built-topography]], [[0008-clear-in-place-reset]], [[../sessions/2026-10-06-mound-detail-layer]]
---
# ADR-0016: Mound walls render from a sim-resolution mirror of permeability; the physics stays coarse

## Context
The composite paints termite walls from the biome's permeability channel (ch7). In 11.3 the
biome is 551×124 under a 4800×1080 output, so a cell spans ~8.7 output px and the bilinear +
`smoothstep` overlay reads as blocks on the projection. Options:
- **Raise `biomeRez`** — capped at 1024 wide (~4.7 px cells, still visible); the biome has no
  resolution scaling, so every channel's diffusion and relax would change in screen space; ~3.4×
  biome cost.
- **Cubic reconstruction of ch7 in the composite** — no blocks, but nothing finer than a cell.
- **Supersample the termite sim** — beside the point: its own trail texture is already 1:1 with
  the output; the blocks are ch7's.
- **A render-only layer at sim resolution.**

## Decision
A render-only **mound detail** layer — `Biome.PermeabilityDetail`, RFloat at the termite sim's
resolution, in permeability units — mirrors ch7, and the composite paints the walls from it while
`SimulationManager.moundDetail` is on (default off, so every scene renders as before).
- **Builds:** `BuildPermeabilityKernel` draws the same build event (same RNG decision) as a
  Gaussian brush at the agent's exact position (σ = `moundDetailWidth`, cut at ±⌈3σ⌉ px, torus
  wrap). Per-event peak = `buildAmount · cell / (σ√2π) · gain`: at gain 1 a path's line is as
  strong as the cells it crosses.
- **Healing:** each PDE step, `RelaxPermDetailKernel` scales every detail wall by the coarse
  wall's after/before ratio around the interact pass (bilinear across cells) — the detail heals
  exactly as much as the coarse field did, rather than re-running the relax law.
- **Resets:** `SyncPermDetailKernel` copies ch7 (bilinear) into the layer on allocation and after
  every permeability reset (`Biome.GPUReset`, `ClearPermeability`).
- Termites still sense ch7: behaviour is unchanged.

## Consequences
- Ratio-coupled healing is forced by precision. ch7 is RHalf and its relax step (0.0005/step
  toward 0.9) is below half resolution near open ground: a CPU float16 simulation (round to
  nearest) stalls walls at ~0.44 wall-ness and never heals weaker ones; this Mac's GPU truncates
  half stores, which stalls them higher. Re-running the law in float would fade detail walls the
  termites still feel. Whether walls *should* heal fully is a separate question (ROADMAP).
- RFloat, not RHalf: a half store rounds each brush tail's small deposit to a whole ulp — up, on
  truncating GPUs — and repeated passes pile the tails into halos (measured: 90 vs 80 ulps on a
  line of 32 builds).
- Only termite builds and ch7's relax are mirrored. Other ch7 writers — injector or seeder stamps,
  diffusion or advection of ch7, the temperature drift used when relax is 0 — are not drawn; 11.3
  has none. One termite sim per biome is assumed (the layer allocates at its resolution).
- Cost at 4800×1080: +20.7 MB VRAM; one relax pass per PDE step (open pixels exit after one
  sample); a brush of ~121 read-modify-writes per build event. Overlapping brushes in one
  dispatch can drop an update, like the cell write.
- Switching it on mid-run carries the walls built so far over (soft, from the bilinear sync);
  switching it off releases the layer.

## Related
[[0010-permeability-agent-built-topography|ADR-0010]] · [[0008-clear-in-place-reset|ADR-0008]] ·
`Assets/Workspace/11.0 Biomes/src/computes/Biome.compute` (mound detail kernels) ·
`SimulationManager.compute` (`CompositeRenderKernel`) ·
[[../sessions/2026-10-06-mound-detail-layer|session]]
