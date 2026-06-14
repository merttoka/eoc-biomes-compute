---
status: closed
date: 2026-06-13
tags: [session, unity, gpu, reset, syphon, performance]
related: [[../ARCHITECTURE]], [[../adr/0008-clear-in-place-reset]]
---
# Reset: clear-in-place (kill Syphon reset stutter)

Investigated a visible stutter when resetting sims while streaming the composite over Syphon.
Root cause (verified): reset destroyed + recreated `compositeOutTex` (the streamed texture),
flipping its reference → Klak `SyphonServer.SourceTexture` setter tore down the native server
+ IOSurface → MadMapper reconnect/flash; compounded by a long synchronous realloc frame.

## Shipped
Branch `fix/reset-clear-in-place` → main. Tested over Syphon (composite stream): reset no
longer drops/flashes the connection.

- `SimulationBase.cs` — `Reset()` → `NeedsAllocation()`-guarded `Allocate()` + per-reset
  `GPUReset`; neuron CSV parsed+uploaded once and rebound per reset (was re-parsed + leaked a
  buffer every reset).
- `SimulationManager.cs` — composite/dummy/weights moved into guarded `Allocate()`; cascade +
  `Render()` run every reset; `compositeOutTex` instance now stable across resets.
- `Biome.cs` — guarded `Allocate()`; debug grid built once (honors `showDebugGrid` toggle);
  `UploadChannelSettings` split into allocate-once + upload (no longer leaks a buffer pair per
  `Reset`/`ClearAll`).
- `NeuronFiringSource.cs` — `Initialize()` idempotent: blob loaded once, buffers persist, only
  the firing envelope resets.
- `ExternalTextureReceiver.cs` — `Initialize()` keeps its GPU pool across resets.
- `OSCMapping.cs` — `/sim_reset` + `/sim_resetSimsOnly` marshalled to the main thread (drained
  `ConcurrentQueue` in `Update`); fixes a latent off-thread-GPU crash that would break the OSC
  loop. Param/injector/`/index` callbacks stay inline (CPU-only).

## Decided
- Clear-in-place over the sender-owned-intermediate-RT alternative → [[../adr/0008-clear-in-place-reset]].
- Residual realloc (resolution / perception scale / agent count / type count) left as a
  Play-stopped operation — not hardened.
- Reset keeps restoring preset params (drops live tweaks) — unchanged.

## Open / next session
1. If mid-show resolution/agent-count changes ever need to be seamless: route every stream
   through a fixed-size sender-owned RT (ADR-0008 rejected option (b)).
2. Pre-existing: sim `Reset()` re-`Instantiate`s `paramsSO` each reset → old clone leaks until
   GC. Negligible now; revisit if reset cadence rises.
