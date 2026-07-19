# MIDI & OSC Control — 11.0 Biomes

## Components

### MidiFighterTwister

Dedicated Midi Fighter Twister integration. Works in **edit mode and play mode**.
Add to scene, wire SimManager + Simulations. Edit mode logs CC numbers for debugging.

### MIDIMapping

Generic MIDI CC mapping for any controller. Inspector dropdown for param names.

### OSCMapping

OscJack server (default port 9000). Auto-registers addresses from `ModulatableParams`.
Convention: `/<simPrefix>_<paramName>_<index>` (e.g. `/p_moveSpeed_0`, `/b_maxSpeed_0`).

Reset commands (arg ignored — any value triggers; all marshalled to the main thread):

| Address | Effect |
| ------- | ------ |
| `/sim_reset` | full reset (`SimulationManager.Reset()`) — sims + biome + external input |
| `/sim_resetSimsOnly` | respawn **all** sims, preserve biome; zeros the global step counter |
| `/sim_resetPhysarum` | respawn only Physarum sims (others keep running; step counter untouched) |
| `/sim_resetBoids` | respawn only Boid sims |
| `/sim_resetTermites` | respawn only Termite sims |

**Biome injector** — assign a `BiomeInjector` to OSCMapping's `Biome Injector` field.
Registers one address per source, keyed by the source's **Name** (use OSC-safe names, no
spaces). Drives external installation inputs (plants → Oxygen, robot proximity →
Temperature, neuron firing → Pheromone) into biome channels at mapped locations:

| Address | Args | Effect |
| ------- | ---- | ------ |
| `/inject/<name>` | 1 float `0..1` | set the source's live value |
| `/inject/<name>/pos` | 2 floats `u v` (`0..1`) | move the source's biome location (e.g. robot pose → location) |

Value is multiplied by the source's `gain` and stamped into its channel each step
(pre-Step, so it advects + diffuses). Sources register at `Start` — define them before Play.

**Raw sensor calibration (on each source):** sensors rarely send 0..1. Set `Input Min`/
`Input Max` to the raw range you actually see (ppm, distance, etc.) and the injector remaps
+ clamps to 0..1 for you; `Smoothing` (0–0.99 EMA) denoises a jittery feed without TD-side
work. `Value Timeout` (~1–2 s) decays a dropped sensor to 0. `OSC Address` overrides the
default `/inject/<name>` so you can rename a source without rewiring the sender. The
**"Log Live Source Values"** inspector button dumps each source's channel, uv, raw→calibrated
value, OSC address, and seconds-since-last-message — run it in Play to see which sensors
actually arrive.

> **Recommended pattern:** route every physical sensor through TouchDesigner (the hub) and
> have TD emit `/inject/<name> <float>`. Keep raw units if you like — calibrate per-source in
> Unity. TD already speaks serial/MQTT/Art-Net/DMX/HTTP, so Unity only ever needs OSC-in.

---

## Midi Fighter Twister

### Hardware MIDI Layout (0-indexed channels)


| Function                  | Channel | CC Range                         |
| ------------------------- | ------- | -------------------------------- |
| Encoder rotation          | Ch 0    | CC 0-63 (16 per HW bank)         |
| Encoder push              | Ch 1    | CC 0-63                          |
| RGB color (to device)     | Ch 1    | CC 0-15                          |
| RGB animation (to device) | Ch 2    | CC 0-15                          |
| Side buttons              | Ch 3    | CC 8-13 (HW bank 1), +6 per bank |
| HW bank change            | Ch 3    | Note 0-3 (bank 1-4)              |
| Shift layer               | Ch 4    | CC 0-63                          |


### Two-Tier Bank System

**Hardware banks** = column pages (extend type capacity across 4 pages)
**Software banks** = param categories (core, secondary, visual, umwelt)

This gives **4 soft banks × 4 HW banks × 16 encoders = 256 unique param slots**.

#### Hardware Banks (L2/R2 on device)

HW banks extend the 4-column grid to support more types. Types that don't fit in 4 columns spill into the next HW bank.

```
Example: 5 Physarum types + 5 Boid types (10 columns)

HW Bank 1 (CC 0-15):  [P0][P1][P2][P3]
HW Bank 2 (CC 16-31): [P4][B0][B1][B2]
HW Bank 3 (CC 32-47): [B3][B4][ ][ ]
HW Bank 4 (CC 48-63): [ ][ ][ ][ ]
```

HW bank switches are detected automatically via Note On (Ch 3, notes 0-3). The inspector grid and HW tab update in real-time.

#### Software Banks (configurable side buttons)


| Bank             | Content                                                                        | Color       |
| ---------------- | ------------------------------------------------------------------------------ | ----------- |
| 0: Core          | moveSpeed, senseAngle, turnAngle, senseDist / maxSpeed, maxForce, ranges       | Blue/Orange |
| 1: Secondary     | depositAmount, eatAmount, diffuseRate, foodSeek                                | Blue/Orange |
| 2: Visual+Biome  | hue, saturation, diffuseRate per type + biome cross-field (col 3 of HW bank 4) | Green       |
| 3: Umwelt+Global | metabolicHeat, O2, permeability per sim + globals (col 3 of HW bank 1)         | Purple/Cyan |


Bank 2: type columns fill contiguously from col 0 (families never split mid-page); biome cross-field lives in the last column of HW bank 4. Bank 3 reserves col 3 of HW bank 1 for globals (≤3 sims fit before it).

### LED Feedback

**Per-type hue gradients** — SimParam knob color = family hue range interpolated by type index, so columns read left→right as a gradient (inspector-tunable, MFT hue-wheel CC values 1-125):

| Family   | Default range | Reads as         |
| -------- | ------------- | ---------------- |
| Physarum | 20 → 40       | deep blue → cyan |
| Termite  | 57 → 70       | yellow band      |
| Boid     | 78 → 98       | orange → red     |

Fixed: Biome=green(43), Umwelt=cyan(40), Global=purple(110). Single-type sims sit at range midpoint. Bound knobs full RGB brightness; unbound off.

**Bank-switch flash** — on any soft/HW bank change, `bankFlashDuration` (default 0.7s) overlay:

- Top row: `softBank+1` knobs lit in soft bank color (blue/orange/green/purple)
- Bottom row: `hwBank+1` knobs lit white
- Rings off during flash; normal LEDs restore after

### Soft-Takeover (Pickup)

After switching either bank type, encoders are ignored until the physical knob position passes through the current param value. Prevents value jumps.

### 4x4 Encoder Grid

```
[ 0][ 1][ 2][ 3]   row 0
[ 4][ 5][ 6][ 7]   row 1
[ 8][ 9][10][11]   row 2
[12][13][14][15]   row 3
```

### Layout Modes

**ColumnPerType** (default) — columns = agent types flattened across all sims, rows = params. Spans HW banks for >4 types.

**ColumnPerSim** — columns = sims (type 0 only). Spans HW banks for >4 sims.

**HalfAndHalf** — top 2 rows = sim 0, bottom 2 = sim 1. Columns = types, spans HW banks.

### Encoder Push (configurable)

- **RandomizeParam** — randomize that one knob's param
- **ResetParamToDefault** — snap to midpoint
- **ToggleFineTune** — precision mode per encoder

### Shift Layer

Configured per-encoder in the MFT Utility app. When active, encoder sends on Ch 4. Code applies 10x precision scaling.

### Side Buttons

6 physical buttons, L2/R2 reserved for HW bank switching.


| Position    | Left            | Right           |
| ----------- | --------------- | --------------- |
| Top (L1/R1) | Reset           | ResetSimsOnly   |
| Mid (L2/R2) | *HW bank prev*  | *HW bank next*  |
| Bot (L3/R3) | RandomizeParams | RandomizeColors |


Other assignable actions: RandomizeAll, ExportPNG, TogglePause, CycleDebugChannel, ClearBiome, ToggleDebugGrid, PrevSoftBank, NextSoftBank, CycleScreen, ToggleScreen, SaveSnapshot (new timestamped preset), SaveToCurrentParams (overwrite the assigned preset asset in place with live params).

Side button CCs auto-offset per HW bank (+6 per bank: 8-13, 14-19, 20-25, 26-31). Same physical button = same action on all HW banks. Only configure HW bank 1 CC numbers in inspector.

### Inspector

- **Two rows of tabs**: soft bank (param category) + HW bank (column page)
- **4x4 color-coded grid** shows current page bindings
- **Color legend**: blue=Physarum, orange=Boid, yellow=Termite, green=Biome, cyan=Umwelt, purple=Global (device LEDs use per-type gradients within each family — see LED Feedback)
- Tabs auto-sync when switching banks on the physical MFT

### Debugging CC Numbers

1. Enable `logMidi` on the component
2. Works in **edit mode** — no play mode needed
3. Press buttons/turn encoders, check console for `[MFT] Unhandled CC: Ch.X CC#Y = Z`
4. Enter HW bank 1 CC numbers in inspector; banks 2-4 auto-offset

---

## Param Ranges

Both sims support **1-8 agent types** (set `typeCount` on the params ScriptableObject).

Min/max ranges for MIDI/OSC mapping are configurable on each params SO under **"MIDI/OSC Ranges"**. These control:

- How 0-1 MIDI values map to actual param values
- Randomize range for RandomizeParams buttons
- LED ring feedback normalization

### Physarum (9 params)

moveSpeed, senseAngle, turnAngle, senseDistance, depositAmount, eatAmount, diffuseRate, hue, saturation

### Boid (11 params)

maxSpeed, maxForce, separateRange, alignRange, attractRange, depositAmount, eatAmount, foodSeek, hue, saturation, diffuseRate

### Biome Cross-Field (Soft Bank 2, col 3 of HW bank 4)

wasteToNutrientRate, temperatureToFlowStrength, temperatureToPermeability, noiseScale

### Umwelt (Soft Bank 3, per sim)

metabolicHeat, oxygenConsumption, preferredPermeabilityMin, preferredPermeabilityMax

### Global (Soft Bank 3, col 3 of HW bank 1)

stepsPerFrame, stepMod, debugChannel

---

## Packages

- `jp.keijiro.minis` 1.3.2 — MIDI input via Unity InputSystem
- `jp.keijiro.osc-jack` 2.0.0 — OSC server/client
- Scoped registry: `jp.keijiro` via `https://registry.npmjs.com`

