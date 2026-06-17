---
status: accepted
date: 2026-06-17
tags: [adr, unity, workspace, scenes, exhibition]
related: [[../ARCHITECTURE]], [[../sessions/2026-06-17-per-show-scene-split]], [[0005-includes-copied-verbatim]]
---
# ADR-0009: One workspace folder per show; shared engine stays in `11.0 Biomes/`

## Context
`Assets/Workspace/11.0 Biomes/` had grown into a single monolith: the sim engine
(`src/`, `docs/`) plus one show scene (`scene/Scene_CURRENTS.unity`), all its
materials, every param `.asset`, snapshots, and `old/` archives in one tree. With a
second exhibition coming (SIGGRAPH) the show needed its own scene + curated params +
look, but cloning the whole `11.0 Biomes/` tree would duplicate the engine and the
asset history, and editing one scene's assets risked perturbing the other.

Options:
- (a) **One folder per show** — keep `11.0 Biomes/` as the shared engine (`src/` + `docs/`
  + a smoke-test scene), give each exhibition its own `11.x <SHOW> Scene/` folder holding
  only that show's scene + curated `assets/`+`materials/`.
- (b) Keep one folder, multiple scenes inside it sharing one asset pool.
- (c) Separate Unity project per show.

## Decision
(a). `11.0 Biomes/` now contains only the shared engine — `src/`, `docs/`, and
`TestScene.unity` (the engine smoke-test, elevated from `scene/TestScene.unity`). Each
show is a sibling folder:
- `11.1 CURRENTS Scene/` — `Scene_CURRENTS.unity` + full curated `assets/` (incl.
  `Snapshots/` and `old/`) + 6 materials (keeps `m_debug`, `m_composite_LoadLayout`).
  This is the **active build scene** (EditorBuildSettings).
- `11.2 SIGGRAPH Scene/` — `Scene_SIGGRAPH.unity` + a leaner `assets/` (no `Snapshots/`,
  no `old/`) + 4 materials (drops `m_debug`, `m_composite_LoadLayout`). Forked from
  CURRENTS then trimmed: waypoints cleared, own `paramsSO`/umwelt/`outputMat` GUIDs,
  `spawnScale` retuned.

The StreamingAssets firing blob was also renamed `termite_firing.f16` →
`organoid_firing.f16` (byte-identical) so the filename reflects what it is — a shared
organoid spike series, not termite-private data ([[0006-osc-neuron-firing]]).

## Consequences
- Per-show tuning (params, materials, look, scene wiring) is isolated; one show can't
  perturb another's assets.
- The sim engine stays single-source in `11.0 Biomes/src/` — engine changes land once and
  every show picks them up. `docs/` (PERFORMANCE, RESEARCH_BRIEF, INTEGRATION_DESIGN) stays
  with the engine, so existing doc links into `11.0 Biomes/` remain valid.
- New show = copy the latest show folder, retrim. No project-level duplication (rejected (c)).
- **Cost:** curated assets are now duplicated across show folders (each carries its own
  `assets/`). Acceptable — they diverge per show by design, and the engine (the part worth
  deduping) is not duplicated.
- Snapshots are now **per show** under `<show>/assets/Snapshots/` → the memory daemon's
  `--snapshot-dir` is per-show, no single default ([[../migration]] open Q9).
- A scene's `firingBlobFile` is serialized per scene; the rename had to be applied in each
  scene's YAML + the `NeuronFiringSource` default, not just one place.

## Related
[[../ARCHITECTURE]] §2 · [[../sessions/2026-06-17-per-show-scene-split]] ·
[[0005-includes-copied-verbatim]] (same copy-don't-vendor instinct, shader includes) ·
[[0006-osc-neuron-firing]] (firing blob renamed organoid)
