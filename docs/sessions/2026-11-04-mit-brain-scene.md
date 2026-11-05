---
status: closed
date: 2026-11-04
tags: [session, mit-brain, organoid, firing, recording, palette, data]
related: [[../ARCHITECTURE]], [[../adr/0018-presentation-clock-frame-locked-takes]], [[../adr/0019-unreleased-datasets-stay-local]]
---
# 11.5 MIT BRAIN: a two-condition organoid scene and frame-locked takes

Branch `feat/mit-brain-ketamine`. The work ran 2026-10-30 to 11-01; the docs pass, data strip and
merge landed 11-04. Brief: run the 11.4 ecology on a new organoid recording with a baseline and a
ketamine condition, so the difference in firing shapes the picture, and record it in sync with the data.

## Shipped
- **`tools/spikes_npy_to_f16.py`:** lab timeseries CSVs (`rates_norm` as-is) or a raw `.npy` raster →
  TFR1 blob, sidecar with segments and burst onsets, a synthetic MDS layout or `--xy` real positions,
  and backbone labels copied into the positions CSV.
- **11.5 MIT BRAIN:** a clone of 11.4 with fresh GUIDs at 3840×3024 (80:63), 69 neurons × 36000
  frames, looping over 600 s, with its own materials, params and palettes.
- **Backbone scaffold:** `NeuronFiringSource` parses `backbone_labels` and exposes `BackboneCoherence`.
  `BiomeInjector` (default off, on in 11.5) feeds Nutrient at backbone nodes and along a Prim's MST
  between them, with fast attack and slow release. A full `Reset` clears its strength; before that
  fix it re-grew the spine on a fresh biome.
- **Frame-locked takes:** `PresentationClock` and `LoopTakeRecorder`
  ([[../adr/0018-presentation-clock-frame-locked-takes|ADR-0018]]).
- **`NeuronFiringCues`:** palette crossfades and optional blackout resets at blob frames. A seek snaps
  the palette to the condition it landed in. 11.5: Preset at frame 0, `MIT Brain CURRENTS` at 18000,
  resets off.
- **`NeuronFiringParamMorph`:** eases live params toward other presets from a blob frame on. Its weight
  is a pure function of the playhead. The component is disabled in 11.5.
- **Primordial and 11.4 follow-ups** came in on the same branch: 8-type Physarum, the sun injector
  period, palettes that tint walls, per-species population readouts. See the commit log.

## Decided
- **Same params in both conditions.** The param morph into CURRENTS presets was tried and is kept for
  later. In 11.5 the firing drives the visual change, and only the colours differ across the boundary.
- **Palette copies per scene.** 11.5 uses its own palette assets, so show palettes stay untouched.
- **Synthetic unit layout.** The recording has no unit coordinates, so positions come from MDS on
  co-firing, fitted to the organoid footprint. The filename says `SYNTHETIC` on purpose.
- **Keep the dataset local.** Public release isn't confirmed, so the blob, sidecar and positions CSV
  stay out of git, and measured results and researcher names stay out of the text. The unpushed branch
  was rewritten so no pushed commit holds them, and the blob was renamed to `organoid_ketamine.f16`
  ([[../adr/0019-unreleased-datasets-stay-local|ADR-0019]]).

## Verified
- EditMode suite 188/188 in batch mode, before and after the history rewrite, with no compile errors.
- The rewritten range has no dataset paths and no added LFS pointers, and a text scan of every patch
  and commit message for the names and results found nothing.
- Not run after the rename: 11.5 in Play. The new blob path was only checked against the file on disk.
- No test covers `NeuronFiringCues`, `NeuronFiringParamMorph`, the scaffold or `PresentationClock`.

## Open / next session
1. Record the full 10-minute take at native resolution. Only excerpts and side-by-sides exist so far,
   so the loop seam is unproven.
2. Confirm public release of the dataset. If it clears, follow ADR-0019's consequences.
3. Replace the synthetic layout if real unit coordinates arrive (`--xy`).
4. Add tests for the cue crossing and wrap logic, the morph weight and the scaffold envelope.
