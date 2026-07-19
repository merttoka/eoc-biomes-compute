# Temporal Composer — Design

**Date:** 2026-07-19
**Target:** `11.2 SIGGRAPH Scene` show, `EoC-biomes-compute`
**Status:** Approved design, pre-implementation

## Purpose

Editor-hosted temporal sequencer for the SIGGRAPH show. Choreographs, on a scrubbable timeline:

- which sims are visible in the output (fade/switch/blend)
- param snapshot application/interpolation
- sim resets at cue points
- network texture routing
- a grid of 2–4 **live biome cells** shown overlayed on or replacing the sim composite
- **scattered patches** of a StreamDiffusion return stream, animated with the SimAesthetics/Anadol grammar

## Decisions (brainstorm outcomes)

| Question | Decision |
|---|---|
| Source "textures" | Live sim RenderTextures (no file textures exist) |
| Repo | `EoC-biomes-compute` |
| Show runtime | Unity Editor play mode; EditorWindow-authored, PlayableDirector-executed |
| Architecture | Unity Timeline + custom tracks (approach A) |
| Biome grid | In the composite output, 2–4 live cells, overlay or replace |
| Network patches | Free-scattered patches; also feed `externalInfluenceTex`; source = StreamDiffusion return via Spout/Syphon/NDI |
| Composer output | **Own RT, resolution independent from sim rez** (not in-place into `compositeOutTex`) |
| Composer rez default | Match sim composite rez (keeps `ScreenLayout` pixel-crop rects valid); scale factor exposed |
| Displays | `ScreenLayout` + senders sample `composerOutTex` (not the sim composite) |
| "Info UI" | = the cells+patches layer itself; debug/annotation overlay is a separate optional toggle layer |
| Show machine | Windows, RTX 5080; **TouchDesigner runs StreamDiffusion**; Unity rates capped to share GPU |
| Local transport | Spout both directions (Unity ↔ TD); NDI only for cross-machine |

## Architecture

### Data flow

```
SimulationManager ──compositeOutTex──┐
BiomeCellRig ×N ──cell outTex──────┤
ExternalTextureReceiver #2 ─────────┤        (StreamDiffusion return)
                                     ▼
PlayableDirector(ShowSequence) → CompositeSequencer → composerOutTex → ExternalTextureSender / display
        ▲                                                   │
  track mixers push per-frame state              sent out → StreamDiffusion → back in (loop)
```

### Components

**`CompositeSequencer`** (runtime MonoBehaviour, `11.0 Biomes/src/components/sequencer/`)
- Owns `composerOutTex`: ARGBHalf, configurable resolution independent of sim rez (default: display rez). Allocated once, cleared in place, never recreated (same stable-RT rule as ADR-0008 so senders keep their native handle).
- `LateUpdate` after `SimulationManager.Render()`: base layer = sample `CompositeOutputTexture` (or skip when a Replace cell owns the frame), then CellKernel, then PatchKernel, then optional debug/annotation overlay pass (toggleable, off for show). One dispatch each.
- `ScreenLayout` and the outbound sender are re-pointed at `composerOutTex`; default composer rez = sim composite rez so existing pixel-crop rects stay valid (scale factor exposed for perf).
- Receives per-frame state from Timeline mixers: active cells `{sourceRT, dstRect, weight, mode}`, active patch events, param interpolation targets, routing flags.

**`BiomeCellRig`** (prefab, ≤4 instances in scene)
- Trimmed SimulationManager + Biome + sims at reduced rez (default 1024²), each wired to its own preset/snapshot assets. Timeline clips activate/deactivate rigs; `CompositeOutputTexture` of each rig is a cell source. Cell tick rate independently cappable (15–30 Hz).
- Cell sources may also be: the main composite itself, or a received network stream.

**`SequencerComposite.compute`** (2 kernels)
- `CellKernel`: per active cell, sample source into `dstRect`. `Replace` = lerp toward cell color by weight; `Overlay` = additive/screen by weight. Weight driven by Timeline clip ease curves.
- `PatchKernel`: active `PatchEvent` structs in a `StructuredBuffer` (cap 512). Per patch: hold/fade alpha envelope + per-patch sigmoid stochastic crossfade between two sources (raw composite vs diffusion return).

**Patch grammar** (ported from `COMFY_SimAesthetics/scripts/render_overlay_video.py`)
- `PatchScatterClip` generates `PatchEvent[]` deterministically from `(clip params, seed)` on clip start/scrub: rejection-sampled non-overlapping dst rects, randomized sizes.
- Size→hold inversion: large patches flash (~10 frames), small linger (~90). Asymmetric lead/trail stagger + jitter. Sweep-line activation, O(active) per frame, pooled lists, zero per-frame GC alloc.

### Timeline tracks (`ShowSequence` TimelineAsset in `11.2 SIGGRAPH Scene/assets/`)

| Track | Clip payload | Mixer effect |
|---|---|---|
| `BiomeCellTrack` | cell source (rig/stream/composite), dstRect, mode | weight from clip blend curves → CellKernel |
| `PatchScatterTrack` | seed, density, patch min/max, hold curve, stagger, crossfade center/width, source binding | event generation + PatchKernel feed |
| `ParamSnapshotTrack` | snapshot `.asset`, ease | interpolate current→snapshot numeric params (reuse existing parameter-interpolation machinery) |
| `RoutingTrack` | receiver stream → per-sim `externalInfluenceTex`, overlay on/off | assignment in `Step()` path |
| Reset markers | Timeline `SignalEmitter` | `SignalReceiver` → `ResetSimsOnly()/ResetPhysarum()/ResetBoids()/ResetTermites()` |

### Biome Palette (`EditorWindow`, `11.0 Biomes/src/Editor/sequencer/`)

Grid of all snapshot/preset assets with cached PNG thumbnails (captured from composite on snapshot save or on demand, stored next to asset). Drag onto timeline → creates `ParamSnapshotClip` or `BiomeCellClip`. Follows `ScreenLayoutPreview` idioms.

### StreamDiffusion loop

- Existing `ExternalTextureSender` ships `composerOutTex` out via Spout → **TouchDesigner runs StreamDiffusion** on the same Windows/RTX 5080 machine → returns via Spout into a **second** `ExternalTextureReceiver` instance (so the diffusion return and any other input stream coexist).
- Spout both directions locally (same-GPU zero-copy); NDI only for cross-machine sources. Syphon path remains for mac dev without the diffusion leg.
- Return stream binds as PatchKernel source and optionally as `externalInfluenceTex` (existing path unchanged).
- Patch scheduling makes diffusion fps nearly irrelevant: patches hold 0.2–1.5 s, so 5–10 fps return reads identically to 30 fps.

## Performance budget (single machine, 4090-class)

| Piece | Budget | Mitigation |
|---|---|---|
| Composer pass | 0.5–1.5 ms/frame | 1–2 dispatches; composer rez independent (4K ≈ half the texels of 10k×2k) |
| 2–4 cells | +30–60% of sim GPU time | 1024² rigs, 15–30 Hz cell tick |
| StreamDiffusion | dominant; ~25–60 ms/img at 512² uncontended | cap Unity: `targetFrameRate = 30`, sim tick 30 Hz → diffusion gets ~half the GPU, ~8–15 fps |
| Timeline/mixers | negligible CPU | pooled, no per-frame alloc |

Rate caps (`targetFrameRate`, sim Hz, cell Hz, composer rez) exposed as `CompositeSequencer` inspector fields.

## Failure handling

- Receiver texture null/stale → PatchKernel falls back to self-sampling the composite (degrades gracefully, never black).
- Missing snapshot asset → clip no-op + console warning.
- Missing/disabled cell rig → weight forced to 0.
- All sequencer behavior play-mode-guarded; scrubbing outside play mode previews schedule only.

## Scrub semantics

Sims are stateful and forward-only. Scrubbing repositions the deterministic layer (cells layout, patch events, param targets); sim state keeps evolving from wherever it is. Patch events are exactly reproducible from `(clip, seed)`.

## Testing

- Edit-mode: patch-event generation determinism (same seed → same events), non-overlap invariant, size→hold mapping, sigmoid crossfade distribution.
- Play-mode smoke: sample sequence evaluates with zero per-frame GC alloc; composer RT stable across resets.
- Manual visual validation in `Scene_SIGGRAPH.unity`.

## New files

```
11.0 Biomes/src/components/sequencer/
  CompositeSequencer.cs
  BiomeCellRig.cs
  PatchEventScheduler.cs
  tracks/ (BiomeCellTrack/Clip/Mixer, PatchScatterTrack/…, ParamSnapshotTrack/…, RoutingTrack/…)
11.0 Biomes/src/computes/SequencerComposite.compute
11.0 Biomes/src/Editor/sequencer/
  BiomePaletteWindow.cs
  SnapshotThumbnailCache.cs
11.2 SIGGRAPH Scene/assets/ShowSequence.playable  (+ BiomeCellRig prefab wiring in scene)
```
