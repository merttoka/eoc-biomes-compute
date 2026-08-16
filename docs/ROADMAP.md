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
  *additive* main composite (model on the existing post-composite `NeuronRingKernel` overlay).
- **Injector click-to-place + texture-valued source** — no CustomEditor/OnSceneGUI; `Source.value`
  is scalar-only.
- **Half-rez trail structure tensor** — cut the `trailAnisotropy > 0` cost ~3–4× by evaluating
  the (already low-frequency) tensor at half rez with double-angle axis encoding; designed, not
  built — measure 11.3's knob-on GPU ms first
  ([[superpowers/specs/2026-08-15-half-rez-trail-tensor-design|spec]]).

### Low-stakes polish
- Injector EMA dt-independence (06-10 #4) — constant `Lerp`, no `deltaTime`.
- `decompositionTempSpan` default (06-10 #6) — code default `4`, show assets override to `3`.
- Parameter-literature grounding (`RESEARCH_BRIEF`).

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
- Boid `.b` waste-avoidance — sampled every frame but neutralized by its negative weight.

## Memory daemon (infra, non-show-blocking)
`--snapshot-dir` per-show glob vs per-run · canonical store · v1 feedback mechanism — see
[[migration]] open questions.
