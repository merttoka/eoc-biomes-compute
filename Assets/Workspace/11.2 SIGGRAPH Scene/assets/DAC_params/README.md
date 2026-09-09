# DAC_params — video → Excitability parameter sets

Ten `BiomeFieldConfig` + `UmweltMapping` triples for the Shanghai cut. The video's luminance lands in
**Excitability (13)** through `TextureChannelSeeder`; each set decides how the three species read that
channel and how the PDE holds the picture. Sim param assets (`Physarum_/Boid_/Termite_*_DAC`) stay shared.

Each folder also holds `Scene_DAC_<NN_Name>.unity`, a copy of `Scene_SIGGRAPH DAC` with `Biome.fieldConfig` and the
three sims' `umwelt` already pointed at that folder's assets, and the seeder route (`Luminance → Excitability`) set to
the recommended mode/gain below — open the scene, press Play. The copies were taken 2026-09-08; if the parent DAC scene
changes (params, timeline cues, keep-outs), regenerate or re-diff them — they do not track it.

Read semantics (Biome.compute `ReadFieldKernel`): **Chemotaxis** sums `val*w` into the sensor gradient (±);
**SpeedPenalty** multiplies speed by `lerp(1, val, |w|)` (dark = slow); **Avoidance** adds `max(0, val*w)` —
negative weights are a no-op; **SpeedBoost** adds `max(0, val*w)` into the dispersal speed response.
Excitability with `relaxRate 0`: `diffuseRate` is per-step retention (afterimage where the video goes dark);
the 3×3 blur is always on. With `relaxRate > 0` it becomes blur strength and the field sinks to `initialValue`.

| Set | Kind | Idea | Seeder |
|---|---|---|---|
| `01_Trace` | minimal | Video luminance draws the paths. Every species chemotaxes toward brightness and nothing else; crisp, short afterimage. | MaxToward, gain 1 |
| `02_Negative` | minimal | Video as void. Species avoid brightness, so the picture reads as negative space carved out of the swarm; soft glow edge. | MaxToward, gain 1 |
| `03_Pooling` | minimal | Density collects in the dark. Brightness is a speed multiplier: agents sprint through light and crawl in shadow, so the video's dark shapes fill up. A mild pull toward light keeps them cycling. | MaxToward, gain 1 |
| `04_Currents` | minimal | Video ghosts drift on a prevailing wind. Excitability is advected and stretched along flow; species follow the smeared image, termites surf it. | MaxToward, gain 1 |
| `05_Ecotone` | complex | Species partition the frame by brightness on top of the full DAC ecology: physarum claims the light, boids flee it, termites hover at the edge (attracted until it gets too bright). | MaxToward, gain 1 |
| `06_LongExposure` | complex | Long-exposure glow. Excitability barely leaks, so the video accumulates into a soft, flow-stretched luminance map the whole ecosystem grazes on. Seed it additively or it saturates in seconds. | Additive, gain 0.02 (MaxToward saturates) |
| `07_Storm` | complex | Bright = fast. Excitability is a speed boost for everyone plus a pull toward light; strong convection and a light wind; dispersal lingers longer so firing bursts ride the storm. | MaxToward, gain 1 |
| `08_TermiteCity` | complex | Termites claim the light and build there; their walls duct every scent (permeabilityInfluence 1). Physarum avoids the bright city, boids skim it fast and crawl in the dark. | MaxToward, gain 1 |
| `09_Predation` | complex | Trophic chase under video light. Boids hunt physarum scent, physarum shelters in the light and flees boids, termites scavenge waste and keep out of the glare; hot decomposition closes the loop. | MaxToward, gain 1 |
| `10_Breath` | complex | The field breathes. Excitability sits on a 0.25 baseline and relaxes back toward it, so the video pushes a swell that keeps sinking; Gaussian blur in relax mode. Physarum follows and slows in the swell, boids back off, termites surge. | MaxToward gain 1, or Additive gain 0.05 (SetToward defeats the relax) |

Minimal sets open the habitat bands to 0–1 and drop every read except Dispersal, so brightness is the
only landscape; complex sets keep the full DAC ecology (nutrient/waste/oxygen/humidity/pheromones/habitat)
and layer the Excitability policy on top. Physarum's old `Excitability Avoidance -2` was a no-op (negative
avoidance clamps to 0) — none of these sets carry it.

Generated 2026-09-08; edit freely, they are plain assets.
