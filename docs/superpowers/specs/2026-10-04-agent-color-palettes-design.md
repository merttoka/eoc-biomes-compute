# Agent Colors: HSB + Palettes — Design

**Date:** 2026-10-04
**Status:** Approved (design); spec under review
**Branch:** `feat/agent-color-palettes`
**Files touched:** `11.0 Biomes/src/params/{Physarum,Boid,Termite}Params.cs`, `params/ColorPalette.cs`,
new `params/AgentColorPalette.cs` + `params/IAgentColorParams.cs`, new `core_math/AgentColor.cs` +
`core_math/PaletteAssign.cs`, `components/Sim/{Physarum,Boid,Termite}Sim.cs`,
`computes/{Physarum,Boid,Termite}Sim.compute`, `computes/includes/{physarum,boid,termite}_type_params.hlsl`,
`components/core/SimulationManager.cs`, new `components/utils/AgentPaletteCycler.cs`,
`components/network/{MidiFighterTwister,OSCMapping}.cs`, `Editor/ParamsEditor.cs`,
new `11.0 Biomes/assets/Palettes/**`, new `Assets/Tests/EditMode/{AgentColor,PaletteAssign,PalettePreset}Tests.cs`,
`MIDI_OSC.md`, docs.

## Problem

Fine-tuning agent colors is hard:

1. Each agent type carries only `hue` + `saturation`. Brightness is hardcoded in the render
   kernels: `hsb2rgb(h, s, 0.8 * val)` for Physarum and Boids, `hsb2rgb(h, s, val)` for Termites.
   There is no way to make one type darker or lighter than another.
2. The inspector edits colors as two raw float fields per type. Its swatch row uses
   `Color.HSVToRGB(h, s, 0.85)`, but the shader's `hsb2rgb` smooths each hue channel
   (`rgb*rgb*(3-2*rgb)`), so the swatch is not the rendered color.
3. "Randomize Colors" picks colors in CIE Lab with a lightness range, converts to RGB, then keeps
   only Unity-HSV hue and saturation. The Lightness range has no effect on brightness, and the hue
   lands slightly off because of the smooth-hue mismatch.
4. There is no way to reuse a scene's colors in another scene, or to switch looks live.

## Exhibited color sets (harvested)

Values read from the param assets each scene's sims use as `paramsSO`. Every set was exhibited at
the implicit legacy brightness (0.8 Physarum/Boid, 1.0 Termite).

| Show | Physarum | Boids | Termite |
|---|---|---|---|
| Metaesthetica / VISAP (10.0, `ParamsPhysarum_VISAP`) | 4: h .12 .29 .45 .55 @ s .5 | grey (10.0 remaps hue, s 0) — skipped | — |
| CURRENTS (11.1) | 4: .12/.5 .29/.5 .55/.5 .57/.75 | 4: .488/.795 .4/.5 .508/.54 .055/.42 | .62/.811 |
| SIGGRAPH show (11.2, `SHOWVERSION 1` + `20260711_200244`) | 8: hue 0, s .81 .627 .788 .291 .699 .423 .961, + .638/.024 | 4: .276/.15 .646/.472 .276/.157 .213/.409 | .567/.394 |
| SIGGRAPH DAC (11.2, `*_20260908_181119_DAC`) | 8: hue 0, s .827 0 .748 .181 .764 .496 .906, + .047/.827 | 4: .276/.15 .543/.472 .354/.157 .079/.409 | .606/.394 |
| Brave New Work (11.3) | 8: .109/.81 .841/.627 .544/.788 .33/.676 .6/.699 .041/.423 .955/.796 .82/.806 | = CURRENTS | = CURRENTS |

(h/s pairs; exact floats are taken from the YAML at build time, not from this table.)

## Design

### 1. Brightness per agent type

- `PhysarumAgentType`, `BoidAgentType`: `public float brightness = 0.8f;` `TermiteAgentType`:
  `public float brightness = 1f;`. These equal the old hardcoded constants, so every existing
  scene, snapshot, and interpolator waypoint renders identically with no asset migration. Unity
  fills a field missing from old YAML from its C# initializer (`PhysarumParams_20260607.asset` has
  no `firingSpeedMul` and is TestScene's live preset); a test asserts this for `brightness`.
- `ranges` gains `new("brightness", 0f, 1f)`; assets without the entry fall back to (0, 1) already.
- `GetValue`/`SetValue`, the sims' `SetParameter`/`SetParameterDelta`/`GetParameter` gain a
  `"brightness"` case.
- `"brightness"` is **appended** to each sim's `ModulatableParams`. MFT banks 0/1 index positions
  0–7 and stay unchanged. OSC (`/p_brightness_0` …), `ParameterRecorder` (events carry the name),
  `ParameterInterpolator` (unlisted toggle = on; old waypoints carry the legacy default) and
  `ParamSnapshotMixer` pick it up by name. Brightness lerps linearly; hue stays the only
  shortest-arc param.
- GPU: `brightness` is **appended** to the end of `PhysarumTypeParams` (44→48 B),
  `BoidTypeParams` (60→64 B), `TermiteTypeParams` (48→52 B) in both HLSL and the C#
  `[StructLayout(Sequential)]` mirror, so no existing field offset moves. Buffers are sized with
  `Marshal.SizeOf`.
- Render kernels: Physarum/Boid `0.8 * val` → `p.brightness * val`. Termite
  `hsb2rgb(float3(h, s*(1-white), p.brightness * baseB), baseB)`, white lerp unchanged.
  With the defaults, `0.8f * val` and `p.brightness * val` (`p.brightness == 0.8f`) are the same
  float multiply, and `1.0f * baseB == baseB`, so output is bit-identical.

Naming: `brightness`, not lightness/value. The shaders and the CA params already use that word.

### 2. Exact color math — `Biomes.Core/AgentColor`

Pure static functions, unit-tested:

- `Color ToRgb(float h, float s, float b)`: a line-for-line port of `color.hlsl` `hsb2rgb`:
  `p_k = clamp(|((6h + o_k) % 6) − 3| − 1, 0, 1)`, `o = (0, 4, 2)`; `q = p²(3 − 2p)`;
  `c = b·(1 − s + s·q)`.
- `(float h, float s, float b) FromRgb(Color c, float fallbackHue, float fallbackSat)`: the exact
  inverse. `b = max`, `s = (max − min)/max`. For the middle channel
  `q = (mid − min)/(max − min)`, the inverse smoothstep `p = 0.5 − sin(asin(1 − 2q)/3)`, then the
  sector from (max, mid) channel: R,G → `p`; G,R → `2 − p`; G,B → `2 + p`; B,G → `4 − p`;
  B,R → `4 + p`; R,B → `6 − p`; `h = h6/6` wrapped to [0, 1). Grey (`max == min`) keeps
  `fallbackHue`; black (`max == 0`) keeps both fallbacks. So picking grey or black never throws
  away the type's hue.
- Guarantee: `ToRgb(FromRgb(c)) == c` for any sRGB color, and `FromRgb(ToRgb(h,s,b)) == (h,s,b)`
  for s, b > 0 (tolerance 1e-4).

### 3. Palette asset — `AgentColorPalette`

```csharp
public enum AgentFamily { Any, Physarum, Boid, Termite }          // Biomes.Core
[Serializable] public class PaletteSwatch { public Color color = Color.white; public AgentFamily family; }
[CreateAssetMenu(menuName = "Biomes/Agent Color Palette")]
public class AgentColorPalette : ScriptableObject
{
    [TextArea] public string notes;                                 // provenance
    public List<PaletteSwatch> swatches = new();
}
```

- A swatch color is the rendered color at full trail intensity (sRGB, no HDR), so it can be edited
  with Unity's picker, eyedropper, or a pasted hex code.
- **Assignment rule** (`Biomes.Core/PaletteAssign`, tested): candidates are the swatches tagged with
  the sim's family; if none, the untagged (`Any`) ones; if none, all swatches. Type `i` takes
  `candidates[i % n]`. A show palette tags each color with the sim it came from, so applying it to
  that show's Physarum/Boid/Termite assets reproduces the show sim by sim. Curated palettes are
  untagged and serve every sim.
- `IAgentColorParams : IParamSet { AgentFamily Family { get; } }` on the three agent param classes
  is how the editor and the cycler learn a param set's family. CA params are not agent params and
  are untouched.
- Applying writes `FromRgb(swatch, currentHue, currentSat)` into hue/saturation/brightness.

### 4. Presets — `11.0 Biomes/assets/Palettes/`

- `Shows/`: `Metaesthetica VISAP`, `CURRENTS`, `SIGGRAPH`, `SIGGRAPH DAC`, `Brave New Work`. Swatches
  are `ToRgb(h, s, legacyBrightness)` of each source type, tagged per sim, with `notes` naming the
  source assets. Brave New Work's Boid/Termite swatches repeat CURRENTS (the scene reuses those
  assets).
- `Curated/` (untagged, sRGB hex):
  - **Bioluminescent** `#00F5D4 #00BBF9 #4361EE #7209B7 #9BF6FF #06D6A0`
  - **Ember** `#FFBA08 #F48C06 #E85D04 #DC2F02 #9D0208`
  - **Lichen** `#DAD7CD #A3B18A #588157 #3A5A40 #C9A227 #8A6F3E`
  - **Monochrome** `#FFFFFF #D9D9D9 #B3B3B3 #8C8C8C`
  - **Viridis** `#440154 #46327E #365C8D #277F8E #1FA187 #4AC16D #A0DA39 #FDE725`
- Assets and `.meta` files are authored as YAML (GUIDs minted, as in the CA session). A test checks
  each show palette against its source assets.

### 5. Params inspector — "Colors" section

Drawn by `ParamsEditorGUI` above the `types` list, for any `IAgentColorParams` (the on-disk asset
in Edit mode, or the runtime clone in Play):

- **Palette row**: `AgentColorPalette` object field (selection kept in `EditorPrefs` by GUID, shared
  by all param inspectors) + swatch strip (swatches tagged for this family are marked) +
  **Apply to N types**.
- **Per type**: `ColorField` (no alpha, no HDR, eyedropper) showing `ToRgb(h, s, b)`; edits write
  back through `FromRgb`. Below it, H / S / B sliders (0–1) for exact numbers, and a **▾** button
  that opens a swatch popup (`PopupWindowContent`) to assign one palette color to that type.
- **Save as new palette…**: creates an `AgentColorPalette` from the current type colors, tagged
  with this family. **Store in palette**: replaces the selected palette's swatches of this family
  (appends if it has none), which is how a whole show is captured sim by sim.
- The old "Palette Preview" row (fixed 0.85) is removed; the per-type color fields replace it.
- **Randomize Colors** writes hue, saturation, *and* brightness: `ColorPalette.GenerateHS` becomes
  `GenerateHSB`, which converts each Lab color with `AgentColor.FromRgb`. The existing Lightness
  range now sets brightness, and the generated colors render as chosen. The CA params' randomize
  keeps setting hue + saturation only.
- All edits go through `Undo.RecordObject` + `SetDirty`.

### 6. Live palette cycling — `AgentPaletteCycler`

A `[Serializable]` plain class in its own file, held by `SimulationManager` as
`[Header("Palettes")] public AgentPaletteCycler palettes`. Living on the manager means no new scene
wiring: MFT and OSC already reference it.

- Fields: `List<AgentColorPalette> palettes`, `float fadeSeconds = 2f`. The cycle is
  `[Preset, palettes[0], …, palettes[n-1]]`, wrapping. **Preset** = each sim's authored colors,
  read from its `PresetParamSet`. Active index is runtime-only and starts at Preset.
- `SimulationManager`: `[Button] NextPalette()`, `[Button] PreviousPalette()`,
  `SelectPalette(int index)` (−1 = Preset). Each logs the active palette name.
- **Fade**: on select, capture each running agent sim's live (h, s, b) per type, then over
  `fadeSeconds` of unscaled time lerp to the target. Hue takes the shortest arc
  (`ParameterInterpolator.LerpHue01`); a grey target keeps the current hue. The tick runs from
  `LateUpdate`. `fadeSeconds == 0`, or Edit mode, applies instantly.
- **Persistence across resets**: `ConfigureAndReset` (the single funnel for Reset, ResetSimsOnly,
  per-family resets, StartSim and timeline starts) calls `palettes.Reimpose(sim)` after
  `sim.Reset()`, writing the palette's current value (mid-fade or final) into the fresh clone.
  A respawn never snaps colors back. Cycling to Preset restores authored colors.
- Targets every sim in `simulations` whose `LiveParamSet` is `IAgentColorParams`. Stopped sims are
  skipped and pick the palette up through `Reimpose` when started.
- **MFT**: `SideButtonAction.NextPalette`, `PreviousPalette`, appended to the enum (serialized
  ints of existing scenes stay valid). Existing scenes keep their side-button assignments; map one
  in the inspector.
- **OSC**: `/palette_next`, `/palette_prev`, `/palette <int>` (−1 = Preset), queued to the main
  thread like the reset commands.

### 7. MFT bank 2 layout

Rows per type column: **hue, saturation, brightness, diffuseRate** (was hue, saturation,
diffuseRate, depositAmount). `depositAmount` stays on bank 1 for all three sims (index 4–7).
Biome cross-field column unchanged.

## Interactions and caveats

- A `ParameterInterpolator` with hue/saturation/brightness toggles on keeps writing colors toward
  its waypoints and will override a palette. Turn those toggles off where a palette should own
  color.
- A palette fade overwrites MFT/OSC hue/sat/brightness edits on the faded types until it ends (2 s
  default).
- Unity's color picker shows standard HSV numbers. Saturation and value match the params exactly;
  the hue number differs slightly (smooth vs. linear hue curve), but the color is exact.
- Brightness above the legacy default makes a type brighter than any exhibited look; the range is
  0–1 like HSB.

## Not doing

- HSL lightness (white at L = 1). The engine is HSB end to end.
- Palettes for CA sims (single hue + `hueSpread`; not agents).
- Palette cues on `SimTimeline` / as interpolator waypoints, MFT LED palette preview,
  `Resources` auto-discovery of palettes.
- Changing the side-button assignments of existing scenes.

## Testing

- **EditMode, `Biomes.Core`**: `AgentColor` reference values at primaries, secondaries and
  15°/45° hues against hand-computed shader output; `FromRgb∘ToRgb` over an (h, s, b) grid;
  `ToRgb∘FromRgb` over random RGB; grey/black fallbacks; `PaletteAssign` tagged → untagged → all
  fallback and wrap.
- **EditMode, assets** (via `AssetDatabase` + `SerializedObject`, since the test assembly cannot
  reference Assembly-CSharp): every show palette reproduces its source assets' h/s at legacy
  brightness (grey types: s and b only); an old param asset loads `brightness` = its legacy default.
- **Compile gates**: batchmode import with 0 errors (APFS clone if the Editor holds the project),
  Metal compile of the three sim computes, EditMode suite green.
- **Manual** (author): inspector UX, MFT side-button cycling, OSC `/palette_*`.

## Risks

- **GPU struct mismatch**: HLSL and C# structs must change together. Append-only and both sides
  in one commit; the byte-count comments are updated.
- **Missing-field default**: if Unity did not apply initializers to old YAML, old assets would load
  `brightness = 0` (black agents). Covered by the asset test before anything ships.
- **Inspector width**: 8 types × (color row + H/S/B row) is long; acceptable for now.
