---
status: living
date: 2026-07-13
tags: [meta, roadmap, backlog]
related: [[../Assets/Workspace/11.0 Biomes/docs/INTEGRATION_DESIGN]], [[INDEX]]
---
# Roadmap & Backlog

Living status of the biome engine backlog. **Verified against code 2026-07-13** (three-agent
audit). Build-sequence tiers map to [[../Assets/Workspace/11.0 Biomes/docs/INTEGRATION_DESIGN|INTEGRATION_DESIGN]].
Guiding lesson from the audit: *configured ≠ executing* — several features are declared,
inspector-visible, even MIDI-mapped, yet read by no kernel (see Dead Code below).

## ✅ Shipped — Tier-1 "make it alive" complete
Q10 ignition + decay sinks · relax-to-baseline · Humidity channel (+ build cue wired both
shows) · **diurnal sun** (procedural injector, neuron-playhead-phased) · fixed-timestep sim ·
clear-in-place stream-safe resets.

## 🔧 In design (specs forthcoming)
- **Resolution-independent params** — spatial params (moveSpeed, ranges, sensor distance…) are
  pixel-unit, so changing output size breaks every tune. Ground at 3840×2160, scale on Reset by
  `rezY/2160` (height-only; installs go wider, not taller). *Important — actively blocking.*
- **Permeability rework** — replace the static, repetitive, seedless noise terrain with
  agent-authored, *persistent* structure; fix the write-then-dissolve (temp coupling + relax
  heal both fight termite digs). Moderate visual disruption OK; **no** deterministic terrain.

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
- **Permeability-as-topography** (Laplacian curvature → build/dig) — technique exists on
  *Humidity* (`|∇|`), not Permeability. Likely folded into the permeability rework.
- **Trails → separate overlaid composite channel** — decouple termite trail thickness from the
  *additive* main composite (model on the existing post-composite `NeuronRingKernel` overlay).
- **Injector click-to-place + texture-valued source** — no CustomEditor/OnSceneGUI; `Source.value`
  is scalar-only.

### Low-stakes polish
- Injector EMA dt-independence (06-10 #4) — constant `Lerp`, no `deltaTime`.
- `decompositionTempSpan` default (06-10 #6) — code default `4`, show assets override to `3`.
- Parameter-literature grounding (`RESEARCH_BRIEF`).

## 🔭 What's below (deeper biology / future — [[../Assets/Workspace/11.0 Biomes/docs/INTERACTION_DESIGN_II|INTERACTION_DESIGN_II]])
- Outside-signal routing (audio / organoid / sensors → field, beyond the 4 perception slots).
- Lifecycle: death → succession (needs mortality first).
- Tempo-breath procedural injector source (reuses the diurnal-sun phase hook; phase = musical bar).
- Habitat differentiation — *without* deterministic terrain, keyed to an agent-authored
  permeability field rather than static noise (converges with the permeability rework).

## ⚰️ Dead code / scaffolding (configured but executed by no kernel)
- `preferredPermeabilityMin/Max` habitat gate — per-species, MIDI-mapped, read by **no** kernel.
- Mortality params — declared, unused.
- Boid `.b` waste-avoidance — sampled every frame but neutralized by its negative weight.

## Memory daemon (infra, non-show-blocking)
`--snapshot-dir` per-show glob vs per-run · canonical store · v1 feedback mechanism — see
[[migration]] open questions.
