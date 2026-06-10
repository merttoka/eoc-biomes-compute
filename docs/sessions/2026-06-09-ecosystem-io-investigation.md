---
status: closed
date: 2026-06-09
tags: [session, ecosystem, biome, injector, neuron, integration, exhibition]
related: [[../../Assets/Workspace/11.0 Biomes/docs/INTEGRATION_DESIGN]], [[../../Assets/Workspace/11.0 Biomes/docs/PERFORMANCE]], [[2026-06-09-second-performance-pass]]
---
# Ecosystem + IO investigation; injector ergonomics; perf-test tweaks

Follow-on to the second perf pass. Tuning edits, then an investigation of the biome/ALife
structure for richness improvements that don't cost GPU, plus neuron-display and injector
redesign. Branch: `claude/biomes-11-performance-3aog3h`.

## Shipped
- **Boid inspector cap 250 k → 150 k** (`BoidSim.cs`).
- **PERFORMANCE.md §10 bring-up checklist**: turn the neuron firing-ring overlay OFF for the
  show (clashes with the evolved composite; repurpose ideas noted), and cap boid interaction
  ranges to ≤ 64 px (neighbour loop is quadratic in count at fixed range).
- **Injector ergonomics** (additive; defaults preserve behaviour) — `BiomeInjector.cs`,
  `OSCMapping.cs`, `MIDI_OSC.md`:
  - per-source raw→0..1 calibration (`inputMin`/`inputMax`) + EMA `smoothing` so real sensor
    ranges work without TD-side math;
  - `oscAddress` override (decouple wire protocol from display name);
  - "Log Live Source Values" button (per-source channel/uv/raw→cal/osc/last-msg-age monitor).
- **INTEGRATION_DESIGN.md Part 5** — perf-aware refresh: ecosystem richness is GPU-free
  (PDE is tiny + decimated); mush fix is a *prerequisite* for the 10 M scale-up; ranked
  channel recommendations; neuron-display redesign; injector usability. Resolved open Q1
  (Humidity 10→11 is perf-free).

## Decided
- **Ecosystem richness is not perf-constrained** — biome PDE runs on a 320×180 grid
  decimated to every 4th step; adding channels / umwelt entries is ~free. The real cost is
  authoring + mush, not GPU.
- **Mush fix (decay sinks + Q10 + perm-integrator) moves ahead of the scale-up**: 10 M
  physarum saturate the un-sunk Nutrient/Oxygen/Waste ~30× faster than current counts,
  flattening perception gradients and washing the composite. Fidelity bug, amplified by scale.
- **Neuron triggers off the composite**: recommend (1) firing → biome stamp via the injector
  primitive (reuses `NeuronFiringSource.PositionsCPU`/`ScaledValues` — a neuron is just
  another injector source), and (3) rings → separate Syphon infographic layer. Both reuse
  existing machinery; neither draws on the art. Not implemented — aesthetic call pending.
- Did NOT implement ecosystem/display changes — they touch visual fidelity + need a Unity
  session to validate; delivered as ranked proposals instead.

## Open / next session
1. Aesthetic call: neuron disruption ecological (biome stamp) vs graphic (trail scar) vs
   infographic (separate Syphon) — can route different neuron groups to each.
2. Implement the mush fix (asset decay sinks + Q10 + perm relaxation) — cheapest, highest
   fidelity payoff; do before pushing physarum counts up.
3. `ExternalTextureSender.ChannelNames` is stale (9 entries, missing Pheromone_2) — fix when
   touching channels / adding Humidity.
4. Injector: click-to-place in a composite-aspect preview; Texture-valued source (revive the
   dead `externalInfluenceTex`); standardize TD-as-sensor-hub → OSC.
5. Validate this session's edits in Unity (injector calibration/monitor, boid cap).
