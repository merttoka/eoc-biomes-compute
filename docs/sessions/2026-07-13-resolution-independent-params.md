---
status: closed
date: 2026-07-13
tags: [session, sim, resolution, params, exhibition]
related: [[../ROADMAP]], [[../ARCHITECTURE]]
---
# Resolution-independent params + backlog audit

## Shipped
- **Resolution-independent params** (`7420c7e`): pixel-unit sim params are authored at a
  reference height (`referenceHeight = 2160`) and rescaled on Reset by `k = rezY/referenceHeight`,
  so motion + trail density read the same at any output resolution. `IParamSet.ScaleSpatial`
  (distance: `moveSpeed`, sensor distance, boid neighbour ranges, `maxForce`,
  `foodSensorDistance`) + `ScaleDensity` (deposit/eat) implemented on all three param SOs;
  applied centrally in `SimulationBase.Reset()` on the **runtime clone** (non-destructive,
  re-cloned each Reset → never compounds, never touches the asset). `SimulationManager` exposes
  `referenceHeight` + per-axis toggles (`scaleSpatialToResolution`, `scaleDensityToResolution`),
  pushed to sims in all three reset paths (full / sims-only / per-family OSC). Boid neighbour
  ranges upload squared, so scaling the *linear* field yields correct k² for free.
- **No-op at 2160** (SIGGRAPH's res) → current look preserved bit-for-bit; the scaling only
  activates on a resolution change — exactly the bug it fixes (1920×1080 → 3840×2160 had made
  the sim read half-speed).
- **Backlog audit + docs** (`5e692a6`): 3-agent code audit corrected stale statuses — B-channel
  predator/prey is 🟡 partial (Physarum-flees-Termite works; Boid's avoid neutralized by
  `max(0,·)`; Termite none), mortality + permeability-topography NOT-STARTED. New `ROADMAP.md`
  with a Dead-Code section (habitat gate, mortality params, Boid avoid — all configured-but-unread).

## Decided
- Reference = **height only** (`rezY/2160`); installs go wider (multi-projector), not taller.
- Scale the **runtime clone in Reset**, not the asset and not the GPU upload — matches the
  existing non-destructive `beh*Mul` pattern. Density scaling is a first-order `×k` under its
  own toggle (two derivations — transit-time and ink-over-area — agree for height-only).
- Per-scene `referenceHeight`: SIGGRAPH 2160 (`k=1` now); **CURRENTS runs 1080** → set its own
  `referenceHeight = 1080` or leave toggles off, else its 1080-tuned params halve on Reset.

## Open / next session
1. Play-verify equivalence: 1080 vs 2160 should show the same on-screen motion + density.
2. Unscaled edges (follow-ups, bite only at height ≠ 2160): OSC/MIDI clamp *bounds* +
   `ParameterInterpolator` waypoint targets.
3. **Permeability rework brainstorm** — resumed (audit findings captured in ROADMAP).
