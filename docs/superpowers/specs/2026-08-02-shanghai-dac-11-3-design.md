---
status: draft
date: 2026-08-02
tags: [spec, show, shanghai, dac, siggraph, biome, permeability, cellular-automata, physarum, render]
related: [[../../ARCHITECTURE]], [[../../ROADMAP]], [[2026-07-23-cellular-automata-sims-design]], [[2026-07-14-permeability-mounds-design]], [[2026-06-07-parameter-interpolator-design]]
---

# Scene_DAC (11.3) — SIGGRAPH DAC Shanghai (Design)

## Goal

A 90-second seamless loop for the **Urban Digital Canvas Shanghai** open call — 3rd Shanghai
International Light Festival, Changning District, 24 Sept – 8 Oct 2026, curated by Victoria Szabo
and Wei He. **Submissions close 18 Aug 2026.**

One master serves two near-identical ultra-wide screens:

| Screen | Venue | Resolution | Ratio | Notes |
|---|---|---|---|---|
| **2.6** | Xinda Plaza | 9472×800 | 11.84:1 | no sound |
| **1.2** | Jingyao Hongqiao sunken square | 9000×900 | 10:1 | sound; 30 s–2 min preferred; centre cutout |

Master renders at **9472×900, 60 fps**; both deliverables are **pure crops, no rescale**.

Deliverable is pre-rendered **MP4/H.264** — every DAC screen is video, not a live system. There is
no "on site", which is why the Unity/TD split this show inherits costs nothing (see *Why Unity*).

## Why

`docs/EXHIBITION.md` in `TD_biomes` establishes the site reading, sampled within 400 m of each venue:

> Built-up surface is flat at every core venue across the entire 55-year record. The transformation
> our datasets capture happened 20–60 km away, over the horizon from the viewer. A straight "watch
> Shanghai grow" piece would show these screens someone else's change. The more site-specific
> reading is the inverse: **the viewer stands at the still point the growth radiated away from.**

And:

> Density is the local signal, not sprawl. Built form is flat while population climbs steeply
> (Longemont 0.08 → 0.85). The buildings stopped spreading and **started filling**.

This piece makes both statements mechanical rather than illustrative. The audience does not watch a
city grow; they stand at the dead centre of one and watch life fail to reach them.

### Why Unity, not `TD_biomes`

`TD_biomes` is the intended replacement for the Unity pipeline, but cannot render this show:

- The TD install is **Non-Commercial**, which hard-caps *any* TOP at 1280×1280. Neither 9472×800 nor
  9000×900 is reachable.
- The TD port explicitly scoped out the Timeline sequencer, and a timed piece needs arc automation.

`TD_biomes` remains the source of the **data** and the **site research**. Unity renders.

## Semantic model (the core idea)

The transformation is told in two registers that say the same thing:

| Register | Mechanism | 1975 pole | 2030 pole |
|---|---|---|---|
| **Space** | Shanghai built-up seeds Permeability | open, agents circulate | closed, agents confined |
| **Colour** | 8 physarum type hues converge | spread wide, white-ish → many organisms | unified red → one organism |

Hue *spread across the 8 physarum types* is the variable, not hue itself. Wide spread reads as
several organisms sharing a space; a closed spread reads as one continuous body. Many separate
settlements becoming one continuous built mass **is** many organisms becoming one organism.

This is why the arc drives `ParameterInterpolator` per type rather than a global colour grade — a
grade shifts the mean and never produces the "separate things merging" read. The effect lives
entirely in the spread closing.

## The transect

The strip is a **horizontal transect** of the shared data frame, sized from the master's aspect.

- Grid contract (all `TD_biomes` layers): bbox `120.65, 30.60, 122.15, 31.90` WGS84, EPSG:4326,
  `size_px: 2048` — about 143 × 144 km, so **70 m/px**.
- Master is 9472×900 = 10.52:1 → crop **2048 × 195 px** = **143 × 13.6 km**.
- Centred on grid row **1077** — the venue cluster centre, pixel (1016, 1077). The cluster spans
  5.9 × 1.6 km, near-dead-centre of the frame.

Consequences, all of them the point:

- Screen centre is where the audience stands, and where built-up has been saturated since before
  1975. It closes first and stays closed.
- The 20–60 km band where change actually happened lands **14–42 % out toward each edge** — inside
  frame, but over the viewer's horizon in life.
- 195 px upscaled to 900 px is a 4.6× stretch, acceptable because 70 m/px is already near GHSL's
  native 3-arcsec resolution and the seed is diffused by the PDE regardless. No invented detail.

### Screen 1.2's centre cutout

1.2 is a 190 m inward-facing glass curtain wall — 36.4 m centre with 26.8 m wings, 8.76 m panel
height — and **has a cutout for the central screen area**. Composition must survive a hole in the
middle, explicitly, not incidentally.

Two things make this cheap rather than a constraint fought:

- **The thesis already wants a dead centre.** Screen centre is the saturated, closed, agent-free
  region. A cutout there removes the part of the image that carries the least motion by design.
- **Keep-out is authored, not hoped for.** The transect gives the composition a symmetric structure
  around row 1077, so a `centreKeepOut` normalized-width parameter on `ShowArc` biases three things
  away from the middle third: dispersal stamp placement (`BiomeInjector` source `fieldUV`), CA
  glitch seeding, and physarum spawn density.

Verification is a criterion, not a hope: render a 1.2 crop with the cutout region masked black and
confirm nothing load-bearing was inside it (success criterion 8).

Because 2.6 has **no** cutout, the same master must read as complete *without* the hole — so
keep-out biases density, it never leaves a hard-edged empty band.

Source layer: `shanghai_growth/shanghai_builtup_{00..11}.png` — GHSL GHS-BUILT-S R2023A, 12 epochs
1975→2030 in 5-year steps, 16-bit, normalized linearly against a shared
`global_max_m2_per_cell: 7368.0`.

> **16-bit trap.** These frames are 16-bit multichannel. Pillow cannot encode 16-bit RGB/RGBA and
> **silently downconverts on read**. Any bake or verification tool must use `pypng`, or a correct
> layer looks broken.

## Components

### 1 · `Biome.SeedChannelFromTexture()` + `SeedChannelKernel` (shared primitive)

**There is currently no texture→channel path.** Existing kernels write from agent-position buffers
(`WriteFieldKernel`, `BuildPermeabilityKernel`) or stamp radial blobs from a point + radius +
falloff (`InjectStampKernel`). `BiomeInjector.Source` is a *point stamp* — `fieldUV`, `radius`,
`falloff`, `channel`, `gain`, `mode` — and cannot ingest a raster.

One new kernel closes that gap and serves two consumers:

- the Shanghai permeability seed (§2), and
- the CA channel-publish path (§4 / [[2026-07-23-cellular-automata-sims-design]] §4 "B1").

Signature mirrors the existing `RenderChannelTo` / `WriteField` conventions:

```
void SeedChannelFromTexture(int channel, Texture src, float gain, BlendMode mode)
```

`BlendMode` reuses `BiomeInjector.BlendMode` semantics (`Additive` / `MaxToward` / `SetToward`) so
there is one blend vocabulary in the codebase, not two.

### 2 · `ShanghaiTransect`

Bakes the 12 epochs once (editor utility → `StreamingAssets`), then at runtime crossfades adjacent
epochs by arc phase and seeds Permeability each biome step.

**Monotonic closure blend.** Built-up only grows 1975→2030, so the layer is applied as a *closure
floor*, never an overwrite:

```
perm = min(perm, 1 - shanghai(t))
```

The city only ever closes. Termite-built mounds survive because they only close further. A stomping
re-seed would fight [[../../adr/0010-permeability-agent-built-topography|ADR-0010]]'s persistence
design; a static 2030 seed would waste 11 of the 12 epochs.

### 3 · Show arc — `ShowArc`

A waypoint program on the shipped `ParameterInterpolator` (Jun 7, exercised in exhibition) plus OSC
cues. **The show path depends on no untested code.** The Jul-19 temporal composer and bioform 3D
are validated on a parallel, non-blocking track — a defect there costs a subsystem, not the
submission.

```
0s ─────── 20s ─────── 55s ─────── 75s ─────── 90s → loop
empty      many        converging   ONE BODY    melt
           8 hues      spread       8 hues      back to
           spread      closing      unified     open / empty
           white-ish                red

perm       1975 ──────── epochs crossfade ──────── 2030   → ResetTermites
CA         ·             seeding      GLITCH PEAK          melts it open
firing     sparse ────────────────── dense ── peak ──────── sparse
```

Ramp-from-nothing **is** the arc, not a prologue. The loop closes by calling **`ResetTermites`** at
75 s, which already "clears permeability, melts mounds, frees confined species" — the built form
dissolving is the loop point, using shipped code, and it masks the permeability reset from 2030
back to 1975.

### 4 · CA subsystem

Full scope per [[2026-07-23-cellular-automata-sims-design]]: `FieldSimulationBase`, `CyclicCASim`,
`LookupCASim`, both computes, both `IParamSet` param sets, and CA↔CA coupling.

**The visible glitch is the composite layer, not the field.** The CA renders into `outTex` and rides
the existing additive 8-layer composite with per-sim `compositeWeight` — that tear is what reads as
"glitch" at 55–75 s. The channel-publish path (§4 of the CA spec, agents perceiving CA state via
`UmweltMapping`) is still built, but is **not load-bearing for this show**: if it slips, the glitch
still lands, degraded not broken.

Two constraints carried from that spec:

- `LookupCASim` is authored **double-buffered from the start**. The ported `CA2D.cs` binds a single
  `simTex` as both read and write — an in-place race. It is never inherited, only avoided.
- The lookup rule table is an `nstates^5` seed-generated *buffer*, not scalars, so
  `ParameterInterpolator` cannot morph it. Only `seed`, `lambda`, and `nstates` are interpolatable,
  and changing any regenerates the table. CA evolution across the arc is **discrete regenerations,
  not continuous morphs.**

### 5 · Render + `CueExporter`

Offline render via Unity Recorder at **60 fps** — **not realtime capture**. Recorder drives `Time`
deterministically, so the Jul-11 fixed-60 Hz `FixedUpdate` sim steps stay in lockstep with recorded
frames even at single-digit wall-clock fps. That work is precisely what makes an ultra-wide offline
render viable on this machine.

60 fps output against a 60 Hz sim is a **1:1 step-to-frame ratio** — no interpolation, no catch-up
stepping, and 90 s = exactly 5400 sim steps. That makes the loop seam deterministic and reproducible
across re-renders, which a non-integer ratio would not.

`CueExporter` writes `cues.json` (firing indices + arc cue times) alongside the render, so **Max/MSP**
composes sound against picture after lock. Screen 1.2 supports sound; 2.6 does not.

## Data flow (per biome step, during the arc)

1. `ShanghaiTransect` resolves epoch phase from arc time → crossfades two 16-bit frames.
2. `SeedChannelFromTexture(Permeability, transect, gain, MaxToward)` applies the closure floor.
3. Termites build on top (`BuildPermeabilityKernel`) — mounds only close further, so they survive.
4. Biome PDE steps (diffuse / interact / advect / flow) as today.
5. Agent sims sense the field; species confined to preferred-permeability bands.
6. CAs step double-buffered, render to `outTex`.
7. `SimulationManager.Render` composites all layers additively by `compositeWeight`.
8. `ParameterInterpolator` advances physarum type hues toward the arc's current spread.

## Files touched

- **New** `src/computes/` — `SeedChannelKernel` added to `Biome.compute`.
- **New** `src/components/core/FieldSimulationBase.cs` (or virtual no-op agent members on
  `SimulationBase` — the CA spec leans this way as the smallest manager change).
- **New** `src/components/Sim/CyclicCASim.cs`, `LookupCASim.cs`.
- **New** `src/computes/CyclicCA.compute`, `LookupCA.compute`.
- **New** `src/params/CyclicCAParams.cs`, `LookupCAParams.cs`.
- **New** `src/components/show/ShanghaiTransect.cs`, `ShowArc.cs`, `CueExporter.cs`.
- **New** `src/Editor/ShanghaiTransectBaker.cs` (12-epoch bake, `pypng`-equivalent 16-bit read).
- `src/components/core/Biome.cs` — `SeedChannelFromTexture`; **raise `biomeRezX/Y` `[Range(32,1024)]`**
  if the ultra-wide field proves too thin (see Risks).
- `src/components/core/BiomeFieldConfig.cs` — channel(s) for the CA publish path.
- `src/components/core/SimulationManager.cs` — hold CA sims; `ResetCellular` per-type reset.
- `Assets/Workspace/11.3 SIGGRAPH DAC Shanghai Scene/Scene_DAC.unity` — wiring.

## Non-goals (deferred)

- **Bioform 3D.** A flat 11.84:1 canvas viewed head-on gains nothing from a heightfield with orbit
  camera and SSS. Retired from this show's critical path; validated on the parallel track.
- **The other 7 screens.** 2.2–2.6 are judged together, but this piece is authored for the ultra-wide
  pair; site preferences are noted in the application rather than re-authored per canvas.
- **Audio in Unity.** Sound is generated in Max/MSP against exported cues, post-lock.
- **The 50-minute / 5-minute-break durational form.** That idea is for galleries, not DAC.
- **The remaining 8 Shanghai layers.** Only `growth` (built-up) is used. `urban` B (population) is the
  strongest follow-up candidate since density, not sprawl, is the local signal.

## Risks / open tuning

- **Field aspect is the real unknown.** `Biome.biomeRezX/Y` is `[Range(32, 1024)]` — the field is a
  deliberately coarse PDE grid. At 10.52:1 that is 1024×97, which may be too few cells vertically for
  habitat banding to read. Needs a play-mode look before committing; mitigation is raising the cap,
  at ~55 MB per 1024² × 13 channels the memory is not the constraint.
- **Full-spec CA in 16 days** is the schedule risk. Mitigated twice: the show arc never depends on CA,
  and the glitch uses the composite path rather than the channel-publish path.
- **CCA neighbourhood cost.** `range=r` Moore is O((2r+1)²) samples per cell per step. Run CAs at
  reduced resolution (`simResolutionScale` or a per-sim override) and/or `stepEvery` decimation.
- **Resolution-independent params.** The Jul-13 work scales pixel-unit spatial params by
  `rezY / referenceHeight`, grounded at 2160. At 900 px tall everything scales to ~0.42×, so the look
  tuned at 2160 will not transfer directly — either re-ground the reference or retune. **The exact
  symbol name was not located during design and must be confirmed at plan time, not assumed.**
- **`BiomeChannel` definition location.** Referenced as `BiomeChannel.Count` / `BiomeChannel.Oxygen`,
  but the Jun-23 "single-source channel names" refactor moved it. Confirm at plan time.
- **Determinism.** CA reset seeding must use the sim-step RNG convention, not `Time.frameCount`, so
  multi-step catch-up frames stay deterministic under Recorder's driven clock.
- **Loop seam.** Permeability resets 2030→1975 at the loop point. `ResetTermites` at 75 s should mask
  it; verify no pop at the actual seam, not just in theory.
- **`spawnScale` is desynced in this very scene — fix before authoring.** Audited 2026-08-02:
  `Scene_DAC` has sims at `(0.5, 0.6)` but `SimulationManager.m_RingSpawnScale` and
  `BiomeInjector.firingSpawnScale` both still at `(0.4, 0.75)`. Firing rings and dispersal stamps
  are displaced up to 5 % of canvas width / 7.5 % of height from the agents they target — **~474 px
  horizontally at 9472 px**, worst at the edges, zero at centre. 11.2 SIGGRAPH has the same defect;
  11.1 CURRENTS agrees only because it was never retuned. Blocked on
  [[2026-08-02-neuron-layout-single-owner-design]]; authoring the arc against displaced stamps would
  bake the error into tuning.

## Success criteria (play-mode; no automated tests for scene work)

1. Master renders 9472×900; both crops land without rescale.
2. Loop is seamless — no visible pop at the 90 s→0 s seam, permeability included.
3. Hue spread visibly closes: the 20 s frame reads as several organisms, the 75 s frame as one body.
4. Screen centre is visibly deader than the edges throughout the closed phase.
5. CA glitch tears the image at 55–75 s and resolves.
6. `cues.json` timings align with the rendered frames.
7. Existing scenes (11.1 CURRENTS, 11.2 SIGGRAPH) still run — no regression from
   `SeedChannelFromTexture` or the `SimulationBase` change.
8. **Cutout survivability** — the 1.2 crop rendered with the centre cutout masked black still reads
   as complete, and the 2.6 crop (no cutout) shows no hard-edged empty band where the keep-out bias
   was applied.
9. Firing rings and dispersal stamps land on the agent clusters, at both the centre and the extreme
   left/right edges of the 11.84:1 frame — the direct visual test that the `spawnScale` desync is
   fixed.

## Follow-ups

- Population (`urban` band B) as a second seed — density is the local signal the built-up layer hides.
- Temporal composer + bioform validation, merged back once proven.
- Gallery long-form (50 min, 5 min break) reusing the arc machinery at a different timescale.
