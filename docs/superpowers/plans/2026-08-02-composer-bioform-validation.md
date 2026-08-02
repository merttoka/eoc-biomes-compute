---
status: draft
date: 2026-08-02
tags: [plan, validation, sequencer, temporal-composer, bioform, untested]
related: [[../specs/2026-08-02-shanghai-dac-11-3-design]], [[../specs/2026-07-19-temporal-composer-design]], [[../../INDEX]]
---

# Temporal Composer + Bioform 3D — Validation Track

## Why this is a separate plan

65 commits merged on 2026-07-19 across three branches. **Only the MFT LED work has been run.**
The temporal composer (10 TDD tasks: scheduler, composer kernels, cell rigs, four Timeline tracks,
palette, show assembly) and bioform 3D (heightfield bake, orbit camera, Form3D stream, OSC surface)
have never executed.

This track is **deliberately non-blocking**. The DAC show authors its arc on `ParameterInterpolator`
(shipped Jun 7, exercised in exhibition), so a defect found here costs a subsystem, not the
18 Aug submission. Keeping it separate is the point — do not let it acquire show dependencies.

Bioform is additionally **off the DAC critical path by design**: a flat 11.84:1 canvas viewed
head-on gains nothing from a heightfield with orbit camera and SSS.

## Ground rules

- **Observe before fixing.** Record what actually happens, don't repair on sight. A defect list is
  the deliverable; fixes are scheduled after triage.
- **One subsystem per session.** They merged separately and fail independently.
- **Never on `main`.** Branch per subsystem.
- Play-mode observation is the instrument — this project has no automated coverage of scene work.
  The sequencer is the exception: it shipped with EditMode tests (`PatchEventSchedulerTests`).

## Phase A — Temporal Composer

Branch `validate/temporal-composer`.

| # | Task | Pass condition |
|---|---|---|
| A1 | Run existing EditMode tests (`PatchEventSchedulerTests`, `SmokeTest`) | green, or defects recorded |
| A2 | Open `Scene_SIGGRAPH_test.unity` (untracked, has `ShowSequence.playable` + signals) | scene loads, no null refs |
| A3 | Scrub the Timeline without playing | no exceptions; composer RT updates |
| A4 | Play through, all four tracks | BiomeCell / PatchScatter / ParamSnapshot / Routing each visibly do something |
| A5 | Verify `stop rigs on director stop/pause` | rigs actually halt; no orphaned stepping |
| A6 | Re-run the one-click `Scene_SIGGRAPH_2` setup on an already-configured scene | authored clip values **not** rewritten (the Jul-19 fix) |
| A7 | Biome Palette: capture → thumbnail → insert-at-playhead | round-trips |
| A8 | Patch budget rejection (512/128) | over-budget rejected, not silently truncated |

Known-fragile from the commit log, check first: unique rig key for never-bound clips (empty
`PropertyName` collision), kernel rect ceil mismatch, composer aspect rebuild.

## Phase B — Bioform 3D

Branch `validate/bioform-3d`. Worktree `.claude/worktrees/bioform-3d` already exists at `80dc444`.

| # | Task | Pass condition |
|---|---|---|
| B1 | Editor utility creates `DP_BioFlesh` + `M_BioForm` | HDRP Lit SSS + displacement material builds |
| B2 | Height + normal bake kernels | permeability mounds and composite trails both contribute |
| B3 | Heightfield component | grid mesh displaces; temporal smoothing stable |
| B4 | Orbit camera | auto-drift runs; OSC pose writes land |
| B5 | `Form3D` send stream | second stream appears alongside the composite |
| B6 | `/form3d/*` + `/cam3d/*` OSC | params respond |
| B7 | HDRP keyword survival | placeholder maps keep keywords through `ValidateMaterial`; runtime fallback works |

B7 is called out because the commit log shows it was already patched once — it is the likely
recurrence.

## Phase C — Triage

Produce a defect list ranked by: (1) blocks a future show, (2) silently wrong vs. loudly broken,
(3) cost to fix. Silent-wrong ranks above loud-broken — the `spawnScale` desync went unnoticed for
weeks precisely because it was silent.

Then decide per defect: fix now, fix before the next show that needs it, or record and leave.

## Deliverables

- `docs/sessions/2026-XX-XX-composer-bioform-validation.md` — what ran, what broke.
- A defect list with the Phase C ranking.
- ADR **only** if validation changes an architectural decision, not merely because bugs were found.

## Non-goals

- Fixing everything found. Triage first.
- Wiring either subsystem into the DAC show. That coupling is what this plan exists to avoid.
- Bioform performance tuning — it has no show depending on it yet.
