---
title: External texture share (Syphon/NDI/Spout send + receive)
date: 2026-06-07
status: approved
tags: [11.0-biomes, texture-share, syphon, ndi, spout, touchdesigner]
related: [[ARCHITECTURE]], [[migration]]
---

# External Texture Share

Send selected Unity textures (composite output, per-sim outputs, biome field
layers) to external apps over **Syphon / NDI / Spout**, and receive one external
texture as sim influence. Replaces the Spout-receive-only `ExternalInputProvider`.

## Goal

Give the installation real inter-app video I/O so TouchDesigner (and other tools)
can consume Unity output and feed Unity input — across macOS (Syphon/NDI) and
Windows (Spout/NDI). Per-stream protocol, source selection, and resolution scaling.

## Decisions (locked)

- **Split into two components:** `ExternalTextureSender` (many out-streams) and
  `ExternalTextureReceiver` (one in-stream; replaces `ExternalInputProvider`).
- **Send sources:** composite output, per-sim outputs, biome channel layers.
- **Per-stream protocol** (NDI/Syphon/Spout), platform-gated.
- **Biome-layer extract only when that stream is enabled.**
- **Per-stream resolution scale** (downscale before send).
- **Default stream names** auto-generated; user-overridable.
- **Add `jp.keijiro.klak.syphon`** package (macOS native; version verified at
  implement time).

## Platform / compile constraints (discovered)

- KlakSpout + KlakNDI runtime asmdefs have empty `includePlatforms`/`excludePlatforms`
  → their types compile on **all** platforms (Spout is a runtime no-op on macOS).
  **No `#if` compile guards needed for NDI or Spout.**
- KlakSyphon ships a Metal native plugin → its asmdef is macOS-constrained.
  References to `Klak.Syphon.*` must sit behind
  `#if (UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX)`.
- The project uses the default `Assembly-CSharp` (no asmdef in `11.0 Biomes/`), so
  Klak namespaces are auto-referenced. Isolate ALL Klak API calls + the Syphon guard
  in one backend file.

## Architecture

### `ExternalTextureShare.cs` — backend (only file touching Klak)

Encapsulates every `Klak.Ndi` / `Klak.Spout` / `Klak.Syphon` reference and the
Syphon platform guard. Senders/receivers below stay protocol-agnostic.

```csharp
public enum ShareProtocol { NDI, Syphon, Spout }

public interface ITextureSenderBackend { void SetSource(Texture tex); void Dispose(); }
public interface ITextureReceiverBackend { Texture Received { get; } void Dispose(); }

public static class ExternalTextureShare
{
    // Platform availability: Spout=Windows, Syphon=macOS, NDI=always.
    public static bool IsAvailable(ShareProtocol p);

    // Add the right Klak sender/receiver MonoBehaviour to `host`, capture method =
    // Texture, bind name + resources. Returns null (+ one-time warn) if unavailable.
    public static ITextureSenderBackend   CreateSender(GameObject host, ShareProtocol p,
                                                       string name, ShareResources res);
    public static ITextureReceiverBackend CreateReceiver(GameObject host, ShareProtocol p,
                                                         string name, ShareResources res);
}

// Serializable holder for the per-protocol Resources assets the Klak components need.
[Serializable] public class ShareResources
{
    public Klak.Spout.SpoutResources spout;   // always-available type
    public Klak.Ndi.NdiResources     ndi;     // always-available type
    // Syphon needs no resources asset.
}
```

- NDI: `NdiSender { captureMethod = Texture, sourceTexture, ndiName, SetResources }` /
  `NdiReceiver { ndiName, SetResources, → targetTexture/received }`.
- Spout: `SpoutSender` / `SpoutReceiver` (mirrors current receive code: `SetResources`,
  `receivedTexture`).
- Syphon: `SyphonServer` (send) / `SyphonClient` (receive) — behind the OSX guard.

Exact Klak member names/signatures verified against the installed package versions at
implement time (the backend is the single place they appear).

### `ExternalTextureReceiver.cs` (replaces `ExternalInputProvider`)

- **Inspector:** `ShareProtocol protocol`, `string streamName`, `ShareResources resources`;
  retains debug video-input fallback (`m_DebugUseVideoInput`, clip, loop, speed) + the
  gaussian-blur compute path.
- Holds one `ITextureReceiverBackend` (rebuilt on protocol/name change). Each
  `UpdateInput()`: if debug video on → existing video path; else blit
  `backend.Received` into `_outputTexture` (same as current Spout path, protocol-agnostic).
- **Unchanged public surface:** `OutputTexture`, `Initialize()`, `UpdateInput()`,
  `Release()`. `SimulationManager.externalInput` retypes `ExternalInputProvider` →
  `ExternalTextureReceiver`.

### `ExternalTextureSender.cs` (new)

```csharp
public enum SendSource { CompositeOutput, SimOutput, BiomeLayer }

[Serializable] public class SendStream
{
    public bool enabled = true;
    public SendSource source = SendSource.CompositeOutput;
    public int index = 0;                 // sim index or biome channel index
    public ShareProtocol protocol = ShareProtocol.NDI;
    public string streamName = "";        // blank → auto default
    [Range(0.05f, 1f)] public float resolutionScale = 1f;
}
```

- **Inspector:** `SimulationManager simManager`, `ShareResources resources`,
  `List<SendStream> streams`, `[Button] Rebuild Streams`.
- **Lifecycle:** `Rebuild()` disposes existing backends and creates one
  `ITextureSenderBackend` per enabled stream (each on its own child GameObject named
  after the stream). Rebuild on enable and when the list/protocol changes (button).
- **`LateUpdate()`** (after `SimulationManager.Update()` has stepped/rendered): per
  enabled stream → resolve source 2D texture → optional downscale → `backend.SetSource`.

**Source resolution:**
- `CompositeOutput` → `simManager.CompositeOutputTexture`
- `SimOutput` → `simManager.simulations[index]?.GetOutputTexture()`
- `BiomeLayer` → `simManager.biome.RenderChannelTo(index, ownedRT)` (extract only runs
  for enabled biome-layer streams)

**Resolution scale:**
- `scale == 1f` and source is direct (composite/sim) → pass source texture directly.
- `scale < 1f` → own a `RenderTexture` at `ceil(srcW*scale) × ceil(srcH*scale)`,
  `Graphics.Blit(source, scaledRT)`, pass `scaledRT`.
- `BiomeLayer` → owned RT sized at scaled dims; `RenderChannelTo` writes directly at
  that resolution (debug kernel dispatches at dst size).

**Default stream names** (when `streamName` blank):
- Composite → `EoC/Composite`
- SimOutput → `EoC/<SimName>` (e.g. `EoC/Physarum`)
- BiomeLayer → `EoC/<ChannelName>` (e.g. `EoC/Nutrient`)

### Host edits (small)

- `Biome.RenderChannelTo(int channel, RenderTexture dst)` — public; binds
  `fieldReadArray` + channel to `renderDebugKernel`, dispatches into `dst`. (Refactor
  the private per-channel render in `RenderDebug()` to call this.)
- `SimulationManager.CompositeOutputTexture { get; }` — public getter for
  `compositeOutTex`.
- `SimulationManager.externalInput` field type → `ExternalTextureReceiver`.

## Error handling

- Protocol unavailable on platform (`IsAvailable == false`) → backend null, stream
  skipped, warn once (`"Spout selected on macOS — no-op"`).
- Missing Resources asset for the chosen protocol → warn once, skip stream.
- Null source (sim not reset / composite not created) → skip that stream's frame.
- `index` out of range (sim/channel) → skip + warn once.

## Files

| File | Change |
|---|---|
| `src/components/network/ExternalTextureShare.cs` | new — Klak backend + platform gate |
| `src/components/network/ExternalTextureReceiver.cs` | rename/rewrite of `ExternalInputProvider.cs` |
| `src/components/network/ExternalTextureSender.cs` | new — send streams |
| `src/components/core/Biome.cs` | add `RenderChannelTo`; refactor `RenderDebug` to use it |
| `src/components/core/SimulationManager.cs` | `CompositeOutputTexture` getter; retype `externalInput` |
| `Packages/manifest.json` | add `jp.keijiro.klak.syphon` |

`ExternalInputProvider.cs` (currently moved to `network/`, uncommitted) is replaced by
`ExternalTextureReceiver.cs`.

## Testing / verification

No test-runner harness; verify in-editor:
- Enter Play; add `ExternalTextureSender`, assign `simManager`, add a Composite stream
  over NDI → confirm `EoC/Composite` appears in a NDI monitor / TouchDesigner.
- Add a SimOutput and a BiomeLayer stream → confirm both publish; biome layer shows the
  field channel; extract only runs while enabled.
- Set `resolutionScale = 0.5` → confirm the received stream is half-res.
- On macOS: Syphon stream appears in a Syphon viewer; a Spout stream warns no-op.
- `ExternalTextureReceiver`: receive a known source (or NDI test pattern) into a sim's
  influence; confirm the debug video fallback still works.
- `ExternalTextureShare.IsAvailable` platform mapping is the one trivially-checkable
  pure function.

## Out of scope (YAGNI)

- Audio over NDI; multiple receive streams; format/colorspace conversion options;
  runtime stream discovery UI.
