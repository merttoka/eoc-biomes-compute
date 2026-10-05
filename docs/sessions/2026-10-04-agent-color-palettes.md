---
status: closed
date: 2026-10-04
tags: [session, colors, palettes, params, inspector, mft, osc, biomes]
related: [[../superpowers/specs/2026-10-04-agent-color-palettes-design]], [[../superpowers/plans/2026-10-04-agent-color-palettes]], [[../ARCHITECTURE]], [[2026-10-04-measured-followups]]
---
# Agent colors: HSB + palettes

Branch `feat/agent-color-palettes` off `main` @ `8261530`. Ask: assign agent colors as hue /
saturation / brightness, pick them from palettes, ship presets including the exhibited scenes'
colors — fine-tuning had become hard. Spec + plan:
[[../superpowers/specs/2026-10-04-agent-color-palettes-design]],
[[../superpowers/plans/2026-10-04-agent-color-palettes]].

## Shipped
- `b21c6a2` — `Biomes.Core/AgentColor`: exact C# port of `color.hlsl` `hsb2rgb` (smoothstepped
  hue channels, so it is *not* Unity HSV) + exact inverse; grey/black keep a fallback hue/sat.
- `1d3972b` — `Biomes.Core/PaletteAssign` + `AgentFamily` + `PaletteSwatch`: family-tagged
  swatches, type *i* ← *i*-th swatch for its family → untagged → all (wrapping); `StoreFamily`,
  `StepCycle` (Preset = −1).
- `f4fd37b` — per-type `brightness` in Physarum/Boid/Termite params, sims (`ModulatableParams`
  appended, so MFT banks 0/1, OSC `/<p>_brightness_<i>`, recorder, interpolator, snapshot
  mixer get it by name), GPU structs (appended last: 48/64/52 B) and render kernels
  (`p.brightness * val`; termite `p.brightness * baseB`). Defaults 0.8/0.8/1.0 = the old constants.
- `2592673` — MFT soft bank 2 rows: hue, saturation, brightness, diffuseRate (depositAmount stays
  on bank 1 for every sim).
- `72f06cb` — `AgentColorPalette` assets + `IAgentColorParams` (family on the three agent param
  sets) + 10 presets in `11.0 Biomes/assets/Palettes/`: `Shows/` Metaesthetica VISAP, CURRENTS,
  SIGGRAPH, SIGGRAPH DAC, Brave New Work (swatches = `ToRgb(h, s, legacy b)` of each source type,
  tagged per sim, source assets named in `notes`); `Curated/` Bioluminescent, Ember, Lichen,
  Monochrome, Viridis (untagged).
- `4d30b7e` — params inspector **Colors** section (`Editor/AgentColorsGUI`): per-type color field
  (exact rendered color; picker/eyedropper/hex) + H/S/B sliders, palette field (EditorPrefs,
  shared by all agent inspectors) with family-marked swatch strip, Apply, per-type ▾ swatch popup,
  Store in palette, Save as new palette. Old fixed-0.85 preview removed. `ColorPalette.GenerateHS`
  → `GenerateHSB`: Randomize Colors now sets brightness from the Lab lightness range.
- `a32e05e` — `AgentPaletteCycler` on `SimulationManager.paletteCycle`: Preset → palettes cycle,
  smoothstepped fades (unscaled time, shortest-arc hue), `Reimpose` from `ConfigureAndReset` so
  every reset/start keeps the palette; MFT `NextPalette`/`PreviousPalette`; OSC `/palette_next`,
  `/palette_prev`, `/palette <i>`; `MIDI_OSC.md` updated.
- `97ee722` — fix from the live probe: a settled fade writes the exact target (the hue lerp's
  wrap left red at hue 1.0 — same color, wrong end for a knob's soft takeover).

- Final review fix — palette/inspector colors are **on-screen** (sRGB) colors: the project is
  Linear, kernels write linear light and the display gamma-encodes it, so the first cut showed a
  picked `#8C8C8C` as ~`#C3C3C3`. `AgentColor.ToDisplay`/`FromDisplay` now sit at every UI/palette
  boundary (swatches, color field, popup, Store/Apply, `GenerateHSB`); show palettes regenerated
  as `ToDisplay(h, s, legacy b)`; curated hex values unchanged (they always meant screen colors).
  Exhibited looks are unaffected (same params round-trip). Spec drift fixed (`paletteCycle`,
  `Select(…, instant)`, file list).

## Verified
- EditMode 78 → **143/143** after the final-review fix (139 before it) in an APFS bench clone (headless; the author's Editor had the real
  project open). New: `AgentColorTests` 19, `PaletteAssignTests` 8, `AgentRenderKernelTests` 6,
  `AgentColorAssetTests` 3, `PalettePresetTests` 14, `RandomizeColorsTests` 2,
  `AgentPaletteCyclerTests` 12, `AgentColorPaletteTests` 1.
- `AgentRenderKernelTests` dispatch the real `RenderKernel`s on Metal and match `AgentColor.ToRgb`
  within 1e-4 (incl. termite firing → white); the C# upload structs' `Marshal.OffsetOf` matches the
  same layout table. Old snapshots without a `brightness` line load 0.8/0.8/1.0.
- Show palettes reproduce their scenes' exhibited h/s at legacy brightness within 1e-4.
- Cycler tests were mutation-checked: no-reimpose, linear hue, no zero-fade guard and
  capture-stopped-sims each fail exactly the intended tests.
- Metal gate (bench Editor): 10/10 computes, 0 errors, 0 warnings.
- Live probe, TestScene in Play: preset (0.120,0.500,0.800) → fade → SIGGRAPH (0.000,0.810,0.800);
  `ResetPhysarum` keeps it; `/palette 99` clamps to the last palette; zero `fadeSeconds` mid-fade
  settles; removing the active palette mid-show → "(missing palette)", no exception; reset at
  fade start keeps the fade value, not the preset; Preset fades back to (0.120,0.500,0.800).

## Decided
- HSB, not HSL: the engine is HSB end to end; `brightness` is the HSB value at full trail.
- Append-only GPU struct + `ModulatableParams` changes: no existing offset or MFT bank 0/1 slot moves.
- Palettes store rendered sRGB colors (picker/hex friendly); the exact inverse makes them lossless
  except the hue of greys, which keeps the type's own.
- One show palette per scene with per-sim tags, instead of one palette per sim per scene.
- Live palette state lives on `SimulationManager` (no scene wiring) and is re-imposed through the
  `ConfigureAndReset` funnel; Preset is a cycle stop.
- `AgentPaletteCycler.Select(…, instant)` takes the instant flag from the manager
  (`!Application.isPlaying`) so fades are testable in EditMode.

## Open / next session
1. **Map a side button in the show scenes** (e.g. R3 RandomizeColors → NextPalette) and fill
   `SimulationManager` → Agent Palettes → `Palettes` per scene; existing scenes keep their mappings.
2. **Interpolators vs palettes**: a `ParameterInterpolator` with hue/saturation/brightness toggles on
   overrides a palette (DAC takes run interpolator groups) — turn those toggles off where a palette
   should own color.
3. Eyeball the inspector Colors section and the MFT bank 2 knobs on device (IMGUI and hardware are
   not covered by tests).
4. Pre-existing: adding a sim component in the Editor runs the sim's `Reset()` (Unity's `Reset`
   message shares the name) and throws when unconfigured — harmless, noisy.
