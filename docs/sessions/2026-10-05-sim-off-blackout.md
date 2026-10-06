---
status: closed
date: 2026-10-05
tags: [session, osc, show, blackout, biomes]
related: [[../ARCHITECTURE]], [[2026-06-09-osc-neuron-firing]]
---
# Show blackout: /sim_off · /sim_on

Branch `show-sim-off` off `main`. Ask: organoid-analysis now drives the show by itself (its
bridge streams `/index` 0→179999 at 60/s with the `osc_index_tester.py` reset cues) and adds a
2 min blackout between 50 min passes. EoC needed a way to go dark on cue.

## Shipped
- `OSCMapping`: `/sim_off [fadeSeconds]`, `/sim_on [fadeSeconds]` (float; no arg →
  `outputFadeSeconds`), marshalled to the main thread like the resets.
- `SimulationManager`: master output level eased in `LateUpdate` (unscaled time),
  `SetOutputOn(on, fadeSeconds)` + Output Off/On buttons, `outputFadeSeconds = 5`,
  `pauseWhenDark = true` (FixedUpdate skips stepping once fully dark; `stepsPerTick` untouched).
- `SimulationManager.compute`: `masterLevel` multiplied last in `CompositeRenderKernel`, bound on
  every dispatch as `pow(smoothstep(level), 2.2)` — the composite is linear light, so this reads
  as an even fade. Both render paths (plain, `frameBlend`) go through it; with stepping paused the
  blend path recomposites every frame, so the black lands.

## Show cycle (sent by organoid-analysis)
`/sim_resetSimsOnly` → `/sim_on 5` → `/index` 0→179999 at 60/s with 5× `/sim_resetTermites`,
10× `/sim_resetPhysarum` → `/sim_off 5` → 120 s silence → repeat. Respawn happens while still
black, then the fade-in.

## Verify (Unity, 11.3 Brave New Work in Play)
`uv run --with python-osc python -c "from pythonosc.udp_client import SimpleUDPClient as C; c=C('127.0.0.1',1234); c.send_message('/sim_off',5.0)"`
→ fades to black over 5 s and the sims pause; same with `/sim_on` → steps resume and it fades back.
