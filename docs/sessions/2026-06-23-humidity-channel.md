---
status: closed
date: 2026-06-23
tags: [session, unity, biome, channel, humidity, shader]
related: [[../../Assets/Workspace/11.0 Biomes/docs/INTEGRATION_DESIGN]], [[../ARCHITECTURE]]
---
# Humidity biome channel (11→12)

Shipped the **Humidity** channel — the INTEGRATION_DESIGN Tier-2 / build-sequence #5 move,
the research PDF's #1 missing layer. It's the second channel growth after Dispersal (which
took the field 10→11); Humidity takes it 11→12. Intended use: 11.2 SIGGRAPH scene.

## Shipped
- **Channel definition** (`BiomeFieldConfig.cs`): `BiomeChannel.Humidity = 11`, `Count → 12`,
  name `"Humidity"`. Default `FieldChannelSettings` row: `diffuseRate 0.97` (moisture spreads
  readily), `decayRate 0.001` (slow baseline drying), `advectedByFlow true` (airflow carries
  it), `initialValue 0.5` (ambient baseline), `relaxRate 0.01` (renewable — refills toward
  baseline, so it's a depletable-but-renewing resource). New cross-field knob
  `temperatureToEvaporation = 0.05f` (range 0–0.2).
- **Field dynamics** (`Biome.compute`): `#define CH_HUMIDITY 11`, `CH_COUNT → 12`, new
  `tempToEvaporation` uniform. `InteractFieldsKernel` now evaporates Humidity off the hot
  zones: `humidity -= tempToEvaporation·max(0, temp−0.5)`, clamped and written back (added to
  the existing nutrient/waste/perm write-back; `CopyAllChannels` already carries it through).
  Heat leaves a drying wake; the steep `|∇Humidity|` edge is the termite build cue. Debug
  color: teal `(0.1, 0.6, 0.9)`.
- **Uniform wiring** (`Biome.cs`): `s_TempToEvaporationID` + `SetFloat` in the interact pass
  setup. Everything else (field-array alloc, settings/relax buffers, debug grid, PNG export)
  already keys off `BiomeChannel.Count` — no other code touch.
- **Sender** (`ExternalTextureSender.cs`): appended `"Humidity"` to `ChannelNames` (kept in
  sync with `BiomeChannel.Names`).
- **Assets**: added the 12th channel row + `temperatureToEvaporation: 0.05` to both
  `BiomeFieldConfig_Homeostatic.asset` (11.2 SIGGRAPH **and** 11.1 CURRENTS) — the loader fills
  channels from the asset's list (`for i < Count && i < channels.Count`), so an 11-row asset
  would leave Humidity zero-filled (no diffusion/relax). `old/` configs left as-is.
- **Docs**: README (12-channel field + Humidity bullet, roadmap), ARCHITECTURE §3.3 (12
  channels, three sync'd hardcode sites not four, evaporation in the interact step),
  INTEGRATION_DESIGN (Tier 2 + build-sequence #5 marked shipped), this session, INDEX.

## Decided
- Evaporation lives in `InteractFieldsKernel` (alongside waste→nutrient and temp→permeability)
  rather than a new pass — keeps the ping-pong chain length unchanged, free on the decimated
  320×180 grid.
- Humidity is **agent-coupled per-scene, not in code.** Consumption and the `|∇Humidity|`
  build cue are `UmweltMapping` reads/writes the artist wires in the show scene (Tier-0 asset
  edits), matching how the design framed it — the layer ships the field; the scene wires the
  agents.
- Channel-name sync collapsed to **one** source of truth during the post-impl `/simplify`
  pass: `Biome.cs` and `ExternalTextureSender.cs` each carried a hand-synced `ChannelNames`
  copy; the `Biome.cs` one had silently desynced (still 11 entries → latent
  `IndexOutOfRangeException` in the debug grid / PNG export at `Count = 12`). Both now alias
  `BiomeChannel.Names`, so adding a channel can't desync them again. Remaining hardcoded
  count sites: `BiomeChannel.Count/Names` + `Biome.compute` `CH_COUNT` + the asset lists.

## Open / next session
1. Wire the SIGGRAPH `UmweltMapping`s: Physarum/Termite Humidity `+Chemotaxis` (seek moisture)
   and a consumption write; termite build cue off `|∇Humidity|`.
2. Tune `temperatureToEvaporation` against the diurnal-sun work (not yet shipped) so the
   drying wake actually travels rather than pinning a static dry disk.
3. Validate in Unity (compile, debug-grid shows the teal channel, evaporation reads visibly).
