---
status: living
date: 2026-07-15
tags: [meta, roadmap, backlog]
related: [[../Assets/Workspace/11.0 Biomes/docs/INTEGRATION_DESIGN]], [[INDEX]]
---
# Roadmap & Backlog

Living status of the biome engine backlog. **Verified against code 2026-07-13** (three-agent
audit). Build-sequence tiers map to [[../Assets/Workspace/11.0 Biomes/docs/INTEGRATION_DESIGN|INTEGRATION_DESIGN]].
Guiding lesson from the audit: *configured ≠ executing* — several features are declared,
inspector-visible, even MIDI-mapped, yet read by no kernel (see Dead Code below).

**Scope:** this file owns the *code* backlog. Non-code work (strategy, admin, website,
cross-project) lives in Todoist. Cross-link between the two; never copy items either direction.

## ✅ Shipped — Tier-1 "make it alive" complete
Q10 ignition + decay sinks · relax-to-baseline · Humidity channel (+ build cue wired both
shows) · **diurnal sun** (procedural injector, neuron-playhead-phased) · fixed-timestep sim ·
clear-in-place stream-safe resets · **resolution-independent params** (pixel-unit spatial +
trail-density scale by `rezY/referenceHeight` on Reset, grounded at 2160 — no-op at reference,
activates on any resolution change; per-scene toggles) · **permeability mounds** (termites build
persistent walls that partition the field into habitats — agent-authored ch7 + firing-gated build
kernel + habitat-band confinement + `ResetTermites` melt + composite overlay;
[[adr/0010-permeability-agent-built-topography|ADR-0010]]).

**Agent colors (2026-10-04)** — per-type HSB with `brightness` (legacy defaults, bit-identical
render), exact `hsb2rgb` inverse for the inspector, family-tagged `AgentColorPalette` presets
(5 exhibited shows + 5 curated), live palette cycling with fades that survive resets
([[sessions/2026-10-04-agent-color-palettes|session]]).

**Mound detail (2026-10-06)** — `SimulationManager.moundDetail` paints the termite walls from a
render-only sim-resolution mirror of ch7 instead of the coarse biome grid (11.3: ~8.7 px/cell
blocks → thin paths); default off
([[adr/0016-mound-detail-render-mirror|ADR-0016]], [[sessions/2026-10-06-mound-detail-layer|session]]).

**Primordial particle system (2026-10-20)** — 4th agent sim: Schmickl's PPS motion law on the GPU in
paper space units (density-derived population), creatures with membranes/nuclei from a
density-weighted trail bank, coupled through turn noise (perception A) and a clamped drift (R − B);
**11.4 PPS Dev Scene** adds it to 11.3's three sims (Nutrient food web: all three hunters graze it; Pheromone0 predation loop, local
firing playback) ([[adr/0017-primordial-sim-paper-units-noise-coupling|ADR-0017]],
[[sessions/2026-10-20-primordial-sim|session]]).

**11.5 MIT BRAIN (2026-11-01)** — 11.4's ecology on a two-condition organoid recording (local-only
dataset, [[adr/0019-unreleased-datasets-stay-local|ADR-0019]]). Executing in the scene: backbone
scaffold (`BiomeInjector`), palette cues (`NeuronFiringCues`), `PresentationClock`. Built but off in
11.5: `NeuronFiringParamMorph` (component disabled) and cue blackout resets. `LoopTakeRecorder` records
frame-locked takes ([[adr/0018-presentation-clock-frame-locked-takes|ADR-0018]],
[[sessions/2026-11-04-mit-brain-scene|session]]).

**MFT hover knob and inspector scroll (2026-11-05)** — on soft bank 3 / HW bank 4, one knob moves the numeric
inspector field under the mouse; sideways trackpad scroll does the same in any scene with no MFT. Keyboard Shift
or the MFT shift layer gives 1/10 speed on every knob, and press-and-turn moves a param across agent types.
Editor only for the first two, device-tested on macOS
([[adr/0020-mft-hover-knob-imgui-debugger-data|ADR-0020]], [[adr/0021-sideways-scroll-debugger-lease|ADR-0021]],
[[sessions/2026-11-05-inspector-scroll-fine-control|session]]).

## 🔧 In design (specs forthcoming)
- _Permeability mounds **shipped 2026-07-15** (see Shipped /
  [[adr/0010-permeability-agent-built-topography|ADR-0010]] /
  [[sessions/2026-07-15-permeability-mounds|session]])._ Deferred follow-on layers: curvature
  (Laplacian) build/dig cue, humidity-scaffold, optional per-run seed bootstrap, discrete build
  types. Open: legacy umwelt ch7 chemotaxis/speed reads now overlap the habitat bands (Physarum/
  Boid contradict) — decide whether to strip them.

## 📋 Backlog — verified status

### Partial (finish the half-wired)
- **B-channel predator/prey** 🟡 — Physarum↔Termite works (Physarum flees Termite's Pheromone_2
  `avoid +1.2` + scavenges Waste). **Boid's** waste-avoid `−1` is neutralized (`avoidance =
  max(0,·)` floors negatives → 0); **Termite** has no avoid read. To finish: give Boid a
  positive-weight avoid on a real threat channel; optionally a Termite avoid.
- **Wall healing stalls** 🟡 — ch7's relax (0.0005/step toward 0.9) is below RHalf resolution
  near open ground: walls stop healing at ~0.44 wall-ness (float16 simulation, round to nearest;
  higher on GPUs that truncate half stores, as this Mac's does) and weaker walls never heal, so
  mounds persist until `ResetTermites`. Decide whether that is the look; if not, relax in wall
  space or store ch7 at higher precision. The mound detail heals with the field either way
  ([[adr/0016-mound-detail-render-mirror|ADR-0016]]).
- **11.5 MIT BRAIN** 🟡 — the full 10-minute native take has not been recorded, so the loop seam is
  unproven; the unit layout is synthetic until real coordinates exist (`spikes_npy_to_f16.py --xy`);
  the scene needs its dataset restored locally to play. No tests yet for cue crossing/wrap, the morph
  weight, the scaffold envelope or `PresentationClock`.
- **MFT knob review fixes** 🟡 — ring end-stop handling, press state on device loss and bank change, per-bank
  fine-tune and flash expiry were applied after the last device test
  ([[sessions/2026-11-05-inspector-scroll-fine-control|session]]). They need a pass on hardware.

### Not started (features)
- **Agent mortality** — `enableDeath`/thresholds/`corpseWasteAmount` declared, **zero executing
  code**. Biggest aesthetic (bloom/collapse); also revives Waste (corpse drop 0.5 ≈ 50× the
  current per-step deposits).
- **Waste utilization without mortality** — Termite is the biggest depositor but reads Waste
  nowhere; give it a waste read + amplify the Q10 fertility wave the Physarum food-read chases.
- **Permeability-as-topography** — the agent-authored permeability field **shipped**
  ([[adr/0010-permeability-agent-built-topography|ADR-0010]]); the *Laplacian-curvature*
  build/dig cue itself is still deferred (build is firing-gated probabilistic, not curvature-driven).
- **Trails → separate overlaid composite channel** — decouple termite trail thickness from the
  *additive* main composite (model on the mound overlay painted at the end of `CompositeRenderKernel`; the firing-ring
  overlay this was first modelled on is gone — [[adr/0015-remove-firing-ring-overlay|ADR-0015]]).
- **Injector click-to-place + texture-valued source** — no CustomEditor/OnSceneGUI; `Source.value`
  is scalar-only.
- **Half-rez trail structure tensor** — cut the `trailAnisotropy > 0` cost ~3–4× by evaluating
  the (already low-frequency) tensor at half rez with double-angle axis encoding; designed, not
  built — measure 11.3's knob-on GPU ms first
  ([[superpowers/specs/2026-08-15-half-rez-trail-tensor-design|spec]]).

- **Primordial follow-ups** — a dedicated PPS biome channel (v1 writes Nutrient, so a scene that also
  needs Nutrient for decomposition can't tell them apart); the paper's asynchronous update order as
  an option; per-species α/β morphologies (untested — species so far share the paper's law); a
  symbiosis umwelt that keeps creatures on the veins without the drift collapse (drift up a
  standing scent converges the soup onto its maxima —
  [[adr/0017-primordial-sim-paper-units-noise-coupling|ADR-0017]]).

### Low-stakes polish
- Injector EMA dt-independence (06-10 #4) — constant `Lerp`, no `deltaTime`.
- `decompositionTempSpan` default (06-10 #6) — code default `4`, show assets override to `3`.
- Parameter-literature grounding (`RESEARCH_BRIEF`).
- MFT turn model — fine, group and hover are three relative routes beside a parallel value switch; one relative
  binding, with `_lastValues` as the only record of knob position, would remove the ring caches. Touches the LED path.
- `typeCount` sync lives in one inspector's change check, hence `[KnobIgnore]`; moving it to the param sets would remove
  the opt-out. `MIDIMapping`'s delta path still snaps out-of-range values through `ClampDelta`.
- `HoverKnobMath` holds stepping used by every knob and path helpers used by one editor class; split and rename it.
- Hover driver edges — it still runs on the hover page when no binding exists; a resting mouse keeps the old objects
  if a multi-edit selection changes but its first target doesn't; the first repaint wait is a 30 ms guess.
- Inspector input cost, unprofiled — a write per wheel event, and `wantsMouseMove` running IMGUI on each mouse move on
  the hover page. Coalesce per frame if a profile shows it matters.

## 🔭 What's below (deeper biology / future — [[../Assets/Workspace/11.0 Biomes/docs/INTERACTION_DESIGN_II|INTERACTION_DESIGN_II]])
- Outside-signal routing (audio / organoid / sensors → field, beyond the 4 perception slots).
- Lifecycle: death → succession (needs mortality first).
- Tempo-breath procedural injector source (reuses the diurnal-sun phase hook; phase = musical bar).
- ~~Habitat differentiation~~ → **shipped 2026-07-15** — agent-authored permeability + per-species
  habitat bands (no deterministic terrain); [[adr/0010-permeability-agent-built-topography|ADR-0010]].

## ⚰️ Dead code / scaffolding (configured but executed by no kernel)
- ~~`preferredPermeabilityMin/Max` habitat gate — read by **no** kernel~~ → **wired 2026-07-15**
  into `ReadFieldKernel` (out-of-band → avoidance + speed penalty, floored);
  [[adr/0010-permeability-agent-built-topography|ADR-0010]].
- Mortality params — declared, unused.
- ~~`BiomeFieldConfig.noiseScale` / `noiseThreshold` + the MFT "noiseScale" knob — fed no kernel since
  ADR-0010~~ → **removed 2026-10-04** with the `Biome.compute` uniforms
  ([[sessions/2026-10-04-measured-followups|session]]).
- Boid `.b` waste-avoidance — sampled every frame but neutralized by its negative weight.

## Memory daemon (infra, non-show-blocking)
`--snapshot-dir` per-show glob vs per-run · canonical store · v1 feedback mechanism — see
[[migration]] open questions.
