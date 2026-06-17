---
status: closed
date: 2026-06-17
tags: [session, unity, workspace, scenes, build, hdrp]
related: [[../ARCHITECTURE]], [[../adr/0009-per-show-scene-workspaces]]
---
# Per-show scene split (CURRENTS / SIGGRAPH) + organoid blob rename

Split the `11.0 Biomes/` monolith into one workspace folder per show, renamed the firing
blob, and committed the in-editor Unity 6 config + HDRP changes that rode along. Docs only
on my side — the Unity-side reorg was done in-editor; this session staged + documented it.

## Shipped
Five staged commits on `main`, pushed.

- **Scene split** ([[../adr/0009-per-show-scene-workspaces]]): `11.0 Biomes/scene/` +
  `assets/` deleted (56 tracked files). New `11.1 CURRENTS Scene/` (full curated assets +
  `Snapshots/` + `old/`, 6 materials) and `11.2 SIGGRAPH Scene/` (leaner — no Snapshots/old,
  4 materials, `Scene_SIGGRAPH.unity` forked from CURRENTS then trimmed). `TestScene.unity`
  elevated to `11.0 Biomes/` top level. `11.0 Biomes/` now = shared engine (`src/`, `docs/`).
- **Firing blob rename**: `StreamingAssets/biomes11/termite_firing.f16` →
  `organoid_firing.f16` (byte-identical, SHA256 `bb525d…`, 47,160,012 B, LFS). Updated all 3
  references — `NeuronFiringSource.cs` default + both scenes (`Scene_CURRENTS`, `TestScene`)
  still pointed at the old name and would have failed to load firing at runtime (SIGGRAPH was
  already updated in-editor).
- **Unity 6 project config**: new `Assets/Settings/Build Profiles/macOS.asset` (StandaloneOSX,
  Intel64, release); EditorBuildSettings enables `Scene_CURRENTS.unity` as the build scene;
  branding → `Metaesthetica` / `SimulacraNaturae-CURRENTS`; default screen 1280×720 windowed;
  platform targets bumped (macOS 12, iOS/tvOS 15, Android SDK 25); Unity analytics + cloud
  diagnostics disabled; QualitySettings → serialized v5 (meshLodThreshold, Switch 2 entry).
- **HDRP resources**: `HDRenderPipelineGlobalSettings` now populates 18 runtime volume
  profiles (was empty); `SkyFogSettingsProfile` schema migration — HDRI upper-hemisphere lux
  exposure overrides enabled, procedural-sky fields added, scroll params → object-mode,
  versioned (`m_Version`/`m_SkyVersion: 1`).
- **Docs**: README (layout + entry points + per-show snapshot path), ARCHITECTURE §2 (layout
  + per-show note) / §3.4 (blob name) / §5 (ADR-0009), migration Q9 (per-show snapshot dirs),
  ADR-0009, this session, INDEX.

## Decided
- One folder per show over multi-scene-one-pool or project-per-show → [[../adr/0009-per-show-scene-workspaces]].
- Engine (`src/`) + `docs/` stay in `11.0 Biomes/` → existing doc links into `11.0` stay valid;
  only `scene/`+`assets/` moved out.
- Blob renamed termite→organoid to reflect it's a shared organoid spike series, not termite-private.
- `migration.md` §1 (the April repo-split record) left as historical fact — reorg recorded in
  ADR/session, not by rewriting the split description.

## Open / next session
1. Daemon `--snapshot-dir` is now per-show — decide: pass per run, or glob `11.*/assets/Snapshots`
   (migration Q9).
2. `11.2 SIGGRAPH` diverged from CURRENTS by hand (cleared waypoints, retuned spawnScale, own
   param GUIDs) — no shared-base mechanism; future shows re-fork + retrim manually.
3. Curated `assets/` are duplicated per show; if they drift toward a common core, revisit a
   shared show-asset pool.
