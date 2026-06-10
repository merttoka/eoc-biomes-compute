---
status: accepted
date: 2026-06-10
tags: [adr, biome, pde, homeostasis]
related: [[../migration]], [[../sessions/2026-06-10-branch-validation-equilibrium-fix]], [[../sessions/2026-06-09-ecosystem-io-investigation]]
---
# ADR-0007: diffusion operator is channel-class dependent (mass-conserving for relax channels)

## Context

The mush fix (e1a8654) added per-channel `relaxRate` pulling fields toward `initialValue` baseline. But `DiffuseFieldsKernel` computed `diffused = avg * diffuseRate` — the 3×3 blur conserves the mean, then the multiply leaks `(1-diffuseRate)` of the *entire field* per step. Relax only partially offsets the leak; uniform-field fixed point is `x* = r·b / (1-(1-r)·d·(1-k))` — Oxygen settled at 0.535 instead of 0.8, Temperature 0.427 instead of 0.5. Lowering the baseline to compensate makes it worse (b is the target, not the result).

Options: (a) make diffusion mass-conserving globally — breaks pheromones, whose trail evaporation IS the leak; (b) retune relax/baseline around the leak — fragile, every knob shifts the equilibrium, none removes the leak; (c) gate the operator per channel class.

## Decision

Gate on `relaxRate` in `DiffuseFieldsKernel` (`Biome.compute`):
- `relaxRate > 0` (homeostatic: Oxygen, Temperature): `diffused = lerp(center, avg, diffuseRate)` — pure spatial mixing, uniform field maps to itself, equilibrium == baseline exactly (for decay 0).
- `relaxRate == 0` (stigmergic: pheromones, nutrient, waste): legacy `avg * diffuseRate`; the leak doubles as evaporation.

## Consequences

- Baselines mean what they say: O₂ holds 0.8, Temp holds 0.5; local dips/bumps track agent density.
- `diffuseRate` semantics differ by class: mixing strength (relax channels) vs retention/evaporation (stigmergic). New channels choose class via `relaxRate`.
- `decayRate` is redundant for relax channels (relax is bidirectional — pulls down when above baseline); keep decay 0 there or equilibrium lands slightly under baseline (`x* = r·b/(r+(1-r)·k)`).
- Known limit: the `diffuseRate <= 0` early-skip still bypasses relax — a non-diffusing relax channel is not currently possible (no shipped channel needs it).

## Related

[[../sessions/2026-06-10-branch-validation-equilibrium-fix]] — derivation + review that surfaced it.
