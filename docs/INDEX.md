---
status: living
date: 2026-04-26
tags: [meta, index]
---
# Docs Index

## Architecture (living)
- [[ARCHITECTURE]] — system reference (Unity runtime + memory overview)
- [[migration]] — memory architecture plan + open questions

## Specs / plans
- [[superpowers/specs/2026-06-08-osc-neuron-firing-design]] — OSC-driven shared neuron firing ([[superpowers/plans/2026-06-08-osc-neuron-firing|plan]])
- [[superpowers/specs/2026-06-07-parameter-interpolator-design]] — slow preset crossfade interpolator

## Sessions (newest first)
- [[sessions/2026-06-09-osc-neuron-firing]] — shared OSC-driven neuron firing + ring overlay
- [[sessions/2026-04-26-split-and-daemon-v0]] — repo split via rsync, memory daemon v0

## ADRs (newest first)
- [[adr/0006-osc-neuron-firing]] — neuron firing is an external OSC-driven shared signal
- [[adr/0005-includes-copied-verbatim]] — `Includes/` not vendored
- [[adr/0004-td-as-orchestration-hub]] — TouchDesigner as central hub
- [[adr/0003-local-first-storage]] — SQLite + LanceDB per node
- [[adr/0002-folder-as-event-log]] — snapshot folder is source of truth
- [[adr/0001-rsync-over-filter-repo]] — split via plain rsync

## Memory daemon
- [[../memory/README]]
- [[../memory/docs/osc-contract]]
