---
status: living
date: 2026-08-02
tags: [delivery, shanghai, dac, siggraph, render]
related: [[superpowers/specs/2026-08-02-shanghai-dac-11-3-design]], [[sessions/2026-08-02-cellular-automata-dac-machinery]], [[ARCHITECTURE]]
---
# Delivery — Urban Digital Canvas Shanghai

3rd Shanghai International Light Festival, Changning District, 24 Sept – 8 Oct 2026.
Curated by Victoria Szabo and Wei He. **Submissions close 18 Aug 2026.**

## Deliverables — RENDERED 2026-08-02

`Recordings/DAC_Shanghai_2026-08-02/` (gitignored; 19 GB)

One master, two pure crops. **No rescale anywhere** — that is why the master is 9472×900
(10.524:1), sized so both screens fall out of it by cropping alone.

| File | Screen | Venue | Resolution | Crop | Size | Sound |
|---|---|---|---|---|---|---|
| `DAC_screen2.6_9472x800.mp4` | 2.6 | Xinda Plaza | 9472×800 (11.84:1) | `y + 50` | 359 MB | no |
| `DAC_screen1.2_9000x900.mp4` | 1.2 | Jingyao Hongqiao sunken square | 9000×900 (10:1) | `x + 236` | 386 MB | yes |
| `DAC_master_9472x900.mp4` | — | master, H.264 High preview | 9472×900 | — | 403 MB | — |
| `DAC_master_9472x900_prores.mov` | — | master, ProRes 422 HQ handover | 9472×900 | — | 18.7 GB | — |
| `cues.json` | — | timing for Max/MSP | — | — | 662 B | — |

All four independently verified: **5400 frames, 60/1 fps, 90.0 s** each.

Rendered in 3 resumed passes (~30 min total) plus ~4 min of encoding. The intermediate PNG
sequence (5400 frames, 54 GB) is at `/private/tmp/eoc-render/frames` — **not durable**; keep
it if you want to re-encode without re-rendering, otherwise the render reproduces it exactly.

## Reproducing the render

```bash
# 1. bake the Shanghai transect (once; reads TD_biomes/data/shanghai_growth)
#    Unity menu: Biomes > Bake Shanghai Transect
# 2. render the frame sequence (batchmode; see the session log for the harness)
# 3. encode
./tools/encode_dac.sh <frame-dir> <out-dir>
```

The render is deterministic: the sim runs at a fixed 60 Hz and the show is authored at a
**1:1 sim-step-to-frame ratio**, so frame index *is* sim step and 90 s is exactly 5400 of
both. Re-rendering reproduces the same frames, and the loop seam lands in the same place.

## Known constraints

- **9472 px is unusually wide for H.264.** It is 592 macroblocks; Level 6.2 permits 139 264
  MBs and ~1056 MBs of width, and libx264 encodes it (Level 6.0). But many **hardware**
  decoders cap at 4096 or 8192 px and will refuse the file. Confirm the venue's playback
  chain, or hand over the ProRes master and let them transcode.
- **Screen 1.2 has a centre cutout.** Measured, the cutout band carries **1.90×** the mean
  luminance of the rest of frame — because the mound overlay renders the built-up city right
  there. By the piece's own logic the cutout is safe (the centre is where *motion* is absent:
  agent-layer luminance there is 0.0011 against 0.022–0.033 at the edges). If brightness
  matters more than motion, `SimulationManager.moundOverlayStrength` (0.6) is the lever.
- **Sound is composed against `cues.json`, post-lock.** `fps == simRate`, so every cue is an
  exact integer frame: `start 0 · many 1200 · converge 3300 · oneBody 4500 · loop 5400`.

## What the piece does

The audience stands at the **still point the growth radiated away from**. Verified in the
source data: the venue-cluster centre pixel reads 0.3384 → 0.3393 built-up across 1975–2030 —
flat to three decimals — while the band that actually transformed sits 20–60 km away, which
lands out toward the frame edges, in shot but over the viewer's horizon in life.

Two registers say the same thing:

- **Space.** A 12-epoch GHSL built-up transect seeds Permeability as a *monotonic closure
  floor*, `perm = min(perm, 1 − builtUp)`. The city only ever closes; termite-built mounds
  survive it because they have already closed further.
- **Colour.** The 8 physarum hues converge from a wide spread to a single red. Measured: hue
  circular variance 0.556 → 0.000, mean hue exactly 0.0. Many organisms becoming one body —
  the same statement many settlements becoming one built mass makes.
