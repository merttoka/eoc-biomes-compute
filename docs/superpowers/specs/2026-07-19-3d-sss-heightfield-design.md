# Design: 3D SSS heightfield form (bioluminescent relief)

Date: 2026-07-19
Scope: `Assets/Workspace/11.0 Biomes`
Status: Approved, pending spec review

## Summary

First 3D element for the biomes: a displaced heightfield mesh with HDRP subsurface
scattering, driven read-only by the existing sims. Permeability mounds (ch7, termite-built
topography per [[../../adr/0010-permeability-agent-built-topography|ADR-0010]]) form the
slow terrain base; the total trail texture adds fast fine relief. The 2D composite output
is sampled as subdermal emission so agent activity glows through translucent "flesh" —
and the palette automatically matches the show (composite is already graded: per-sim
trail colors + `moundColor`). Rendered by a dedicated orbiting camera to its own RT and
sent as a **second** external texture stream (`EoC-Form3D`). Zero changes to sim/biome
code paths; the existing composite stream is untouched.

## Decisions (locked)

- **Form:** heightfield relief (not volumetric, not SDF blob, not 3D sims).
- **Output:** second Syphon/NDI stream via a second `ExternalTextureSender`; TD mixes.
- **Height source:** `permGain * biome[CH_PERMEABILITY] + trailGain * totalTrail`.
- **Look:** bioluminescent flesh — warm skin-like HDRP diffusion profile, subdermal
  emission from `compositeOutTex`, back/rim key light.
- **Camera:** slow auto-orbit, OSC override (`/cam3d/*`).
- **Bake resolution:** biome field resolution (height detail is bounded by the field
  anyway; cheaper than composite res; trail tex sampled bilinearly at field res).
- **Stream name:** `EoC-Form3D`.
- **Home:** `TestScene.unity` first, as a prefab (`BioForm3D`) so promotion to
  `Scene_CURRENTS` is a drag-and-drop.

## Architecture

Approach chosen: **compute-baked height+normal pre-pass → simple displaced mesh**
(over pure-Shader-Graph displacement). Rationale: normals from central differences in
the bake kernel light correctly under SSS; temporal smoothing lives in HLSL alongside
the rest of the codebase's kernels; the shader graph stays trivial.

```
Biome.Step() ──┐ (read-only taps)
totalTrail  ───┤
               ▼
      HeightBake.compute            LateUpdate, after composite
      (smooth h, bake n)
               ▼
   _HeightNormalTex (RGBA16F: n.xyz, h)
               ▼
   M_BioForm shader graph            vertex displace + SSS + emission
   (emission ← compositeOutTex)
               ▼
   3D camera → form3dOutTex ──► ExternalTextureSender #2 ("EoC-Form3D")
```

## Components

### 1. `computes/HeightBake.compute` — kernels `BakeHeight`, `BakeNormal`
- Two dispatches per frame; the dispatch boundary is the sync barrier (a single kernel
  reading neighbor texels it also writes would race).
- **`BakeHeight`** — threads over field res. Reads biome read-buffer slice
  `CH_PERMEABILITY` and `totalTrail` (bilinear, texel-center UVs `(id+0.5)/rez` per
  existing convention). `target = permGain * perm + trailGain * trail`; temporal
  smoothing `h = lerp(prevH, target, smoothK)` (own-texel read-modify-write only —
  safe, no ping-pong). Writes `.a` of `_HeightNormalTex` (RGBA16F).
- **`BakeNormal`** — central differences over the fully-updated `.a` heights of the
  4 neighbors, writes `normal.xyz * 0.5 + 0.5` to `.rgb` (own-texel RMW preserves `.a`).

### 2. `components/render3d/HeightfieldForm.cs` (MonoBehaviour)
- References: `SimulationManager` (for `Biome`, total trail, `compositeOutTex`).
- Owns `_HeightNormalTex` RT. Clear-in-place per [[../../adr/0008-clear-in-place-reset]]:
  guarded `Allocate()` keyed on field-resolution signature; RT instance stable across
  resets so the material binding and any downstream use never tear down.
- `LateUpdate()` (after `SimulationManager`'s composite via script-execution-order):
  dispatch `BakeHeight`, push material params.
- Inspector + OSC params (`/form3d/`): `permGain`, `trailGain`, `smoothK`,
  `heightScale`, `emissionGain`. OSC wired through the existing `OSCMapping` pattern.
- Missing refs → component disables itself with a single warning (no per-frame spam).

### 3. `M_BioForm` — HDRP Lit Shader Graph + diffusion profile
- Material type **Subsurface Scattering**; new diffusion profile asset
  `DP_BioFlesh` (warm scatter radius, red-shifted transmission — flesh preset tuned
  toward the show's warm tones, cf. `moundColor` `(0.25, 0.18, 0.12)`).
- Vertex stage: sample `_HeightNormalTex.a` → displace along object Y by `heightScale`.
- Fragment: normal from `_HeightNormalTex.rgb` (world-space remap), base color = dark
  warm flesh tone, **emission = `compositeOutTex` sample × `emissionGain`** (subdermal
  glow, palette-matched by construction), smoothness modest (~0.35).
- Mesh: static dense grid plane (~512×512 verts, aspect-matched to field). Generated
  once in `HeightfieldForm.Allocate()` (procedural mesh, 32-bit indices) — avoids HDRP
  tessellation-stage texture-sampling headaches.

### 4. Scene rig (prefab `BioForm3D`)
- Plane + `HeightfieldForm` + `M_BioForm`.
- Lights: one warm spot/area behind-above the form (rim/backlight, drives SSS
  transmission through ridges) + very dim cool fill. No other scene lights affect it
  (light layers).
- `OrbitCamera3D.cs`: slow auto-orbit (azimuth drift, gentle elevation sine);
  OSC `/cam3d/azimuth`, `/cam3d/elev`, `/cam3d/dist`, `/cam3d/auto` (0|1). Renders to
  `form3dOutTex` (allocated by `HeightfieldForm`, composite-res, stable instance).
- `ExternalTextureSender` #2 on the camera output, stream name `EoC-Form3D`.

## Data flow & coupling

Strictly read-only taps on `biome` read buffer, `totalTrail`, `compositeOutTex`.
No new biome channels, no sim changes, no composite changes. `ResetTermites` melts the
mounds → temporal lerp makes the 3D form deflate smoothly for free.

## Error handling

- `Allocate()` signature guard exactly mirrors ADR-0008 owners.
- Null `SimulationManager`/`Biome` refs: disable + one warning.
- If HDRP diffusion profile missing from the scene's volume/HDRP asset list, material
  falls back to standard lit — visible but not broken; note in prefab README comment.

## Testing (manual, TestScene)

1. Mounds rise where termites build; `ResetTermites` deflates smoothly.
2. Trail shimmer visible as fine relief; emission glows through skin in dark areas.
3. Rim light bleeds through thin ridges (SSS transmission sanity check).
4. OSC `/form3d/*` + `/cam3d/*` respond; `/sim_reset` does not tear down either stream.
5. Existing composite stream byte-identical behavior (no regressions).
6. Perf: bake kernel is one field-res dispatch — negligible next to `Biome.Step()`.

## Out of scope (explicit)

Volumetric extrusion, SDF blobs, 3D sims, TD-side mixing changes, promotion into show
scenes (follow-up after TestScene validation).
