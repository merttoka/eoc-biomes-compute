---
status: accepted
date: 2026-04-26
tags: [adr, architecture, touchdesigner, orchestration]
related: [[../migration]]
---
# ADR-0004: TouchDesigner is the orchestration hub

## Context

Installation involves multiple sub-systems: Unity (biomes compute), sensors (cameras / motion / environmental), brain organoid spike data, plant biopotential (planned), termite sims, audio output, possible plotter/physical actuators, memory daemon. Who orchestrates real-time signal flow?

## Decision

TouchDesigner is the central hub. Memory daemon is a separate Python process speaking OSC to TD.

- Sensors → TD (already the user's pattern)
- Unity → TD via Spout/Syphon (visual) and OSC (params)
- TD ↔ Memory daemon via OSC
- TD composites everything for output

## Consequences

- **TD's strength is real-time signal routing** (multi-protocol: OSC, MIDI, DMX, NDI, Spout/Syphon, WebSocket, Serial). Plays to its strengths.
- **Daemon stays focused** on storage / embeddings / queries. Doesn't know about Unity, sensors, or organisms — just OSC.
- **OSC contract is the seam**. Both sides churn independently. See [[../../memory/docs/osc-contract]].
- TD process is a single point of failure for runtime — mitigations (autorestart, watchdog) become operational concerns.
- TD project files (`.toe` / `.tox`) live in `memory/td/` (placeholder for now).

## Related

- [[../sessions/2026-04-26-split-and-daemon-v0]]
- [[../adr/0002-folder-as-event-log]]
