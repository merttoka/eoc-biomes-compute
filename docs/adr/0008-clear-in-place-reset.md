---
status: accepted
date: 2026-06-13
tags: [adr, unity, gpu, reset, syphon]
related: [[../ARCHITECTURE]], [[../sessions/2026-06-13-reset-clear-in-place]], [[0006-osc-neuron-firing]]
---
# ADR-0008: Sim reset clears GPU resources in place instead of destroy+recreate

## Context
`SimulationManager.Reset()` — and its cascade into `Biome`, every `SimulationBase`,
`NeuronFiringSource`, `ExternalTextureReceiver` — did `Release(); new GPUResourceManager();
reallocate everything; clear`. Two problems while streaming the composite over Syphon:

1. **Syphon teardown (visible flash).** `compositeOutTex` (and each sim `outTex`) got a
   NEW RenderTexture instance every reset. `ExternalTextureSender` pushes the new
   reference → Klak `SyphonServer.SourceTexture` setter calls `TeardownPlugin()` (disposes
   the native server + IOSurface, stops the publish coroutine) → server drops + re-announces
   in the Syphon directory → downstream (MadMapper) reconnects → black flash.
2. **Long reset frame (hitch).** The whole teardown+realloc ran synchronously in one frame:
   hundreds of MB of agent/trail/field VRAM, neuron CSV re-parse, firing blob re-read from
   disk, 11× `GameObject.CreatePrimitive` for the biome debug grid.

Latent third bug: OSC `/sim_reset` ran the whole thing on the OscJack socket thread (no
main-thread marshal). Unity GPU/GameObject APIs throw off-thread and the OSC loop would break.

Options: (a) **clear-in-place** — allocate once, reuse across resets, only re-run the GPU
clear/respawn dispatches; (b) route every stream through a stable sender-owned intermediate
RT (decouple the Syphon surface from the source texture); (c) leave it.

## Decision
(a) Clear-in-place. Split each owner's `Reset()` into a guarded `Allocate()` + the existing
clear/respawn dispatches. `Allocate()` runs only when an **allocation signature** changes —
resolution, perception scale, agent count, type count; otherwise GPU resources persist, so
`compositeOutTex` and sim `outTex` keep the same instance across a reset and the Syphon
source reference never changes (`SetSource`'s set-on-change guard holds).

Supporting changes: biome debug grid built once (honors the runtime `showDebugGrid` toggle);
`UploadChannelSettings` split so it reuses its buffers; `NeuronFiringSource.Initialize`
loads the blob once; `ExternalTextureReceiver.Initialize` keeps its GPU pool; OSC reset
commands marshalled to the main thread via a drained `ConcurrentQueue`.

Rejected (b) as overkill — normal resets are already solved, and it still re-inits on a
genuine resolution change. Kept as a documented future option for seamless mid-show
resolution/agent-count changes.

## Consequences
- Reset while streaming over Syphon no longer drops the connection or flashes.
  `ResetSimsOnly()` likewise keeps per-sim `SimOutput` streams connected.
- Reset-frame cost drops from a full realloc + 11 GameObject instantiations + disk read to a
  handful of GPU clear/respawn dispatches.
- OSC `/sim_reset` / `/sim_resetSimsOnly` are now safe (run on the main thread).
- **Residual:** genuine structural changes (resolution, `simResolutionScale`,
  `perceptionResScale`, `agentsCount`, sim type count) still force a realloc → one Syphon
  re-init. Accepted — those are Play-stopped operations.
- `GPUResourceManager` instances now **persist across resets**; `ReleaseAll()` runs only on a
  resolution/structural realloc, disable, or destroy (ARCHITECTURE §3.1, §3.8 updated).
- Reset still restores preset params (re-clones `paramsSO`) — live tweaks dropped on reset,
  unchanged behavior.

## Related
[[../ARCHITECTURE]] §3.1, §3.8 · [[../sessions/2026-06-13-reset-clear-in-place]] ·
[[0006-osc-neuron-firing]] (firing blob/playhead reset path)
