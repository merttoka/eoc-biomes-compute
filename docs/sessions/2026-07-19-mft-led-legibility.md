---
status: closed
date: 2026-07-19
tags: [session, mft, midi, led]
related: [[../../docs/superpowers/specs/2026-07-19-mft-led-feedback-design]]
---
# MFT LED legibility: per-type hue gradients + bank flash

## Shipped
- `MidiFighterTwister.cs` — SimParam knob color now per-type hue gradient (family CC range lerped by typeIndex; inspector-tunable: physarum 20→40 blue→cyan, boid 78→98 orange→red, termite 57→70 yellow). Single-type sims = range midpoint (legacy anchor). Fixed colors unchanged (biome 43 / umwelt 40 / global 110).
- Bank-switch flash: 0.7s overlay on any soft/HW bank change — top row counts soft bank in `_bankColors` identity color (first real use of that array), bottom row counts HW bank in white; rings off, timestamp restore in `Update()`, `SendEncoderRingPositions` gated by `FlashActive`.
- Full RGB brightness (anim CC 47) on bound encoders; fixed mislabeled anim constants (`ANIM_STROBE=47` etc. → DJTT manual: 17-47 = brightness, 47 = 100%).
- `LogBindingTable` cells prefix computed color CC (no-device verification).
- Docs: `MIDI_OSC.md` LED Feedback section, README bullet, spec + plan under `docs/superpowers/`.
- Merged to main `7be27c2` (branch `worktree-mft-led-feedback`, 0-error `dotnet build Assembly-CSharp.csproj` per commit).
- Follow-up (same day): bank 2 biome cross-field column moved HW1 col 3 → HW4 col 3; type columns now contiguous from col 0 (8-physarum block no longer split around the biome column).
- Follow-up 2 (device test): hue gradients confusing on hardware → replaced with **per-type brightness ramp** (flat family hue, anim-channel brightness `typeBrightnessMid` 32 → 47 across type index; single-type = max). Six hue fields removed, one brightness field added.

## Decided
- Hue gradient over brightness ramp / screen-hue mirror (family recognizable, order visible; dim knobs bad on stage).
- Flash-on-switch over persistent tint or sacrificed indicator knob (grid colors stay clean, no lost slot).
- physarumHueEnd default 40 not 44 — 44 collides w/ biome green 43 on soft bank 2 (review finding).
- Worktree build trick: symlink `Library/` + copy gitignored `*.csproj` from main checkout → `dotnet build` works in worktree.

## Open / next session
1. On-device hue tuning — MFT wheel nonlinear, defaults are guesses; tweak 6 range fields in inspector live.
2. Verify anim CC 47 = full brightness (not strobe) on actual firmware; if strobe, drop to manual's brightness range.
3. Edit-mode flash restore lags until next editor tick (`[ExecuteInEditMode]` Update cadence) — fine in play mode; revisit if annoying.
4. Optional: mirror gradient in editor inspector grid swatches (currently flat family colors).
