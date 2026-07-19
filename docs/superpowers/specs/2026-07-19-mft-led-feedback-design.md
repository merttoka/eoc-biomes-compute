# MFT LED Feedback: Type Gradients + Bank Flash — Design

**Date:** 2026-07-19
**Status:** Approved
**File touched:** `Assets/Workspace/11.0 Biomes/src/components/network/MidiFighterTwister.cs` (+ `src/Editor/MidiFighterTwisterEditor.cs` inspector fields)

## Problem

Looking at the physical MFT gives no clue which param a knob controls:

1. All columns of one sim class share one flat color (8 physarum columns = 8 identical blue columns).
2. Soft bank (Core/Secondary/Visual/Umwelt) has **no** physical representation; HW bank only via tiny native side LEDs. `_bankColors[]` is built but never sent.

Setup: ColumnPerType layout, Physarum (8 types) + Boid (4) + Termite (N) active → 12+ columns over 3+ HW banks.

## Design

### 1. Per-family hue gradients

- Serialized per-family CC-value pair, inspector-tunable (MFT hue wheel is nonlinear — verified anchors: red=1, blue=33, cyan=40, green=43, yellow=65, orange=83, purple=110):
  - `physarumHueRange = (20, 40)` deep blue → cyan-ish
  - `boidHueRange = (78, 98)` orange → red-ish
  - `termiteHueRange = (57, 70)` yellow band
- Column color = `RoundToInt(Lerp(start, end, typeIndex / max(1, typeCount-1)))`.
  - `typeCount == 1` → midpoint (≈ current family anchor; single-type sims look unchanged).
- Applied in `SendAllLEDs()` for `BindingTarget.SimParam`; replaces the flat `RGB_BLUE/RGB_ORANGE/RGB_YELLOW` switch. Needs `typeCount` per sim — reuse `GetTypeCount()`.
- Non-SimParam targets unchanged: Biome=green(43), Umwelt=cyan(40), Global=purple(110).
- Column order untouched — `CollectTypeColumns()` already emits sim-by-sim, type-by-type; gradient makes the existing order visible.

### 2. Bank flash on switch

- On `SetSoftBank()` / `SetHwBank()`: overlay for `bankFlashDuration` (serialized, default 0.7s):
  - Top row (enc 0–3): `softBank+1` knobs lit in `_bankColors[softBank]` (finally used), rest off.
  - Bottom row (enc 12–15): `hwBank+1` knobs lit white(127), rest off.
  - Middle rows + all ring LEDs off during flash.
- Mechanism: `_flashUntil = Time.unscaledTime + duration`; existing `Update()` checks expiry → `SendAllLEDs()` restores. No coroutine.
- `SendEncoderRingPositions()` early-outs while flash active.
- Encoder input during flash still works (bindings unaffected); flash is display-only.

### 3. Brightness bump

- Bound encoders: send anim CC full RGB brightness (value 47) instead of `ANIM_NONE`.
- Unbound: RGB off + anim none (as today). Dead slots read clearly dead.
- Note: per DJTT manual, anim channel 17–47 = RGB brightness (47=100%); existing `ANIM_STROBE=47`/`ANIM_PULSE=55` constants are unused and appear mislabeled (manual: strobe=1–8, pulse=9–16). Fix constants; verify on device.

## Not doing

- Persistent bank tint (muddies family gradient).
- Reserved indicator knob (loses a param slot).
- Per-family hand-picked color lookup tables (tuning burden; interpolation + tunable endpoints suffices).
- No changes to bindings, ordering, OSC, soft-takeover, or push actions.

## Testing

- Edit-mode: `logMidi` + `LogBindingTable` already prints bindings; add computed color to table output for verification without device.
- On-device eyeball pass to tune the three hue ranges (wheel nonlinearity makes defaults approximate).
- Verify flash restores correct colors after 0.7s on both bank types; verify ring updates resume.

## Risks

- Hue wheel nonlinearity: gradient steps may bunch perceptually → endpoints are serialized precisely so this is tunable live.
- Physarum cyan end (44) nears Umwelt cyan (40)/Biome green (43) — they never share a soft bank page except bank 2 col 3 (biome col); tune `physarumHueRange` end down if ambiguous on device.
